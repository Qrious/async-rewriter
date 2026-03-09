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
            .WithLeadingTrivia(Space)
            .WithTrailingTrivia(Space);

        var defaultValue = EqualsValueClause(
            Token(TriviaList(Space), SyntaxKind.EqualsToken, TriviaList(Space)),
            LiteralExpression(SyntaxKind.DefaultLiteralExpression, Token(SyntaxKind.DefaultKeyword)));

        var ctParam = Parameter(Identifier("cancellationToken"))
            .WithType(ctType)
            .WithDefault(defaultValue);

        var oldList = method.ParameterList;
        SeparatedSyntaxList<ParameterSyntax> newParams;

        if (oldList.Parameters.Any(ct => ct.Identifier.ToString() == "cancellationToken"))
        {
            // If the method already has a parameter named 'cancellationToken'  do not add another one.
            return method;
        }

        if (oldList.Parameters.Count == 0)
        {
            newParams = SingletonSeparatedList(ctParam);
        }
        else
        {
            // If the method has a 'params' parameter, insert the cancellation token
            // before the params parameter so the params argument remains the last parameter.
            var paramsIndex = -1;
            for (int i = 0; i < oldList.Parameters.Count; i++)
            {
                if (oldList.Parameters[i].Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                {
                    paramsIndex = i;
                    break;
                }
            }

            if (paramsIndex >= 0)
            {
                // Insert before the params parameter. Do not add extra leading trivia; the
                // separator tokens are handled by the SeparatedSyntaxList.
                newParams = oldList.Parameters.Insert(paramsIndex, ctParam.WithLeadingTrivia(Space));
            }
            else
            {
                // Insert after the last parameter; add a leading space so formatting matches.
                newParams = oldList.Parameters.Add(ctParam.WithLeadingTrivia(Space));
            }
        }

        var newList = oldList.WithParameters(newParams);
        return method.WithParameterList(newList);
    }
}
