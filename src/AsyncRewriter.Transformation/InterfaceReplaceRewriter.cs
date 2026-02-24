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
    private readonly INamedTypeSymbol _oldSymbol;
    private readonly string _newShortName;
    private readonly string _newNamespace;
    private readonly string _newFullName;
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

    // ──────────────────────────────────────────────────────────────────────────
    // Compilation unit — add using for new namespace when needed
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

        if (_anyRewritten && !string.IsNullOrEmpty(_newNamespace))
        {
            visited = EnsureUsingDirective(visited, _newNamespace);
        }

        return visited;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Type name rewriting — covers IdentifierName and QualifiedName nodes
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (!ResolvesToOldInterface(node))
        {
            return node;
        }

        _anyRewritten = true;
        return IdentifierName(_newShortName)
            .WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        // If the whole qualified name resolves to the old interface, replace wholesale.
        if (ResolvesToOldInterface(node))
        {
            _anyRewritten = true;
            return ParseName(_newFullName)
                .WithTriviaFrom(node);
        }

        return base.VisitQualifiedName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        if (ResolvesToOldInterface(node))
        {
            _anyRewritten = true;
            return IdentifierName(_newShortName)
                .WithTriviaFrom(node);
        }

        return base.VisitGenericName(node);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Detection helpers
    // ──────────────────────────────────────────────────────────────────────────

    private bool ResolvesToOldInterface(ExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        return symbol != null && SymbolEqualityComparer.Default.Equals(symbol, _oldSymbol);
    }

    private bool ResolvesToOldInterface(TypeSyntax node)
    {
        var typeInfo = _semanticModel.GetTypeInfo(node);
        if (typeInfo.Type != null && SymbolEqualityComparer.Default.Equals(typeInfo.Type, _oldSymbol))
        {
            return true;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        return symbol != null && SymbolEqualityComparer.Default.Equals(symbol, _oldSymbol);
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
