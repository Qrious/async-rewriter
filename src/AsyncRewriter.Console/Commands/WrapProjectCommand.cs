using System.CommandLine;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

/// <summary>
/// Scans a C# project for interfaces (optionally filtered by a name postfix) and
/// generates two new files per interface:
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
/// Example:
/// <code>
///   async-rewriter wrap-project MyProject.csproj \
///       --async-helper-namespace MyProject.Threading \
///       --postfix Repository \
///       --dry-run
/// </code>
/// </summary>
public class WrapProjectCommand : Command
{
    private readonly ILogger<WrapProjectCommand> _logger;

    public WrapProjectCommand(ILogger<WrapProjectCommand> logger)
        : base("wrap-project",
            "Generate async interface mirrors and synchronous adapter classes for all interfaces in a project")
    {
        _logger = logger;

        var projectArgument = new Argument<string>(
            "project",
            "Path to the .csproj file to scan");

        var helperNamespaceOption = new Option<string>(
            aliases: ["--async-helper-namespace", "-ns"],
            description: "Namespace that contains AsyncHelper (e.g. MyProject.Threading)")
        {
            IsRequired = true
        };

        var postfixOption = new Option<string?>(
            aliases: ["--postfix", "-p"],
            description: "Only process interfaces whose name ends with this postfix (e.g. Repository, Service). " +
                         "When omitted, all interfaces are processed.",
            getDefaultValue: () => null);

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be generated without writing them",
            getDefaultValue: () => false);

        AddArgument(projectArgument);
        AddOption(helperNamespaceOption);
        AddOption(postfixOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, projectArgument, helperNamespaceOption, postfixOption, dryRunOption);
    }

    private async Task ExecuteAsync(
        string projectPath,
        string asyncHelperNamespace,
        string? postfix,
        bool dryRun)
    {
        if (!File.Exists(projectPath))
        {
            _logger.LogError("Project file not found: {ProjectPath}", projectPath);
            return;
        }

        _logger.LogInformation("Opening project: {ProjectPath}", projectPath);

        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch
        {
            // Already registered — ignore.
        }

        using var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(projectPath);
        var compilation = await project.GetCompilationAsync();

        if (compilation == null)
        {
            _logger.LogError("Could not get compilation for project {Project}.", project.Name);
            return;
        }

        // Collect (interface symbol, source file path) pairs.
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

                // Postfix filter.
                if (postfix != null &&
                    !symbol.Name.EndsWith(postfix, StringComparison.Ordinal))
                {
                    continue;
                }

                candidates.Add((symbol, document.FilePath));
            }
        }

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No matching interfaces found.");
            return;
        }

        _logger.LogInformation(
            "Found {Count} interface(s) to wrap. Generating...",
            candidates.Count);

        var generator = new AsyncWrapperGenerator();
        var generated = new List<(string FilePath, string Content, string Label)>();

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

            var dir = Path.GetDirectoryName(sourceFilePath)!;

            // Async interface: IFooAsync.cs
            var asyncInterfaceName = symbol.Name + "Async";
            var asyncInterfacePath = Path.Combine(dir, asyncInterfaceName + ".cs");
            generated.Add((asyncInterfacePath, asyncInterfaceSource, $"{symbol.Name} → {asyncInterfaceName}"));

            // Adapter: FooAsyncAdapter.cs
            var baseName = symbol.Name.Length > 1 && symbol.Name[0] == 'I'
                ? symbol.Name[1..]
                : symbol.Name;
            var adapterName = baseName + "AsyncAdapter";
            var adapterPath = Path.Combine(dir, adapterName + ".cs");
            generated.Add((adapterPath, adapterSource, $"{symbol.Name} → {adapterName}"));
        }

        foreach (var (filePath, content, label) in generated)
        {
            if (dryRun)
            {
                _logger.LogInformation("[dry-run] Would generate ({Label}): {FilePath}", label, filePath);
            }
            else
            {
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
