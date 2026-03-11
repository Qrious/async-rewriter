using System.Collections.Concurrent;
using System.CommandLine;
using System.Text.RegularExpressions;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Uses GitHub Copilot to methodically rewrite synchronous methods to async, processing
/// them in topological order (deepest callees first) so that each method's prompt can
/// include the already-refactored signatures of its callees.
/// </summary>
public class CopilotDrivenRefactorCommand : Command
{
    private readonly ILogger<CopilotDrivenRefactorCommand> _logger;

    public CopilotDrivenRefactorCommand(ILogger<CopilotDrivenRefactorCommand> logger)
        : base("copilot-refactor", "Use GitHub Copilot to refactor sync methods to async based on a flooded call graph")
    {
        _logger = logger;

        var callGraphIdArg = new Argument<string>("callgraph", "The id of the flooded call graph");
        var neo4jUriOption = new Option<string>(
            aliases: ["--uri", "-u"],
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var neo4jUserOption = new Option<string>(
            aliases: ["--neo4j-user"],
            description: "Neo4j username",
            getDefaultValue: () => "");
        var neo4jPasswordOption = new Option<string>(
            aliases: ["--neo4j-password"],
            description: "Neo4j password",
            getDefaultValue: () => "");
        var modelOption = new Option<string>(
            aliases: ["--model", "-m"],
            description: "Copilot model to use",
            getDefaultValue: () => "gpt-5-mini");
        var githubTokenOption = new Option<string?>(
            aliases: ["--github-token"],
            description: "GitHub token for Copilot authentication (uses CLI auth if omitted)");
        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print what would be refactored without writing changes",
            getDefaultValue: () => false);
        var parallelismOption = new Option<int>(
            aliases: ["--parallelism", "-p"],
            description: "Maximum number of concurrent Copilot requests (default: 1)",
            getDefaultValue: () => 1);
        var sessionFileOption = new Option<string?>(
            aliases: ["--session", "-s"],
            description: "Path to a session file for resume support. Progress is saved after each method; re-running with the same file skips already-completed methods.");

        AddArgument(callGraphIdArg);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(modelOption);
        AddOption(githubTokenOption);
        AddOption(dryRunOption);
        AddOption(parallelismOption);
        AddOption(sessionFileOption);

        this.SetHandler(async ctx =>
        {
            await ExecuteAsync(
                ctx.ParseResult.GetValueForArgument(callGraphIdArg),
                ctx.ParseResult.GetValueForOption(neo4jUriOption)!,
                ctx.ParseResult.GetValueForOption(neo4jUserOption)!,
                ctx.ParseResult.GetValueForOption(neo4jPasswordOption)!,
                ctx.ParseResult.GetValueForOption(modelOption)!,
                ctx.ParseResult.GetValueForOption(githubTokenOption),
                ctx.ParseResult.GetValueForOption(dryRunOption),
                ctx.ParseResult.GetValueForOption(parallelismOption),
                ctx.ParseResult.GetValueForOption(sessionFileOption));
        });
    }

    private async Task ExecuteAsync(
        string callGraphId,
        string neo4jUri, string neo4jUser, string neo4jPassword,
        string model, string? githubToken, bool dryRun, int parallelism, string? sessionFile)
    {
        var neo4jCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
        _logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4jCredentials.Url);

        await using var repository = new Neo4jCallGraphRepository(neo4jCredentials, _logger);

