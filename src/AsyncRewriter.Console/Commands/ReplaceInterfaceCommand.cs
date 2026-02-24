using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Replaces all references to a source interface with a target interface throughout
/// a solution.  Both interfaces are identified by their fully-qualified names.
///
/// Example:
/// <code>
///   async-rewriter replace-interface MySolution.sln \
///       --from MyProject.Data.IUserRepository \
///       --to   MyProject.Data.Async.IAsyncUserRepository
/// </code>
/// </summary>
public class ReplaceInterfaceCommand : Command
{
    private readonly ILogger<ReplaceInterfaceCommand> _logger;

    public ReplaceInterfaceCommand(ILogger<ReplaceInterfaceCommand> logger)
        : base("replace-interface",
            "Replace all references to one interface with another across a solution")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to scan");

        var fromOption = new Option<string>(
            aliases: ["--from", "-f"],
            description: "Fully-qualified name of the interface to replace (e.g. MyProject.Data.IRepository)")
        {
            IsRequired = true
        };

        var toOption = new Option<string>(
            aliases: ["--to", "-t"],
            description: "Fully-qualified name of the replacement interface (e.g. MyProject.Data.IAsyncRepository)")
        {
            IsRequired = true
        };

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be modified without writing changes",
            getDefaultValue: () => false);

        AddArgument(solutionArgument);
        AddOption(fromOption);
        AddOption(toOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, solutionArgument, fromOption, toOption, dryRunOption);
    }

    private async Task ExecuteAsync(string solutionPath, string fromInterface, string toInterface, bool dryRun)
    {
        if (!File.Exists(solutionPath))
        {
            _logger.LogError("Solution file not found: {SolutionPath}", solutionPath);
            return;
        }

        _logger.LogInformation(
            "Replacing {From} → {To} in {SolutionPath}",
            fromInterface, toInterface, solutionPath);

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
            "Solution loaded ({ProjectCount} projects). Scanning...",
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

            // Resolve the old interface symbol once per compilation.
            var oldSymbol = compilation.GetTypeByMetadataName(fromInterface);
            if (oldSymbol == null)
            {
                _logger.LogDebug(
                    "Interface {Interface} not found in project {Project}, skipping.",
                    fromInterface, project.Name);
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

                var rewriter = new InterfaceReplaceRewriter(semanticModel, oldSymbol, toInterface);
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
