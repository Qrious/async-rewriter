using System;
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
            getDefaultValue: () => "claude-sonnet-4-5");
        var githubTokenOption = new Option<string?>(
            aliases: ["--github-token"],
            description: "GitHub token for Copilot authentication (uses CLI auth if omitted)");
        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print what would be refactored without writing changes",
            getDefaultValue: () => false);

        AddArgument(callGraphIdArg);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(modelOption);
        AddOption(githubTokenOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync,
            callGraphIdArg,
            neo4jUriOption, neo4jUserOption, neo4jPasswordOption,
            modelOption, githubTokenOption, dryRunOption);
    }

    private async Task ExecuteAsync(
        string callGraphId,
        string neo4jUri, string neo4jUser, string neo4jPassword,
        string model, string? githubToken, bool dryRun)
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

        // Phase 1: For each method in topological order, call Copilot and collect refactored text.
        var clientOptions = githubToken != null
            ? new CopilotClientOptions { GitHubToken = githubToken }
            : null;

        await using var client = new CopilotClient(clientOptions);
        await client.StartAsync();

        // methodId → refactored method source text
        var refactoredByMethodId = new Dictionary<string, string>();

        foreach (var (methodId, metadata, method) in floodedEntries)
        {
            var methodSource = ReadMethodSource(method!);
            if (methodSource == null)
            {
                _logger.LogWarning("Could not read source for {Method} in {File}", method!.Name, method.FilePath);
                continue;
            }

            var newReturnType = ComputeNewReturnType(metadata.OriginalReturnType);
            var newSignature = BuildNewSignature(method!, newReturnType);
            var calleeContext = BuildCalleeContext(callGraph, methodId, floodedMethodIds);

            var prompt = BuildPrompt(methodSource, newSignature, calleeContext);

            _logger.LogInformation("Refactoring {Type}.{Method} (depth {Depth})...",
                method!.ContainingType, method.Name, metadata.Depth);

            var refactoredRaw = await CallCopilotAsync(client, model, prompt);
            var refactored = ExtractCodeBlock(refactoredRaw);

            if (refactored == null)
            {
                _logger.LogWarning("No code block returned for {Method}; skipping", method.Name);
                continue;
            }

            refactoredByMethodId[methodId] = refactored;
        }

        _logger.LogInformation("Copilot refactored {Count}/{Total} methods",
            refactoredByMethodId.Count, floodedEntries.Count);

        // Phase 2: Apply changes per file, bottom-to-top within each file so earlier
        // line numbers remain stable as we replace later methods first.
        var byFile = refactoredByMethodId
            .Select(kvp =>
            {
                callGraph.Methods.TryGetValue(kvp.Key, out var method);
                return (MethodId: kvp.Key, RefactoredText: kvp.Value, Method: method!);
            })
            .Where(x => x.Method != null)
            .GroupBy(x => x.Method.FilePath)
            .ToList();

        int filesModified = 0;
        foreach (var fileGroup in byFile)
        {
            var filePath = fileGroup.Key;
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found, skipping: {FilePath}", filePath);
                continue;
            }

            if (dryRun)
            {
                _logger.LogInformation("[dry-run] Would modify: {FilePath} ({Count} method(s))",
                    filePath, fileGroup.Count());
                continue;
            }

            var fileLines = (await File.ReadAllLinesAsync(filePath)).ToList();

            // Process from bottom of file upward so line indices stay valid.
            var replacements = fileGroup
                .OrderByDescending(x => x.Method.StartLine)
                .ToList();

            foreach (var (_, refactoredText, method) in replacements)
            {
                int startLine = method.StartLine; // 0-based inclusive
                int endLine = method.EndLine;     // 0-based inclusive

                if (startLine < 0 || endLine >= fileLines.Count || startLine > endLine)
                {
                    _logger.LogWarning("Line range [{Start},{End}] out of bounds for {Method} in {File}; skipping",
                        startLine, endLine, method.Name, filePath);
                    continue;
                }

                var newMethodLines = refactoredText
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .ToList();

                // Preserve the indentation of the original first line.
                var originalIndent = GetIndent(fileLines[startLine]);
                var refactoredIndent = GetIndent(newMethodLines.FirstOrDefault() ?? "");
                if (originalIndent != refactoredIndent)
                {
                    newMethodLines = ReindentLines(newMethodLines, refactoredIndent, originalIndent);
                }

                fileLines.RemoveRange(startLine, endLine - startLine + 1);
                fileLines.InsertRange(startLine, newMethodLines);
            }

            await File.WriteAllLinesAsync(filePath, fileLines);
            _logger.LogInformation("Modified: {FilePath}", filePath);
            filesModified++;
        }

        if (!dryRun)
        {
            _logger.LogInformation("Modified {FileCount} file(s).", filesModified);
        }
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

    private static string BuildPrompt(string methodSource, string newSignature, string calleeContext)
    {
        // Use $$""" so single { } are literal; interpolations use {{ }}.
        return $$"""
                You are a C# async refactoring assistant. Transform the target method below.
                Rules:
                - Return ONLY the refactored method body (no class wrapper, no using directives).
                - Wrap the output in a ```csharp code block.
                - Preserve all existing logic and error handling exactly.
                - Add 'await' to every call listed under "Callee methods that became async".
                - Pass 'cancellationToken' through to every async callee that accepts a CancellationToken.
                - Return the Task directly (no async/await) when there is a single async expression and the result is not used further.
                - Use Task.CompletedTask or Task.FromResult<T>() when the method has no async calls.

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

    // ── Indentation helpers ─────────────────────────────────────────────────

    private static string GetIndent(string line)
    {
        int i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    private static List<string> ReindentLines(List<string> lines, string fromIndent, string toIndent)
    {
        return lines.Select(line =>
        {
            if (line.StartsWith(fromIndent, StringComparison.Ordinal))
            {
                return toIndent + line[fromIndent.Length..];
            }

            return line;
        }).ToList();
    }
}
