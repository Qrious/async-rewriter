using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
///     Placed next to the source interface file by default.
///   </item>
///   <item>
///     <c>{Name}AsyncAdapter.cs</c> — a class that implements the original interface
///     by delegating to the async counterpart via <c>AsyncHelper.RunTaskSynchronously</c>.
///     Placed next to the concrete implementation of the interface by default
///     (searching the entire solution); falls back to the interface file's directory
///     when no implementation is found.
///   </item>
/// </list>
///
/// Use <c>--async-interface-output-dir</c> and <c>--adapter-output-dir</c> to override
/// the output directories explicitly.
///
/// Example:
/// <code>
///   async-rewriter wrap-project MySolution.sln \
///       --async-helper-namespace MyProject.Threading \
///       --projects MyProject.Contracts \
///       --postfix Repository \
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
            description: "Project names to scan for interfaces (e.g. MyProject.Data MyProject.Domain). " +
                         "When omitted, all projects in the solution are scanned.")
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
            description: "Override the output directory for IFooAsync.cs files. " +
                         "Defaults to the same directory as the source interface file.",
            getDefaultValue: () => null);

        var adapterOutputDirOption = new Option<string?>(
            aliases: ["--adapter-output-dir", "-ad"],
            description: "Override the output directory for FooAsyncAdapter.cs files. " +
                         "Defaults to the directory of the concrete implementation of the interface " +
                         "(searched across the whole solution); falls back to the interface directory " +
                         "when no implementation is found.",
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

        var interfaceProjects = solution.Projects
            .Where(p => projectFilter == null || projectFilter.Contains(p.Name))
            .ToList();

        if (projectFilter != null && interfaceProjects.Count == 0)
        {
            _logger.LogError(
                "None of the specified projects were found in the solution. " +
                "Available projects: {Projects}",
                string.Join(", ", solution.Projects.Select(p => p.Name)));
            return;
        }

        _logger.LogInformation(
            "Scanning {Count} project(s) for interfaces: {Projects}",
            interfaceProjects.Count,
            string.Join(", ", interfaceProjects.Select(p => p.Name)));

        // ── Phase 1: collect interface candidates ────────────────────────────
        var candidates = new List<(INamedTypeSymbol Symbol, string InterfaceFilePath)>();

        foreach (var project in interfaceProjects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {Project}, skipping.", project.Name);
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var node in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
                        continue;

                    if (postfix != null && !symbol.Name.EndsWith(postfix, StringComparison.Ordinal))
                        continue;

                    candidates.Add((symbol, document.FilePath));
                }
            }
        }

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No matching interfaces found.");
            return;
        }

        _logger.LogInformation("Found {Count} interface(s). Searching for implementations...", candidates.Count);

        // ── Phase 2: find implementation file paths across the whole solution ─
        // Build a map: interface full name → first implementation file path found.
        Dictionary<string, string>? implFilePaths = null;

        if (adapterOutputDir == null)
        {
            implFilePaths = await FindImplementationFilePathsAsync(solution, candidates);
        }

        // ── Phase 3: generate files ──────────────────────────────────────────
        var generator = new AsyncWrapperGenerator();
        var generated = new List<(string FilePath, string Content, string Label)>();

        foreach (var (symbol, interfaceFilePath) in candidates)
        {
            var (asyncInterfaceSource, adapterSource) = generator.Generate(
                symbol,
                asyncHelperNamespace,
                out var warnings);

            foreach (var warning in warnings)
                _logger.LogWarning("{Warning}", warning);

            var interfaceDir = Path.GetDirectoryName(interfaceFilePath)!;

            // Async interface → next to source interface (or explicit override).
            var asyncInterfaceName = symbol.Name + "Async";
            var resolvedInterfaceDir = asyncInterfaceOutputDir ?? interfaceDir;
            var asyncInterfacePath = Path.Combine(resolvedInterfaceDir, asyncInterfaceName + ".cs");
            generated.Add((asyncInterfacePath, asyncInterfaceSource, $"{symbol.Name} → {asyncInterfaceName}"));

            // Adapter → next to implementation, with fallback to interface dir.
            var baseName = symbol.Name.Length > 1 && symbol.Name[0] == 'I'
                ? symbol.Name[1..]
                : symbol.Name;
            var adapterName = baseName + "AsyncAdapter";

            string resolvedAdapterDir;
            if (adapterOutputDir != null)
            {
                resolvedAdapterDir = adapterOutputDir;
            }
            else if (implFilePaths!.TryGetValue(symbol.ToDisplayString(), out var implFile))
            {
                resolvedAdapterDir = Path.GetDirectoryName(implFile)!;
            }
            else
            {
                _logger.LogWarning(
                    "No implementation found for {Interface} in the solution; " +
                    "placing adapter next to the interface instead.",
                    symbol.Name);
                resolvedAdapterDir = interfaceDir;
            }

            var adapterPath = Path.Combine(resolvedAdapterDir, adapterName + ".cs");
            generated.Add((adapterPath, adapterSource, $"{symbol.Name} → {adapterName}"));
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

    // ──────────────────────────────────────────────────────────────────────────
    // Implementation lookup
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the entire solution for classes that directly implement one of the
    /// candidate interfaces and returns a map of interface full name → file path.
    /// Only the first implementation found per interface is recorded.
    /// </summary>
    private async Task<Dictionary<string, string>> FindImplementationFilePathsAsync(
        Solution solution,
        List<(INamedTypeSymbol Symbol, string InterfaceFilePath)> candidates)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // Build a set of the interface full names we are looking for.
        var remaining = new HashSet<string>(
            candidates.Select(c => c.Symbol.ToDisplayString()),
            StringComparer.Ordinal);

        foreach (var project in solution.Projects)
        {
            if (remaining.Count == 0) break;

            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                if (remaining.Count == 0) break;
                if (document.FilePath == null) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol)
                        continue;

                    foreach (var iface in classSymbol.Interfaces)
                    {
                        var ifaceKey = iface.ToDisplayString();
                        if (!remaining.Contains(ifaceKey)) continue;

                        result[ifaceKey] = document.FilePath;
                        remaining.Remove(ifaceKey);
                    }
                }
            }
        }

        return result;
    }
}
