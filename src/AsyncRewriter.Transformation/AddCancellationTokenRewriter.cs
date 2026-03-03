using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Adds a <c>CancellationToken cancellationToken = default</c> parameter to every method
/// declared inside a specified set of interface declarations, skipping methods that already
/// have a <c>CancellationToken</c> parameter.
///
/// Interfaces are matched by short name so the rewriter can safely be applied to a
/// syntax tree that has already been modified (and therefore no longer matches its
/// original <see cref="SemanticModel"/>).
/// </summary>
public sealed class AddCancellationTokenRewriter : CSharpSyntaxRewriter
{
    private readonly IReadOnlySet<string> _targetInterfaceNames;

    public bool AnyRewritten { get; private set; }

    public AddCancellationTokenRewriter(IReadOnlySet<INamedTypeSymbol> targetInterfaces)
    {
        _targetInterfaceNames = targetInterfaces
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        if (!_targetInterfaceNames.Contains(node.Identifier.ValueText))
        {
            return base.VisitInterfaceDeclaration(node);
        }

        // Visit children so that nested interfaces (unusual but possible) are processed too,
        // then patch the methods of this interface.
        var visited = (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!;

        var newMembers = visited.Members.Select(member =>
        {
            if (member is not MethodDeclarationSyntax method)
            {
                return member;
            }

            if (AlreadyHasCancellationToken(method))
            {
                return member;
            }

            AnyRewritten = true;
            return (MemberDeclarationSyntax)AddCancellationTokenParameter(method);
        }).ToList();

        return visited.WithMembers(List(newMembers));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static bool AlreadyHasCancellationToken(MethodDeclarationSyntax method)
    {
        return method.ParameterList.Parameters.Any(p =>
            p.Type?.ToString().Contains("CancellationToken") == true);
    }

    private static MethodDeclarationSyntax AddCancellationTokenParameter(MethodDeclarationSyntax method)
    {
        // CancellationToken cancellationToken = default
        var ctType = ParseTypeName("CancellationToken")
            .WithLeadingTrivia(Space);

        var defaultValue = EqualsValueClause(
            LiteralExpression(SyntaxKind.DefaultLiteralExpression, Token(SyntaxKind.DefaultKeyword)));

        var ctParam = Parameter(Identifier("cancellationToken"))
            .WithType(ctType)
            .WithDefault(defaultValue);

        var oldList = method.ParameterList;
        SeparatedSyntaxList<ParameterSyntax> newParams;

        if (oldList.Parameters.Count == 0)
        {
            newParams = SingletonSeparatedList(ctParam);
        }
        else
        {
            // Insert after the last parameter; add a leading comma separator.
            ctParam = ctParam.WithLeadingTrivia(Space);
            newParams = oldList.Parameters.Add(ctParam);
        }

        var newList = oldList.WithParameters(newParams);
        return method.WithParameterList(newList);
    }
}
