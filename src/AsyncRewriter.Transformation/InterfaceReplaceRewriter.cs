using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Replaces all references to a given interface (identified by its fully-qualified name)
/// with another interface throughout a syntax tree.
///
/// Replacements are made for:
/// <list type="bullet">
///   <item>Type references in field/property/variable/parameter/return-type positions</item>
///   <item><c>using</c> directives whose namespace matches the old interface's namespace</item>
///   <item>A <c>using</c> directive for the new interface's namespace is added when needed</item>
/// </list>
///
/// Detection uses the Roslyn <see cref="SemanticModel"/> to resolve type symbols, so only
/// references that actually bind to the specified interface are replaced.
/// </summary>
public sealed class InterfaceReplaceRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;

    // Single-pair fields (null when using the multi-pair constructor).
    private readonly INamedTypeSymbol? _oldSymbol;
    private readonly string? _newShortName;
    private readonly string? _newNamespace;
    private readonly string? _newFullName;

    // Multi-pair support.
    private readonly IReadOnlyDictionary<INamedTypeSymbol, (string FullName, string ShortName, string Namespace)>? _pairs;

    private bool _anyRewritten;

    public bool AnyRewritten => _anyRewritten;

    public InterfaceReplaceRewriter(
        SemanticModel semanticModel,
        INamedTypeSymbol oldSymbol,
        string newFullName)
    {
        _semanticModel = semanticModel;
        _oldSymbol = oldSymbol;
        _newFullName = newFullName;

        var lastDot = newFullName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            _newNamespace = newFullName[..lastDot];
            _newShortName = newFullName[(lastDot + 1)..];
        }
        else
        {
            _newNamespace = string.Empty;
            _newShortName = newFullName;
        }
    }

    /// <summary>
    /// Multi-pair constructor: replaces all listed interfaces in a single tree traversal,
    /// avoiding the "node is not within syntax tree" error that occurs when the same
    /// semantic model is used against nodes from an already-rewritten tree.
    /// </summary>
    public InterfaceReplaceRewriter(
        SemanticModel semanticModel,
        IReadOnlyDictionary<INamedTypeSymbol, string> oldToNewFullName)
    {
        _semanticModel = semanticModel;

        var pairs = new Dictionary<INamedTypeSymbol, (string, string, string)>(SymbolEqualityComparer.Default);
        foreach (var (oldSym, newFull) in oldToNewFullName)
        {
            var lastDot = newFull.LastIndexOf('.');
            var ns = lastDot >= 0 ? newFull[..lastDot] : string.Empty;
            var shortName = lastDot >= 0 ? newFull[(lastDot + 1)..] : newFull;
            pairs[oldSym] = (newFull, shortName, ns);
        }

        _pairs = pairs;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Compilation unit — add using for new namespace(s) when needed
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

        if (_anyRewritten)
        {
            if (_pairs != null)
            {
                foreach (var (_, _, ns) in _pairs.Values.Where(p => !string.IsNullOrEmpty(p.Namespace)))
                {
                    visited = EnsureUsingDirective(visited, ns);
                }
            }
            else if (!string.IsNullOrEmpty(_newNamespace))
            {
                visited = EnsureUsingDirective(visited, _newNamespace!);
            }
        }

        return visited;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Type name rewriting — covers IdentifierName and QualifiedName nodes
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var replacement = TryGetReplacement(node);
        if (replacement == null)
        {
            return node;
        }

        _anyRewritten = true;
        return IdentifierName(replacement.Value.ShortName)
            .WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var replacement = TryGetReplacement(node);
        if (replacement != null)
        {
            _anyRewritten = true;
            return ParseName(replacement.Value.FullName)
                .WithTriviaFrom(node);
        }

        return base.VisitQualifiedName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var replacement = TryGetReplacement(node);
        if (replacement != null)
        {
            _anyRewritten = true;
            return IdentifierName(replacement.Value.ShortName)
                .WithTriviaFrom(node);
        }

        return base.VisitGenericName(node);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Detection helpers
    // ──────────────────────────────────────────────────────────────────────────

    private (string FullName, string ShortName, string Namespace)? TryGetReplacement(ExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        if (symbol == null)
        {
            return null;
        }

        if (_pairs != null)
        {
            return _pairs.TryGetValue((INamedTypeSymbol)symbol, out var entry) ? entry : null;
        }

        return SymbolEqualityComparer.Default.Equals(symbol, _oldSymbol)
            ? (_newFullName!, _newShortName!, _newNamespace!)
            : null;
    }

    private (string FullName, string ShortName, string Namespace)? TryGetReplacement(TypeSyntax node)
    {
        var typeInfo = _semanticModel.GetTypeInfo(node);
        if (typeInfo.Type is INamedTypeSymbol typeSymbol)
        {
            if (_pairs != null)
            {
                if (_pairs.TryGetValue(typeSymbol, out var entry))
                {
                    return entry;
                }
            }
            else if (SymbolEqualityComparer.Default.Equals(typeSymbol, _oldSymbol))
            {
                return (_newFullName!, _newShortName!, _newNamespace!);
            }
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        if (symbol is not INamedTypeSymbol namedSymbol)
        {
            return null;
        }

        if (_pairs != null)
        {
            return _pairs.TryGetValue(namedSymbol, out var p) ? p : null;
        }

        return SymbolEqualityComparer.Default.Equals(namedSymbol, _oldSymbol)
            ? (_newFullName!, _newShortName!, _newNamespace!)
            : null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Using-directive helper
    // ──────────────────────────────────────────────────────────────────────────

    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        if (root.Usings.Any(u => u.Name?.ToString() == namespaceName))
        {
            return root;
        }

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return root.AddUsings(usingDirective);
    }
}
