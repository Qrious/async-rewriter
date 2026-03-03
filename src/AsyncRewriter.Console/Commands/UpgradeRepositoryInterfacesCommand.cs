using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// For every <c>I*Repository</c> interface in a solution for which a corresponding
/// <c>I*RepositoryAsync</c> interface also exists, this command:
/// <list type="number">
///   <item>Replaces all references to <c>I*Repository</c> with <c>I*RepositoryAsync</c>.</item>
///   <item>
///     Adds a <c>CancellationToken cancellationToken = default</c> parameter to every
///     method declared in the <c>I*RepositoryAsync</c> interface (if not already present).
///   </item>
/// </list>
///
/// Example:
/// <code>
///   async-rewriter upgrade-repository-interfaces MySolution.sln
///   async-rewriter upgrade-repository-interfaces MySolution.sln --dry-run
/// </code>
/// </summary>
public class UpgradeRepositoryInterfacesCommand : Command
{
    private readonly ILogger<UpgradeRepositoryInterfacesCommand> _logger;

    public UpgradeRepositoryInterfacesCommand(ILogger<UpgradeRepositoryInterfacesCommand> logger)
        : base("upgrade-repository-interfaces",
            "Replace I*Repository references with I*RepositoryAsync and add CancellationToken parameters")
    {
        _logger = logger;

        var solutionArgument = new Argument<string>(
            "solution",
            "Path to the .sln file to process");

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
            "Solution loaded ({ProjectCount} projects). Scanning for I*Repository / I*RepositoryAsync pairs...",
            solution.Projects.Count());

        // ── Phase 1: discover pairs ────────────────────────────────────────────
        // Map: old symbol → new symbol, collected across all projects.
        // Use the first project that defines the interface (others will reference it).
        var pairs = await DiscoverPairsAsync(solution);

        if (pairs.Count == 0)
        {
            _logger.LogInformation("No matching I*Repository / I*RepositoryAsync pairs found.");
            return;
        }

        foreach (var (oldName, newName) in pairs.Select(p => (p.Key.ToDisplayString(), p.Value.ToDisplayString())))
        {
            _logger.LogInformation("Pair found: {Old} → {New}", oldName, newName);
        }

        // ── Phase 2: rewrite all documents ────────────────────────────────────
        // We need the latest solution after each round of changes, but since we're
        // working on the file system (not the in-memory workspace), we process all
        // documents independently from the original compilation.

        var modifiedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Could not get compilation for project {ProjectName}, skipping.", project.Name);
                continue;
            }

            // Resolve old symbols in this compilation.
            var localPairs = ResolveSymbolsInCompilation(compilation, pairs);
            if (localPairs.Count == 0)
            {
                continue;
            }

            var asyncSymbols = localPairs.Values.ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

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

                var anyChanged = false;

                // Step A: replace I*Repository → I*RepositoryAsync references
                foreach (var (oldSymbol, newSymbol) in localPairs)
                {
                    var replaceRewriter = new InterfaceReplaceRewriter(
                        semanticModel, oldSymbol, newSymbol.ToDisplayString());

                    var newRoot = replaceRewriter.Visit(root);
                    if (replaceRewriter.AnyRewritten)
                    {
                        root = newRoot!;
                        anyChanged = true;
                    }
                }

                // Step B: add CancellationToken to methods on the async interfaces
                // (only meaningful for the file that *declares* the interface)
                var ctRewriter = new AddCancellationTokenRewriter(semanticModel, asyncSymbols);
                var rootAfterCt = ctRewriter.Visit(root);
                if (ctRewriter.AnyRewritten)
                {
                    root = rootAfterCt!;
                    anyChanged = true;
                }

                if (!anyChanged)
                {
                    continue;
                }

                // Last writer wins if the same physical file appears in multiple projects.
                modifiedFiles[document.FilePath] = root.ToFullString();
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

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans every project in the solution and collects
    /// (IXRepository symbol → IXRepositoryAsync symbol) pairs.
    /// </summary>
    private async Task<Dictionary<INamedTypeSymbol, INamedTypeSymbol>> DiscoverPairsAsync(Solution solution)
    {
        var pairs = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
            {
                continue;
            }

            var allInterfaces = GetAllInterfaceSymbols(compilation.GlobalNamespace);

            foreach (var iface in allInterfaces)
            {
                if (!IsRepositoryInterface(iface.Name))
                {
                    continue;
                }

                var asyncName = iface.Name + "Async";

                // Look for the async counterpart in the same namespace.
                var asyncSymbol = allInterfaces.FirstOrDefault(i =>
                    i.Name == asyncName &&
                    SymbolEqualityComparer.Default.Equals(i.ContainingNamespace, iface.ContainingNamespace));

                if (asyncSymbol == null)
                {
                    continue;
                }

                if (!pairs.ContainsKey(iface))
                {
                    pairs[iface] = asyncSymbol;
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// Returns true for interfaces named <c>I…Repository</c> (but not <c>I…RepositoryAsync</c>).
    /// </summary>
    private static bool IsRepositoryInterface(string name)
    {
        return name.StartsWith("I", StringComparison.Ordinal)
               && name.EndsWith("Repository", StringComparison.Ordinal)
               && !name.EndsWith("RepositoryAsync", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the discovered (old, new) pairs to symbols from the given compilation
    /// (which may differ from the compilation they were discovered in).
    /// </summary>
    private static Dictionary<INamedTypeSymbol, INamedTypeSymbol> ResolveSymbolsInCompilation(
        Compilation compilation,
        Dictionary<INamedTypeSymbol, INamedTypeSymbol> globalPairs)
    {
        var local = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var (oldGlobal, newGlobal) in globalPairs)
        {
            var oldLocal = compilation.GetTypeByMetadataName(oldGlobal.MetadataName) ??
                           FindByDisplayName(compilation, oldGlobal.ToDisplayString());

            var newLocal = compilation.GetTypeByMetadataName(newGlobal.MetadataName) ??
                           FindByDisplayName(compilation, newGlobal.ToDisplayString());

            if (oldLocal != null && newLocal != null)
            {
                local[oldLocal] = newLocal;
            }
        }

        return local;
    }

    private static INamedTypeSymbol? FindByDisplayName(Compilation compilation, string displayName)
    {
        return GetAllInterfaceSymbols(compilation.GlobalNamespace)
            .FirstOrDefault(i => i.ToDisplayString() == displayName);
    }

    private static IEnumerable<INamedTypeSymbol> GetAllInterfaceSymbols(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                yield return type;
            }
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var iface in GetAllInterfaceSymbols(child))
            {
                yield return iface;
            }
        }
    }
}
