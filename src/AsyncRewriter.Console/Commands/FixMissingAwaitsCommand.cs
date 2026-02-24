using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Scans a solution for invocations that return <c>Task</c> or <c>Task&lt;T&gt;</c>
/// (or their <c>ValueTask</c> equivalents) but are not awaited, and inserts the missing
/// <c>await</c> keywords.  The enclosing method or lambda has <c>async</c> added when
/// it is not already present.
///
/// This command uses a full Roslyn semantic model to resolve return types, so it is
/// accurate regardless of naming conventions.
///
/// Example:
/// <code>
///   async-rewriter fix-missing-awaits MySolution.sln
///   async-rewriter fix-missing-awaits MySolution.sln --dry-run
/// </code>
/// </summary>
public class FixMissingAwaitsCommand : Command
{
    private readonly ILogger<FixMissingAwaitsCommand> _logger;

    public FixMissingAwaitsCommand(ILogger<FixMissingAwaitsCommand> logger)
        : base("fix-missing-awaits",
            "Add missing await keywords to Task/ValueTask-returning invocations throughout a solution")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to scan");

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be modified without writing changes",
            getDefaultValue: () => false);

        AddArgument(solutionArgument);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, solutionArgument, dryRunOption);
    }

    private async Task ExecuteAsync(string solutionPath, bool dryRun)
    {
        if (!File.Exists(solutionPath))
        {
            _logger.LogError("Solution file not found: {SolutionPath}", solutionPath);
            return;
        }

        _logger.LogInformation("Opening solution: {SolutionPath}", solutionPath);

        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch
        {
            // Already registered — ignore.
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);

        _logger.LogInformation(
            "Solution loaded ({ProjectCount} projects). Scanning for missing awaits...",
            solution.Projects.Count());

        var modifiedFiles = new List<(string FilePath, string NewContent)>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {ProjectName}, skipping.", project.Name);
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                {
                    continue;
                }

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null)
                {
                    continue;
                }

                var root = await syntaxTree.GetRootAsync();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                var rewriter = new MissingAwaitRewriter(semanticModel);
                var newRoot = rewriter.Visit(root);

                if (!rewriter.AnyRewritten)
                {
                    continue;
                }

                modifiedFiles.Add((document.FilePath, newRoot!.ToFullString()));
            }
        }

        if (modifiedFiles.Count == 0)
        {
            _logger.LogInformation("No missing awaits found.");
            return;
        }

        foreach (var (filePath, newContent) in modifiedFiles)
        {
            if (dryRun)
            {
                _logger.LogInformation("[dry-run] Would modify: {FilePath}", filePath);
            }
            else
            {
                await File.WriteAllTextAsync(filePath, newContent);
                _logger.LogInformation("Modified: {FilePath}", filePath);
            }
        }

        _logger.LogInformation(
            "{Action} {Count} file(s).",
            dryRun ? "Would modify" : "Modified",
            modifiedFiles.Count);
    }
}
