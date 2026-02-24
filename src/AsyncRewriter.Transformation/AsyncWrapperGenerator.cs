using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Generates two source files for a given interface:
/// <list type="bullet">
///   <item>
///     An async counterpart interface (<c>IFooAsync</c>) whose methods have an <c>Async</c>
///     suffix and return <c>Task</c> / <c>Task&lt;T&gt;</c>.
///   </item>
///   <item>
///     A synchronous adapter class (<c>FooAsyncAdapter</c>) that implements the original
///     interface by forwarding every call to the async counterpart via
///     <c>AsyncHelper.RunTaskSynchronously</c>.
///   </item>
/// </list>
/// Methods that have <c>out</c> or <c>ref</c> parameters are skipped with a warning
/// because those are incompatible with async delegates.
/// </summary>
public sealed class AsyncWrapperGenerator
{
    // ──────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────────

    /// <param name="interfaceSymbol">The interface to wrap.</param>
    /// <param name="asyncHelperNamespace">
    ///   Fully-qualified namespace that contains <c>AsyncHelper</c>
    ///   (e.g. <c>MyProject.Threading</c>).
    /// </param>
    /// <param name="warnings">
    ///   Populated with human-readable warnings for skipped members.
    /// </param>
    public (string AsyncInterfaceSource, string AdapterSource) Generate(
        INamedTypeSymbol interfaceSymbol,
        string asyncHelperNamespace,
        out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        var namespaceName = interfaceSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : interfaceSymbol.ContainingNamespace.ToDisplayString();

        var originalName = interfaceSymbol.Name;           // e.g. IFoo
        var asyncInterfaceName = originalName + "Async";   // e.g. IFooAsync

        // Strip leading 'I' for the adapter class name, fall back to full name.
        var baseName = originalName.Length > 1 && originalName[0] == 'I'
            ? originalName[1..]
            : originalName;
        var adapterClassName = baseName + "AsyncAdapter";  // e.g. FooAsyncAdapter

        var eligibleMethods = GetEligibleMethods(interfaceSymbol, warningList);

        var asyncInterfaceSource = BuildAsyncInterface(
            namespaceName, asyncInterfaceName, eligibleMethods);

        var adapterSource = BuildAdapter(
            namespaceName,
            adapterClassName,
            originalName,
            asyncInterfaceName,
            asyncHelperNamespace,
            eligibleMethods);

        return (asyncInterfaceSource, adapterSource);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Method selection
    // ──────────────────────────────────────────────────────────────────────────

    private static List<IMethodSymbol> GetEligibleMethods(
        INamedTypeSymbol interfaceSymbol,
        List<string> warnings)
    {
        var result = new List<IMethodSymbol>();

        foreach (var member in interfaceSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method)
            {
                continue;
            }

            // Skip property accessor noise; property-like methods are already covered
            // by the property symbol itself (which we don't process).
            if (method.MethodKind != MethodKind.Ordinary)
            {
                continue;
            }

            bool hasOutOrRef = method.Parameters.Any(
                p => p.RefKind is RefKind.Out or RefKind.Ref);

            if (hasOutOrRef)
            {
                warnings.Add(
                    $"Skipping {interfaceSymbol.Name}.{method.Name}: " +
                    "out/ref parameters are incompatible with async delegates.");
                continue;
            }

            result.Add(method);
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Async interface generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAsyncInterface(
        string? namespaceName,
        string asyncInterfaceName,
        List<IMethodSymbol> methods)
    {
        var members = new List<MemberDeclarationSyntax>();

        foreach (var method in methods)
        {
            var alreadyTaskBased = IsTaskLike(method.ReturnType.ToDisplayString());

            // Already Task-returning methods are passed through unchanged (name + return type).
            var returnType = alreadyTaskBased
                ? ParseTypeName(method.ReturnType.ToDisplayString()).WithTrailingTrivia(Space)
                : ToAsyncReturnTypeSyntax(method.ReturnType);

            var asyncMethodName = alreadyTaskBased || method.Name.EndsWith("Async", StringComparison.Ordinal)
                ? method.Name
                : method.Name + "Async";

            var methodDecl = MethodDeclaration(returnType, asyncMethodName)
                .WithParameterList(BuildParameterList(method))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(LineFeed, Whitespace("    "));

            members.Add(methodDecl);
        }

        var interfaceDecl = InterfaceDeclaration(asyncInterfaceName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space)))
            .WithMembers(List(members))
            .WithLeadingTrivia(LineFeed);

        return WrapInCompilationUnit(
            namespaceName,
            interfaceDecl,
            extraUsings: ["System.Threading.Tasks"]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Adapter class generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAdapter(
        string? namespaceName,
        string adapterClassName,
        string originalInterfaceName,
        string asyncInterfaceName,
        string asyncHelperNamespace,
        List<IMethodSymbol> methods)
    {
        // private readonly IFooAsync _inner;
        var fieldDecl = FieldDeclaration(
                VariableDeclaration(IdentifierName(asyncInterfaceName).WithTrailingTrivia(Space))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(Identifier("_inner")))))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(Space),
                Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(Space)))
            .WithLeadingTrivia(LineFeed, Whitespace("    "));

        // constructor
        var ctorDecl = ConstructorDeclaration(adapterClassName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space)))
            .WithParameterList(
                ParameterList(SingletonSeparatedList(
                    Parameter(Identifier("inner"))
                        .WithType(IdentifierName(asyncInterfaceName).WithTrailingTrivia(Space)))))
            .WithBody(Block(
                ExpressionStatement(
                    AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName("_inner"),
                        IdentifierName("inner")))))
            .WithLeadingTrivia(LineFeed, Whitespace("    "));

        var methodDecls = new List<MemberDeclarationSyntax>();
        methodDecls.Add(fieldDecl);
        methodDecls.Add(ctorDecl);

        foreach (var method in methods)
        {
            var alreadyTaskBased = IsTaskLike(method.ReturnType.ToDisplayString());

            var asyncMethodName = alreadyTaskBased || method.Name.EndsWith("Async", StringComparison.Ordinal)
                ? method.Name
                : method.Name + "Async";

            // Build the inner call: _inner.MethodAsync(p1, p2, ...) or _inner.Method(...)
            var innerCall = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("_inner"),
                    IdentifierName(asyncMethodName)),
                BuildArgumentList(method));

            StatementSyntax bodyStatement;
            var isVoidReturn = method.ReturnType.SpecialType == SpecialType.System_Void;

            if (alreadyTaskBased)
            {
                // Method already returns Task — delegate directly, no sync wrapper needed.
                bodyStatement = ReturnStatement(innerCall.WithLeadingTrivia(Space));
            }
            else
            {
                // Lambda: () => _inner.MethodAsync(...)
                var lambda = ParenthesizedLambdaExpression(innerCall);

                // AsyncHelper.RunTaskSynchronously(lambda)
                var helperCall = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        IdentifierName("AsyncHelper"),
                        IdentifierName("RunTaskSynchronously")),
                    ArgumentList(SingletonSeparatedList(Argument(lambda))));

                if (isVoidReturn)
                {
                    bodyStatement = ExpressionStatement(helperCall);
                }
                else
                {
                    bodyStatement = ReturnStatement(helperCall.WithLeadingTrivia(Space));
                }
            }

            // Use the original (non-async) return type for the implementing method.
            var returnTypeSyntax = ParseTypeName(method.ReturnType.ToDisplayString())
                .WithTrailingTrivia(Space);

            var methodDecl = MethodDeclaration(returnTypeSyntax, method.Name)
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space)))
                .WithParameterList(BuildParameterList(method))
                .WithBody(Block(bodyStatement))
                .WithLeadingTrivia(LineFeed, Whitespace("    "));

            methodDecls.Add(methodDecl);
        }

        var classDecl = ClassDeclaration(adapterClassName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(Space)))
            .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(
                SimpleBaseType(IdentifierName(originalInterfaceName)))))
            .WithMembers(List(methodDecls))
            .WithLeadingTrivia(LineFeed);

        return WrapInCompilationUnit(
            namespaceName,
            classDecl,
            extraUsings: [asyncHelperNamespace, "System.Threading.Tasks"]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Syntax helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Transforms the method's return type to its async counterpart:
    /// <c>void</c> → <c>Task</c>, <c>T</c> → <c>Task&lt;T&gt;</c>.
    /// Types that are already Task/ValueTask are left unchanged.
    /// </summary>
    private static TypeSyntax ToAsyncReturnTypeSyntax(ITypeSymbol returnType)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return IdentifierName("Task").WithTrailingTrivia(Space);
        }

        var displayName = returnType.ToDisplayString();

        // Already a Task / ValueTask family — keep as-is.
        if (IsTaskLike(displayName))
        {
            return ParseTypeName(displayName).WithTrailingTrivia(Space);
        }

        // Wrap in Task<T>.
        return GenericName(Identifier("Task"))
            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                ParseTypeName(displayName))))
            .WithTrailingTrivia(Space);
    }

    private static bool IsTaskLike(string displayName) =>
        displayName is
            "System.Threading.Tasks.Task" or
            "System.Threading.Tasks.ValueTask" or
            "Task" or "ValueTask" ||
        displayName.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) ||
        displayName.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal) ||
        displayName.StartsWith("Task<", StringComparison.Ordinal) ||
        displayName.StartsWith("ValueTask<", StringComparison.Ordinal);

    private static ParameterListSyntax BuildParameterList(IMethodSymbol method)
    {
        var parameters = method.Parameters.Select(p =>
        {
            var typeSyntax = ParseTypeName(p.Type.ToDisplayString()).WithTrailingTrivia(Space);
            return Parameter(Identifier(p.Name)).WithType(typeSyntax);
        });

        return ParameterList(SeparatedList(parameters));
    }

    private static ArgumentListSyntax BuildArgumentList(IMethodSymbol method)
    {
        var arguments = method.Parameters.Select(p => Argument(IdentifierName(p.Name)));
        return ArgumentList(SeparatedList(arguments));
    }

    private static string WrapInCompilationUnit(
        string? namespaceName,
        MemberDeclarationSyntax member,
        IEnumerable<string> extraUsings)
    {
        var usings = extraUsings
            .Distinct()
            .OrderBy(u => u)
            .Select(u => UsingDirective(ParseName(u).WithLeadingTrivia(Space))
                .WithTrailingTrivia(LineFeed))
            .ToArray();

        CompilationUnitSyntax compilationUnit;

        if (namespaceName != null)
        {
            var namespaceDecl = NamespaceDeclaration(ParseName(namespaceName).WithLeadingTrivia(Space))
                .WithMembers(SingletonList(member));

            compilationUnit = CompilationUnit()
                .WithUsings(List(usings))
                .WithMembers(SingletonList<MemberDeclarationSyntax>(namespaceDecl));
        }
        else
        {
            compilationUnit = CompilationUnit()
                .WithUsings(List(usings))
                .WithMembers(SingletonList(member));
        }

        return compilationUnit.NormalizeWhitespace().ToFullString();
    }
}
