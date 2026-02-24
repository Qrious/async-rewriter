using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Scans one or more projects in a solution for interfaces (optionally filtered by a
/// name postfix) and generates two new files per interface:
/// <list type="bullet">
///   <item>
///     <c>I{Name}Async.cs</c> — a mirror interface whose methods all return
///     <c>Task</c> / <c>Task&lt;T&gt;</c> and carry an <c>Async</c> suffix.
///   </item>
///   <item>
///     <c>{Name}AsyncAdapter.cs</c> — a class that implements the original interface
///     by delegating to the async counterpart via <c>AsyncHelper.RunTaskSynchronously</c>.
///   </item>
/// </list>
///
/// Generated files are written next to the source file that declares the interface.
///
/// By default both generated files are placed next to the source interface file.
/// Use <c>--async-interface-output-dir</c> and <c>--adapter-output-dir</c> to redirect
/// them to different directories (e.g. when interfaces and implementations live in
/// separate projects).
///
/// Example:
/// <code>
///   async-rewriter wrap-project MySolution.sln \
///       --async-helper-namespace MyProject.Threading \
///       --projects MyProject.Data \
///       --postfix Repository \
///       --async-interface-output-dir src/MyProject.Contracts/Async \
///       --adapter-output-dir src/MyProject.Data.Async \
///       --dry-run
/// </code>
/// </summary>
public class WrapProjectCommand : Command
{
    private readonly ILogger<WrapProjectCommand> _logger;

    public WrapProjectCommand(ILogger<WrapProjectCommand> logger)
        : base("wrap-project",
            "Generate async interface mirrors and synchronous adapter classes for interfaces in a solution")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to scan");

        var helperNamespaceOption = new Option<string>(
            aliases: ["--async-helper-namespace", "-ns"],
            description: "Namespace that contains AsyncHelper (e.g. MyProject.Threading)")
        {
            IsRequired = true
        };

        var projectsOption = new Option<string[]>(
            aliases: ["--projects", "-proj"],
            description: "Project names to include (e.g. MyProject.Data MyProject.Domain). " +
                         "When omitted, all projects in the solution are processed.")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var postfixOption = new Option<string?>(
            aliases: ["--postfix", "-p"],
            description: "Only process interfaces whose name ends with this postfix (e.g. Repository, Service). " +
                         "When omitted, all interfaces are processed.",
            getDefaultValue: () => null);

        var asyncInterfaceOutputDirOption = new Option<string?>(
            aliases: ["--async-interface-output-dir", "-id"],
            description: "Directory where async interface files (IFooAsync.cs) are written. " +
                         "Defaults to the same directory as the source interface file.",
            getDefaultValue: () => null);

        var adapterOutputDirOption = new Option<string?>(
            aliases: ["--adapter-output-dir", "-ad"],
            description: "Directory where adapter class files (FooAsyncAdapter.cs) are written. " +
                         "Defaults to the same directory as the source interface file.",
            getDefaultValue: () => null);

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be generated without writing them",
            getDefaultValue: () => false);

        AddArgument(solutionArgument);
        AddOption(helperNamespaceOption);
        AddOption(projectsOption);
        AddOption(postfixOption);
        AddOption(asyncInterfaceOutputDirOption);
        AddOption(adapterOutputDirOption);
        AddOption(dryRunOption);

        this.SetHandler(
            ExecuteAsync,
            solutionArgument, helperNamespaceOption, projectsOption, postfixOption,
            asyncInterfaceOutputDirOption, adapterOutputDirOption, dryRunOption);
    }

    private async Task ExecuteAsync(
        string solutionPath,
        string asyncHelperNamespace,
        string[] projects,
        string? postfix,
        string? asyncInterfaceOutputDir,
        string? adapterOutputDir,
        bool dryRun)
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

        var projectFilter = projects.Length > 0
            ? new HashSet<string>(projects, StringComparer.OrdinalIgnoreCase)
            : null;

        var matchingProjects = solution.Projects
            .Where(p => projectFilter == null || projectFilter.Contains(p.Name))
            .ToList();

        if (projectFilter != null && matchingProjects.Count == 0)
        {
            _logger.LogError(
                "None of the specified projects were found in the solution. " +
                "Available projects: {Projects}",
                string.Join(", ", solution.Projects.Select(p => p.Name)));
            return;
        }

        _logger.LogInformation(
            "Processing {Count} project(s): {Projects}",
            matchingProjects.Count,
            string.Join(", ", matchingProjects.Select(p => p.Name)));

        var generator = new AsyncWrapperGenerator();
        var generated = new List<(string FilePath, string Content, string Label)>();

        foreach (var project in matchingProjects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {Project}, skipping.", project.Name);
                continue;
            }

            var candidates = new List<(INamedTypeSymbol Symbol, string FilePath)>();

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

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var node in root.DescendantNodes())
                {
                    if (node is not Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax interfaceDecl)
                    {
                        continue;
                    }

                    if (semanticModel.GetDeclaredSymbol(interfaceDecl) is not INamedTypeSymbol symbol)
                    {
                        continue;
                    }

                    if (postfix != null && !symbol.Name.EndsWith(postfix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    candidates.Add((symbol, document.FilePath));
                }
            }

            if (candidates.Count == 0)
            {
                _logger.LogInformation("No matching interfaces found in project {Project}.", project.Name);
                continue;
            }

            _logger.LogInformation(
                "Found {Count} interface(s) in {Project}. Generating...",
                candidates.Count, project.Name);

            foreach (var (symbol, sourceFilePath) in candidates)
            {
                var (asyncInterfaceSource, adapterSource) = generator.Generate(
                    symbol,
                    asyncHelperNamespace,
                    out var warnings);

                foreach (var warning in warnings)
                {
                    _logger.LogWarning("{Warning}", warning);
                }

                var sourceDir = Path.GetDirectoryName(sourceFilePath)!;

                var asyncInterfaceName = symbol.Name + "Async";
                var asyncInterfaceDir = asyncInterfaceOutputDir ?? sourceDir;
                var asyncInterfacePath = Path.Combine(asyncInterfaceDir, asyncInterfaceName + ".cs");
                generated.Add((asyncInterfacePath, asyncInterfaceSource, $"{symbol.Name} → {asyncInterfaceName}"));

                var baseName = symbol.Name.Length > 1 && symbol.Name[0] == 'I'
                    ? symbol.Name[1..]
                    : symbol.Name;
                var adapterName = baseName + "AsyncAdapter";
                var adapterDir = adapterOutputDir ?? sourceDir;
                var adapterPath = Path.Combine(adapterDir, adapterName + ".cs");
                generated.Add((adapterPath, adapterSource, $"{symbol.Name} → {adapterName}"));
            }
        }

        if (generated.Count == 0)
        {
            _logger.LogInformation("No files to generate.");
            return;
        }

        foreach (var (filePath, content, label) in generated)
        {
            if (dryRun)
            {
                _logger.LogInformation("[dry-run] Would generate ({Label}): {FilePath}", label, filePath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, content);
                _logger.LogInformation("Generated ({Label}): {FilePath}", label, filePath);
            }
        }

        _logger.LogInformation(
            "{Action} {Count} file(s).",
            dryRun ? "Would generate" : "Generated",
            generated.Count);
    }
}
