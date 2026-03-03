using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
using GitHub.Copilot.SDK;
using Microsoft.CodeAnalysis.CSharp;
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

        AddArgument(callGraphIdArg);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(modelOption);
        AddOption(githubTokenOption);
        AddOption(dryRunOption);
        AddOption(parallelismOption);

        this.SetHandler(ExecuteAsync,
            callGraphIdArg,
            neo4jUriOption, neo4jUserOption, neo4jPasswordOption,
            modelOption, githubTokenOption, dryRunOption, parallelismOption);
    }

    private async Task ExecuteAsync(
        string callGraphId,
        string neo4jUri, string neo4jUser, string neo4jPassword,
        string model, string? githubToken, bool dryRun, int parallelism)
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

        var floodedMethodIds = new HashSet<string>(floodedEntries.Select(x => x.MethodId));

        var clientOptions = githubToken != null
            ? new CopilotClientOptions { GitHubToken = githubToken }
            : null;

        await using var client = new CopilotClient(clientOptions);
        await client.StartAsync();

        if (dryRun)
        {
            foreach (var fileGroup in floodedEntries.GroupBy(x => x.Method!.FilePath))
                _logger.LogInformation("[dry-run] Would modify: {FilePath} ({Count} method(s))",
                    fileGroup.Key, fileGroup.Count());
            return;
        }

        _logger.LogInformation("Processing with parallelism={Parallelism}", parallelism);

        // Per-file semaphores serialise the read-modify-write.
        var fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        var modifiedFiles = new ConcurrentDictionary<string, bool>();
        int totalRefactored = 0;

        await Parallel.ForEachAsync(
            floodedEntries,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (entry, _) =>
        {
            var (methodId, metadata, method) = entry;

            if (!File.Exists(method!.FilePath))
            {
                _logger.LogWarning("File not found, skipping: {FilePath}", method.FilePath);
                return;
            }

            var methodSource = ReadMethodSource(method);
            if (methodSource == null)
            {
                _logger.LogWarning("Could not read source for {Method} in {File}", method.Name, method.FilePath);
                return;
            }

            var newReturnType = ComputeNewReturnType(metadata.OriginalReturnType);
            var newSignature = BuildNewSignature(method, newReturnType);
            var calleeContext = BuildCalleeContext(callGraph, methodId, floodedMethodIds);
            bool isInterfaceMember = !methodSource.Contains('{');
            var prompt = BuildPrompt(methodSource, newSignature, calleeContext, isInterfaceMember);

            _logger.LogInformation("Refactoring {Type}.{Method} (depth {Depth})...",
                method.ContainingType, method.Name, metadata.Depth);

            // ── Copilot call ───────────────────────────────────────────────
            string? refactored;
            var refactoredRaw = await CallCopilotAsync(client, model, prompt);
            refactored = ExtractCodeBlock(refactoredRaw);

            if (refactored == null)
            {
                _logger.LogWarning("No code block returned for {Method}; skipping", method.Name);
                return;
            }

            // ── File write (per-file lock) ─────────────────────────────────
            var fileLock = fileLocks.GetOrAdd(method.FilePath, _ => new SemaphoreSlim(1, 1));
            bool acquiredImmediately = await fileLock.WaitAsync(0);
            if (!acquiredImmediately)
            {
                _logger.LogDebug("Lock contention on {FilePath} waiting for {Method}; queuing...",
                    method.FilePath, method.Name);
                await fileLock.WaitAsync();
            }
            try
            {
                var sourceText = await File.ReadAllTextAsync(method.FilePath);
                var syntaxTree = CSharpSyntaxTree.ParseText(sourceText);
                var root = await syntaxTree.GetRootAsync();

                var rewriter = new CopilotMethodReplacementRewriter(
                    new Dictionary<int, string> { [method.StartLine] = refactored });
                var newRoot = rewriter.Visit(root);

                await File.WriteAllTextAsync(method.FilePath, newRoot.ToFullString());
                modifiedFiles[method.FilePath] = true;
                Interlocked.Increment(ref totalRefactored);
            }
            finally
            {
                fileLock.Release();
            }
        });

        _logger.LogInformation("Copilot refactored {Count}/{Total} methods across {FileCount} file(s).",
            totalRefactored, floodedEntries.Count, modifiedFiles.Count);
    }

    // ── Copilot interaction ─────────────────────────────────────────────────

    private async Task<string?> CallCopilotAsync(CopilotClient client, string model, string prompt)
    {
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            OnPermissionRequest = PermissionHandler.ApproveAll
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

        await session.SendAsync(new MessageOptions { Prompt = prompt });
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
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // ── Source extraction ───────────────────────────────────────────────────

    private string? ReadMethodSource(AsyncRewriter.Core.Interfaces.IMethodNode method)
    {
        if (!File.Exists(method.FilePath))
        {
            return null;
        }

        try
        {
            var lines = File.ReadAllLines(method.FilePath);
            int start = Math.Max(0, method.StartLine);
            int end = Math.Min(lines.Length - 1, method.EndLine);
            if (start > end)
            {
                return null;
            }

            return string.Join(Environment.NewLine, lines[start..(end + 1)]);
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

    private static string BuildNewSignature(AsyncRewriter.Core.Interfaces.IMethodNode method, string newReturnType)
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
            return "(none — all callees remain synchronous)";

        var lines = new List<string>();
        foreach (var calleeId in calleeIds)
        {
            if (!callGraph.Methods.TryGetValue(calleeId, out var callee)) continue;
            if (!callGraph.MethodMetadata.TryGetValue(calleeId, out var calleeMeta)) continue;

            var origReturn = calleeMeta.First.OriginalReturnType;
            if (string.IsNullOrEmpty(origReturn)) continue;

            var newReturn = ComputeNewReturnType(origReturn);
            var newName = callee.Name.EndsWith("Async") ? callee.Name : callee.Name + "Async";
            var parms = callee.Parameters.Select(p => p.ToString()).ToList();
            if (!parms.Any(p => p.Contains("CancellationToken", StringComparison.OrdinalIgnoreCase)))
                parms.Add("CancellationToken cancellationToken = default");

            lines.Add($"- {callee.Name}({string.Join(", ", callee.Parameters.Select(p => p.ToString()))}) " +
                      $"→ async {newReturn} {newName}({string.Join(", ", parms)})");
        }

        return lines.Count > 0
            ? string.Join("\n", lines)
            : "(none — all callees remain synchronous)";
    }

    private static string BuildPrompt(string methodSource, string newSignature, string calleeContext, bool isInterfaceMember = false)
    {
        // Use $$""" so single { } are literal; interpolations use {{ }}.
        return $$"""
                You are a C# async refactoring assistant. Transform the target method below.
                Rules:
                - Return ONLY the refactored method (no class wrapper, no using directives).
                - Wrap the output in a ```csharp code block.
                - Preserve all existing logic and error handling exactly.
                - Add 'await' to every call listed under "Callee methods that became async".
                - Pass 'cancellationToken' through to every async callee that accepts a CancellationToken.
                {{(isInterfaceMember
                    ? "- This is an interface member: output ONLY the new signature followed by a semicolon — no method body, no braces."
                    : "- Return the Task directly (no async/await) when there is a single async expression and the result is not used further.\n                - Use Task.CompletedTask or Task.FromResult<T>() when the method has no async calls.")}}

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

                Now refactor the target method. Return only the refactored method in a ```csharp block.
                """;
    }

}