        _logger.LogInformation("Loading flooded call graph: {CallGraphId}", callGraphId);
        var callGraph = await repository.Load<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraphId, default);

        // Collect flooded methods (those with non-empty OriginalReturnType from the flooding pass).
        // Sort ascending by depth so leaves (deepest callees) are processed first — their already-
        // refactored signatures are then available as context when we process their callers.
        var floodedEntries = callGraph.MethodMetadata
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.First.OriginalReturnType))
            .Select(kvp =>
            {
                callGraph.Methods.TryGetValue(kvp.Key, out var method);

                return (MethodId: kvp.Key, Metadata: kvp.Value.First, Method: method);
            })
            .Where(x => x.Method != null && !string.IsNullOrEmpty(x.Method.FilePath))
            .OrderBy(x => x.Metadata.Depth)
            .ToList();

        _logger.LogInformation("Found {Count} flooded methods to refactor", floodedEntries.Count);

        // ── Session (resume support) ────────────────────────────────────────
        CopilotRefactorSession? session = null;

        if (sessionFile != null)
        {
            session = await CopilotRefactorSession.OpenOrCreateAsync(sessionFile, callGraphId);
            var alreadyDone = floodedEntries.Count(e => session.IsCompleted(e.MethodId));

            if (alreadyDone > 0)
            {
                _logger.LogInformation("Resuming session: {Done}/{Total} methods already completed, skipping them.",
                    alreadyDone, floodedEntries.Count);
            }
        }

        var floodedMethodIds = new HashSet<string>(floodedEntries.Select(x => x.MethodId));

        var clientOptions = githubToken != null
            ? new CopilotClientOptions
            {
                GitHubToken = githubToken
            }
            : null;

        await using var client = new CopilotClient(clientOptions);
        await client.StartAsync();

        if (dryRun)
        {
            foreach (var fileGroup in floodedEntries.GroupBy(x => x.Method!.FilePath))
            {
                _logger.LogInformation("[dry-run] Would modify: {FilePath} ({Count} method(s))",
                    fileGroup.Key, fileGroup.Count());
            }

            return;
        }

        _logger.LogInformation("Processing with parallelism={Parallelism}", parallelism);

        int totalRefactored = 0;
        var modifiedFiles = new ConcurrentDictionary<string, bool>();

        // Group entries by file so that all methods in a file are written in a single pass.
        var fileGroups = floodedEntries
            .Where(e => e.Method!.FilePath != "external")
            .GroupBy(e => e.Method!.FilePath)
            .ToDictionary(e => e.Key, e => e.ToList());

        _logger.LogInformation("Processing {FileCount} file(s) file-by-file...", fileGroups.Count);

        // Process files sequentially (or with bounded parallelism across files).
        int fileCounter = 0;

        foreach (var fileGroup in fileGroups)
        {
            var filePath = fileGroup.Key;

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found, skipping: {FilePath}", filePath);

                continue;
            }

            // ── Fan out Copilot calls for all methods in this file ──────
            var refactoredMap = new ConcurrentDictionary<string, string>(); // originalSource → refactoredSource
            var completedIds = new ConcurrentBag<string>();

            var sourceCode = await File.ReadAllLinesAsync(filePath);

            _logger.LogInformation("[{DateTime}] - Refactoring ({Current}/{Total}) files, processing {FilePath} with {MethodCount} method(s)...",
                DateTime.Now, fileCounter, fileGroups.Count, filePath, fileGroup.Value.Count);
            await Parallel.ForEachAsync(
                fileGroup.Value,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = parallelism
                },
                ProcessMethod(model, session, sourceCode, callGraph, floodedMethodIds, client, refactoredMap, completedIds));

            if (refactoredMap.IsEmpty)
            {
                fileCounter++;
                totalRefactored += fileGroup.Value.Count;

                continue;
            }

            // ── Apply all replacements in a single file write ──────────
            try
            {
                int replacedCount = 0;
                var sourceText = string.Join(Environment.NewLine, sourceCode);

                foreach (var (originalSource, refactoredSource) in refactoredMap)
                {
                    sourceText = sourceText.Replace(originalSource, refactoredSource);
                    replacedCount++;
                }

                if (replacedCount == 0)
                {
                    _logger.LogWarning(
                        "No replacements applied in {FilePath}. Skipping file write to avoid data loss.",
                        filePath);

                    continue;
                }

                await File.WriteAllTextAsync(filePath, sourceText);
                modifiedFiles[filePath] = true;
                Interlocked.Add(ref totalRefactored, replacedCount);

                _logger.LogTrace("Wrote {Count} refactored method(s) to {FilePath}",
                    refactoredMap.Count, filePath);

                if (session != null)
                {
                    foreach (var methodId in completedIds)
                    {
                        await session.MarkCompletedAsync(methodId);
                    }
                }

                _logger.LogInformation("Completed {Count} refactored method(s) to {FilePath}", replacedCount, filePath);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error writing refactored code to {FilePath}", filePath);
            }
        }

        fileCounter++;
        _logger.LogInformation("Copilot refactored {Count}/{Total} methods across {FileCounter}/{FileCount} file(s).",
            totalRefactored, floodedEntries.Count, fileCounter, modifiedFiles.Count);
    }

    private Func<(string MethodId, FloodingMethodMetadata Metadata, IMethodNode? Method), CancellationToken, ValueTask> ProcessMethod(string model, CopilotRefactorSession? session,
        string[] sourceCode,
        ICallGraphWithMetadata<CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>, EmptyGraphMetadata,
            EmptyGraphMetadata, EmptyGraphMetadata> callGraph, HashSet<string> floodedMethodIds, CopilotClient client, ConcurrentDictionary<string, string> refactoredMap,
        ConcurrentBag<string> completedIds)
    {
        return async (entry, _) =>
        {
            var (methodId, metadata, method) = entry;

            if (session != null && session.IsCompleted(methodId))
            {
                _logger.LogTrace("Skipping already-completed method {MethodId}", methodId);

                return;
            }

            var methodSource = await ReadMethodSourceAsync(method!, sourceCode);

            if (methodSource == null)
            {
                _logger.LogWarning("Could not read source for {Method} in {File}", method!.Name, method.FilePath);

                return;
            }

            var newReturnType = ComputeNewReturnType(metadata.OriginalReturnType);
            var newSignature = BuildNewSignature(method!, newReturnType);
            var calleeContext = BuildCalleeContext(callGraph, methodId, floodedMethodIds);
            var callsEntityFramework = callGraph.GetCallees(methodId).Any(m =>
                callGraph.MethodMetadata.TryGetValue(m.Id, out var meta) && meta.Third.IsEntityFrameworkCaller);
            bool isInterfaceMember = method!.IsInterfaceMethod;
            var prompt = BuildPrompt(methodSource, newSignature, calleeContext, isInterfaceMember, callsEntityFramework);

            _logger.LogTrace("Refactoring {Type}.{Method} (depth {Depth})...",
                method.ContainingType, method.Name, metadata.Depth);

            var refactoredRaw = await CallCopilotAsync(client, model, prompt);
            var refactored = ExtractCodeBlock(refactoredRaw);

            if (refactored == null)
            {
                _logger.LogWarning("No code block returned for {Method}; skipping", method.Name);

                return;
            }

            if (!refactored.Contains("Task") && !method.Name.Contains("b__"))
            {
                _logger.LogWarning(
                    "Refactored code for {Method} does not contain 'Task' — looks like the signature wasn't updated correctly. Skipping to avoid data loss.\nRefactored code:\n{Code}",
                    method.Name, refactored);

                return;
            }

            refactoredMap[methodSource] = refactored;
            completedIds.Add(methodId);
        };
    }

    // ── Copilot interaction ─────────────────────────────────────────────────

    private async Task<string?> CallCopilotAsync(CopilotClient client, string model, string prompt)
    {
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        });

        string? response = null;
        var tcs = new TaskCompletionSource<string?>();

        session.On(evt =>
        {
            if (evt is AssistantMessageEvent msg)
            {
                response = msg.Data.Content;
            }
            else if (evt is SessionIdleEvent)
            {
                tcs.TrySetResult(response);
            }
            else if (evt is SessionErrorEvent err)
            {
                tcs.TrySetException(new Exception($"Copilot session error: {err}"));
            }
        });

        await session.SendAsync(new MessageOptions
        {
            Prompt = prompt
        });

        return await tcs.Task;
    }

    private static string? ExtractCodeBlock(string? response)
    {
        if (response == null)
        {
            return null;
        }

        var match = Regex.Match(response, @"```(?:csharp|cs)?\s*\n(.*?)\n```",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Source extraction ───────────────────────────────────────────────────

    private async Task<string?> ReadMethodSourceAsync(IMethodNode method, string[] sourceCode)
    {
        try
        {
            int start = Math.Max(0, method.StartLine);
            int end = Math.Min(sourceCode.Length - 1, method.EndLine);

            if (start > end)
            {
                return null;
            }

            // The startcode is actually the method header, but it could be prepended by attributes or comments, so we try to include those as well by looking upwards until we find a non-empty line that doesn't start with [ or //.
            while (start > 0)
            {
                var line = sourceCode[start - 1].Trim();

                if (string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("//"))
                {
                    start--;
                }
                else
                {
                    break;
                }
            }

            return string.Join(Environment.NewLine, sourceCode[(start - 1)..end]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {File}", method.FilePath);

            return null;
        }
    }

    // ── Signature and context helpers ───────────────────────────────────────

    private static string ComputeNewReturnType(string originalReturnType) =>
        originalReturnType.Trim() == "void" ? "Task" : $"Task<{originalReturnType}>";

    private static string BuildNewSignature(IMethodNode method, string newReturnType)
    {
        var newName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name
            : method.Name + "Async";

        var parameters = method.Parameters
            .Select(p => p.ToString())
            .ToList();

        bool hasCancellationToken = parameters.Any(p =>
            p.Contains("CancellationToken", StringComparison.OrdinalIgnoreCase));

        if (!hasCancellationToken)
        {
            parameters.Add("CancellationToken cancellationToken = default");
        }

        if (method.IsInterfaceMethod)
        {
            return $"public {newReturnType} {newName}({string.Join(", ", parameters)});";
        }

        if (method.Name.Contains("b__"))
        {
            return $"inline lambda with return type {newReturnType} and parameters ({string.Join(", ", parameters)})";
        }

        if (method.Name.Contains(">."))
        {
            return $"Inline func with return type {newReturnType} and parameters ({string.Join(", ", parameters)})";
        }

        return $"public async {newReturnType} {newName}({string.Join(", ", parameters)})";
    }

    private static string BuildCalleeContext(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        string methodId,
        HashSet<string> floodedMethodIds)
    {
        var calleeIds = callGraph.Calls
            .Where(c => c.CallerId == methodId && floodedMethodIds.Contains(c.CalleeId))
            .Select(c => c.CalleeId)
            .Distinct()
            .ToList();

        if (calleeIds.Count == 0)
        {
            return "(none — all callees remain synchronous)";
        }

        var lines = new List<string>();

        foreach (var calleeId in calleeIds)
        {
            if (!callGraph.Methods.TryGetValue(calleeId, out var callee))
            {
                continue;
            }

            if (!callGraph.MethodMetadata.TryGetValue(calleeId, out var calleeMeta))
            {
                continue;
            }

            var origReturn = calleeMeta.First.OriginalReturnType;

            if (string.IsNullOrEmpty(origReturn))
            {
                continue;
            }

            var newReturn = ComputeNewReturnType(origReturn);
            var newName = callee.Name.EndsWith("Async") ? callee.Name : callee.Name + "Async";

            if (callee.Name == "AsEnumerable" && calleeMeta.Third.IsEntityFrameworkCaller)
            {
                newName = "ToListAsync";
            }

            if (callee.Name == "AsReadOnlyList" && calleeMeta.Third.IsEntityFrameworkCaller)
            {
                newName = "ToListAsync";
            }

            var parms = callee.Parameters.Select(p => p.ToString()).ToList();

            if (callee.FilePath != "external" && !parms.Any(p => p.Contains("CancellationToken", StringComparison.OrdinalIgnoreCase)))
            {
                parms.Add("CancellationToken cancellationToken = default");
            }

            lines.Add($"- {callee.Name}({string.Join(", ", callee.Parameters.Select(p => p.ToString()))}) " +
                      $"→ async {newReturn} {newName}({string.Join(", ", parms)})");
        }

        return lines.Count > 0
            ? string.Join("\n", lines)
            : "(none — all callees remain synchronous)";
    }

    private static string BuildPrompt(string methodSource, string newSignature, string calleeContext, bool isInterfaceMember = false, bool callsEntityFramework = false)
    {
        // Use $$""" so single { } are literal; interpolations use {{ }}.
        return $$"""
                 You are a C# async refactoring assistant. Transform the target method below.
                 Rules:
                 - Return ONLY the refactored method (no class wrapper, no using directives).
                 - Wrap the output in a ```csharp code block.
                 - Preserve all existing logic and error handling exactly.
                 - Make sure outputted methods are always indented with 4 spaces per level and use consistent formatting.
                 - Maintain the original indentation level of the method in the output (e.g. if the original method is indented by 8 spaces, the output should also be indented by 8 spaces).
                 - Add 'await' to every call listed under "Callee methods that became async".
                 - Pass 'cancellationToken' through to every async callee that accepts a CancellationToken.
                 - If a method call is already awaited, do not add an extra 'await' (e.g. 'return await ...' should not become 'return await await ...').
                 - 'Unwrap' all funcs wrapped in a of AsyncHelper.RunTaskSynchronously(Func<Task<T>>) or AsyncHelper.RunTaskSynchronously(Func<Task>), and instead directly execute the func.
                 - If no calls in the method have to be awaited, don't mark the method as async — just return Task or Task<T> directly without using async/await, and use Task.FromResult or Task.CompletedTask when there are no async calls at all.
                 {{(isInterfaceMember
                     ? "- This is an interface member: output ONLY the new signature followed by a semicolon — no method body, no braces. Don't add 'async' to the signature since interfaces can't have implementation. \n Always add an optional cancellationToken to every method. In case the method has a params argument, add the cancellationToken before the params argument."
                     : "- Return the Task directly (no async/await) when there is a single async expression and the result is not used further.\n - Use Task.CompletedTask or Task.FromResult<T>() when the method has no async calls.")}}
                 {{(callsEntityFramework
                         ? "- The method calls Entity Framework sync methods, use the EF async equivalents (e.g. ToListAsync instead of AsEnumerable, AsReadonlyList or ToList) and pass the cancellationToken to them.\n"
                         : ""
                     )}}

                 ## Target method (refactor this):
                 ```csharp
                 {{methodSource}}
                 ```

                 ## New signature to produce:
                 {{newSignature}}

                 ## Callee methods that became async (add 'await' when calling these):
                 {{calleeContext}}

                 ## Example:
                 Before:
                 ```csharp
                 public User GetUser(int id)
                 {
                     var record = _repo.FindRecord(id);
                     if (record == null) throw new NotFoundException();
                     _cache.Store(record);
                     return record.ToUser();
                 }
                 ```
                 After:
                 ```csharp
                 public async Task<User> GetUserAsync(int id, CancellationToken cancellationToken = default)
                 {
                     var record = await _repo.FindRecordAsync(id, cancellationToken);
                     if (record == null) throw new NotFoundException();
                     await _cache.StoreAsync(record, cancellationToken);
                     return record.ToUser();
                 }
                 ```

                 Before:
                 ```csharp
                 public Entities.Connectivity.Stt.SttInstallbaseResult GetSttInstallbase(Entities.Connectivity.Stt.SttInstallbaseRequest request)
                 {
                     Check.ArgumentIsNotNull(request);

                     try
                     {
                         var result = AsyncHelper.RunTaskSynchronously(() => _client.GetSttInstallBaseV2Async(request.Customer, request.Postcode, request.HouseNumber, request.Extension));
                         return _resultMapper.Map(result);
                     }
                     catch (ApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                     {
                         return null;
                     }
                 }
                 ```

                 After:
                 ```csharp
                 public async TasK<Entities.Connectivity.Stt.SttInstallbaseResult> GetSttInstallbase(Entities.Connectivity.Stt.SttInstallbaseRequest request)
                 {
                     Check.ArgumentIsNotNull(request);

                     try
                     {
                         var result = _client.GetSttInstallBaseV2Async(request.Customer, request.Postcode, request.HouseNumber, request.Extension);
                         return _resultMapper.Map(result);
                     }
                     catch (ApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
                     {
                         return null;
                     }
                 }
                 ```

                 Before:
                 ```csharp
                 public IReadOnlyList<MobileSubscriptionOrderAvailableForVaMo> GetAvailableMobileSubscriptionOrdersForVaMo(int customerId)
                 {
                     return _context.MobileSubscriptionOrdersAvailableForVaMo.Where(x => x.CustomerId == customerId).ToList().AsReadOnly();
                 }
                 ```

                 After:
                 ```csharp
                 public async Task<IReadOnlyList<MobileSubscriptionOrderAvailableForVaMo>> GetAvailableMobileSubscriptionOrdersForVaMo(int customerId)
                 {
                     return await _context.MobileSubscriptionOrdersAvailableForVaMo.Where(x => x.CustomerId == customerId).ToListAsync();
                 }
                 ```

                 Now refactor the target method. Return only the refactored method in a ```csharp block.
                 """;
    }
}