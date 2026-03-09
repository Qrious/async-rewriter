using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Adds a <c>CancellationToken cancellationToken = default</c> parameter to every method
/// in a class that implements one of the specified async interfaces, skipping methods that
/// already have a <c>CancellationToken</c> parameter.
///
/// The rewriter uses the <see cref="SemanticModel"/> to determine whether a class implements
/// one of the target interfaces, and then matches methods by name and arity against the
/// interface members.
/// </summary>
public sealed class ImplementationCancellationTokenRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly HashSet<INamedTypeSymbol> _targetInterfaces;

    public bool AnyRewritten { get; private set; }

    public ImplementationCancellationTokenRewriter(
        SemanticModel semanticModel,
        IReadOnlySet<INamedTypeSymbol> targetInterfaces)
    {
        _semanticModel = semanticModel;
        _targetInterfaces = new HashSet<INamedTypeSymbol>(targetInterfaces, SymbolEqualityComparer.Default);
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

        if (AnyRewritten)
        {
            visited = EnsureUsingDirective(visited, "System.Threading");
        }

        return visited;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // Check whether this class implements any of the target interfaces.
        if (_semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol classSymbol)
            return base.VisitClassDeclaration(node);

        // Build a mapping from impl method name → canonical interface method name.
        // Also maps "Foo" → "FooAsync" when the interface declares "FooAsync" but the
        // implementation hasn't been renamed yet (exact match always wins).
        var nameMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var implementsAny = false;

        foreach (var iface in classSymbol.AllInterfaces)
        {
            // AllInterfaces yields closed generic instantiations (e.g. IRepository<User>).
            // _targetInterfaces holds the original open definitions (e.g. IRepository<T>).
            // Use OriginalDefinition so both sides compare on the same symbol.
            var ifaceToCheck = iface.IsGenericType ? iface.OriginalDefinition : iface;
            if (!_targetInterfaces.Contains(ifaceToCheck))
                continue;

            implementsAny = true;
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var ifaceName = member.Name;
                nameMapping[ifaceName] = ifaceName; // exact match always wins

                // Allow matching "Foo" in the impl when the interface declares "FooAsync".
                if (ifaceName.EndsWith("Async", StringComparison.Ordinal))
                    nameMapping.TryAdd(ifaceName[..^"Async".Length], ifaceName);
            }
        }

        if (!implementsAny)
            return base.VisitClassDeclaration(node);

        // Visit children first (handles nested classes), then patch method signatures.
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        var newMembers = visited.Members.Select(member =>
        {
            if (member is not MethodDeclarationSyntax method)
                return member;

            if (!nameMapping.TryGetValue(method.Identifier.ValueText, out var targetName))
                return member;

            var updated = method;

            // Rename to match the interface name (e.g. "GetFoo" → "GetFooAsync").
            if (method.Identifier.ValueText != targetName)
            {
                updated = updated.WithIdentifier(Identifier(targetName).WithTriviaFrom(method.Identifier));
                AnyRewritten = true;
            }

            // Add CancellationToken if not already present.
            if (!AlreadyHasCancellationToken(updated))
            {
                updated = AddCancellationTokenParameter(updated);
                AnyRewritten = true;
            }

            return updated == method ? member : (MemberDeclarationSyntax)updated;
        }).ToList();

        return visited.WithMembers(List(newMembers));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static bool AlreadyHasCancellationToken(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Any(p =>
            p.Type?.ToString().Contains("CancellationToken") == true);

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

        // Find a 'params' parameter — CancellationToken must come before it.
        var paramsIndex = -1;
        for (int i = 0; i < oldList.Parameters.Count; i++)
        {
            if (oldList.Parameters[i].Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            {
                paramsIndex = i;
                break;
            }
        }

        SeparatedSyntaxList<ParameterSyntax> newParams;
        if (oldList.Parameters.Count == 0)
        {
            newParams = SingletonSeparatedList(ctParam);
        }
        else if (paramsIndex >= 0)
        {
            newParams = oldList.Parameters.Insert(paramsIndex, ctParam.WithLeadingTrivia(Space));
        }
        else
        {
            newParams = oldList.Parameters.Add(ctParam.WithLeadingTrivia(Space));
        }

        return method.WithParameterList(oldList.WithParameters(newParams));
    }

    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        if (root.Usings.Any(u => u.Name?.ToString() == namespaceName))
            return root;

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return root.AddUsings(usingDirective);
    }
}

