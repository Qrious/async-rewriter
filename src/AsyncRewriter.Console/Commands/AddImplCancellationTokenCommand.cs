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
/// name postfix) and adds a <c>CancellationToken cancellationToken = default</c> parameter
/// to every method in each concrete implementation of those interfaces that does not
/// already have one.
///
/// Example:
/// <code>
///   async-rewriter add-impl-cancellation-token MySolution.sln \
///       --projects MyProject.Data \
///       --postfix Repository \
///       --dry-run
/// </code>
/// </summary>
public class AddImplCancellationTokenCommand : Command
{
    private readonly ILogger<AddImplCancellationTokenCommand> _logger;

    public AddImplCancellationTokenCommand(ILogger<AddImplCancellationTokenCommand> logger)
        : base("add-impl-cancellation-token",
            "Add CancellationToken parameters to implementation methods that satisfy interface contracts")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to scan");

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

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be modified without writing changes",
            getDefaultValue: () => false);

        AddArgument(solutionArgument);
        AddOption(projectsOption);
        AddOption(postfixOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, solutionArgument, projectsOption, postfixOption, dryRunOption);
    }

    private async Task ExecuteAsync(
        string solutionPath,
        string[] projects,
        string? postfix,
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
        var interfaceSymbols = new List<INamedTypeSymbol>();

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

                var root = (CompilationUnitSyntax)await syntaxTree.GetRootAsync();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                foreach (var node in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
                {
                    if (semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
                        continue;

                    if (postfix != null && !symbol.Name.EndsWith(postfix, StringComparison.Ordinal))
                        continue;

                    interfaceSymbols.Add(symbol);
                }
            }
        }

        if (interfaceSymbols.Count == 0)
        {
            _logger.LogInformation("No matching interfaces found.");
            return;
        }

        _logger.LogInformation(
            "Found {Count} interface(s). Scanning solution for implementations...",
            interfaceSymbols.Count);

        // ── Phase 2: rewrite implementation files across the whole solution ──
        // Last writer wins if the same physical file appears in multiple projects.
        var modifiedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {Project}, skipping.", project.Name);
                continue;
            }

            // Resolve interface symbols into this compilation.
            var localInterfaces = ResolveInterfacesInCompilation(compilation, interfaceSymbols);
            if (localInterfaces.Count == 0)
                continue;

            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;

                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var root = await syntaxTree.GetRootAsync();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                var rewriter = new ImplementationCancellationTokenRewriter(semanticModel, localInterfaces);
                var newRoot = rewriter.Visit(root);

                if (!rewriter.AnyRewritten) continue;

                modifiedFiles[document.FilePath] = newRoot!.ToFullString();
            }
        }

        if (modifiedFiles.Count == 0)
        {
            _logger.LogInformation("No implementation files needed modification.");
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

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the discovered interface symbols into the given compilation by matching
    /// on the fully-qualified display name, which works correctly across compilations.
    /// </summary>
    private static HashSet<INamedTypeSymbol> ResolveInterfacesInCompilation(
        Compilation compilation,
        IEnumerable<INamedTypeSymbol> interfaces)
    {
        var result = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        var targetDisplayNames = interfaces
            .Select(i => i.ToDisplayString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var iface in GetAllInterfaceSymbols(compilation.GlobalNamespace))
        {
            if (targetDisplayNames.Contains(iface.ToDisplayString()))
                result.Add(iface);
        }

        return result;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllInterfaceSymbols(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind == TypeKind.Interface)
                yield return type;
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var iface in GetAllInterfaceSymbols(child))
                yield return iface;
        }
    }
}
