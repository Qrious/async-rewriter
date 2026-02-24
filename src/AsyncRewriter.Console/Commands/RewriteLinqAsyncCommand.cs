using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Scans a solution for LINQ method calls whose lambda arguments are async
/// (i.e. they carry the <c>async</c> keyword, which happens after the async-rewriter
/// has flooded those lambdas) and rewrites them to their async counterparts.
///
/// The async overloads (e.g. <c>SelectAsync</c>, <c>WhereAsync</c>) are assumed to live
/// in the namespace provided via <c>--async-linq-namespace</c>.  A <c>using</c> directive
/// for that namespace is added to every file that is modified.
///
/// Example:
/// <code>
///   async-rewriter rewrite-linq-async MySolution.sln --async-linq-namespace MyProject.Linq
/// </code>
/// </summary>
public class RewriteLinqAsyncCommand : Command
{
    private readonly ILogger<RewriteLinqAsyncCommand> _logger;

    public RewriteLinqAsyncCommand(ILogger<RewriteLinqAsyncCommand> logger)
        : base("rewrite-linq-async",
            "Rewrite LINQ calls whose lambda arguments are async to use async overloads " +
            "from the specified namespace (e.g. SelectAsync, WhereAsync)")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to scan");

        var namespaceOption = new Option<string>(
            aliases: ["--async-linq-namespace", "-ns"],
            description: "Namespace that contains the async LINQ extension methods (e.g. MyProject.Linq)")
        {
            IsRequired = true
        };

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be modified without writing changes",
            getDefaultValue: () => false);

        AddArgument(solutionArgument);
        AddOption(namespaceOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, solutionArgument, namespaceOption, dryRunOption);
    }

    private async Task ExecuteAsync(string solutionPath, string asyncLinqNamespace, bool dryRun)
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
            "Solution loaded ({ProjectCount} projects). Scanning for async LINQ calls...",
            solution.Projects.Count());

        var modifiedFiles = new List<(string FilePath, string NewContent)>();

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync();
                if (root == null)
                {
                    continue;
                }

                var rewriter = new LinqAsyncOverloadRewriter(asyncLinqNamespace);
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
            _logger.LogInformation("No files needed modification.");
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
