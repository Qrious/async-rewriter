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
///
/// Type names, type parameters, generic constraints, and <c>using</c> directives are
/// taken directly from the original source syntax so the generated output matches the
/// coding style of the source file.  <c>#nullable enable</c> is propagated when present.
///
/// Methods that have <c>out</c> or <c>ref</c> parameters are skipped with a warning
/// because those are incompatible with async delegates.
/// </summary>
public sealed class AsyncWrapperGenerator
{
    // ──────────────────────────────────────────────────────────────────────────
    // Public entry point
    // ──────────────────────────────────────────────────────────────────────────

    public (string AsyncInterfaceSource, string AdapterSource) Generate(
        INamedTypeSymbol interfaceSymbol,
        InterfaceDeclarationSyntax interfaceDeclaration,
        CompilationUnitSyntax compilationUnit,
        SemanticModel semanticModel,
        string asyncHelperNamespace,
        out IReadOnlyList<string> warnings)
    {
        var warningList = new List<string>();
        warnings = warningList;

        var originalName = interfaceSymbol.Name;
        var asyncInterfaceName = originalName + "Async";

        var baseName = originalName.Length > 1 && originalName[0] == 'I'
            ? originalName[1..]
            : originalName;
        var adapterClassName = baseName + "AsyncAdapter";

        var eligibleMethods = GetEligibleMethods(interfaceDeclaration, originalName, warningList);
        var nullableEnable = HasNullableEnable(compilationUnit);

        var asyncInterfaceSource = BuildAsyncInterface(
            compilationUnit, asyncInterfaceName, interfaceDeclaration.TypeParameterList,
            interfaceDeclaration.ConstraintClauses, eligibleMethods, semanticModel, nullableEnable);

        var adapterSource = BuildAdapter(
            compilationUnit, adapterClassName, originalName, asyncInterfaceName,
            interfaceDeclaration.TypeParameterList, interfaceDeclaration.ConstraintClauses,
            asyncHelperNamespace, eligibleMethods, semanticModel, nullableEnable);

        return (asyncInterfaceSource, adapterSource);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Method selection
    // ──────────────────────────────────────────────────────────────────────────

    private static List<MethodDeclarationSyntax> GetEligibleMethods(
        InterfaceDeclarationSyntax interfaceDeclaration,
        string interfaceName,
        List<string> warnings)
    {
        var result = new List<MethodDeclarationSyntax>();

        foreach (var member in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            var hasOutOrRef = member.ParameterList.Parameters.Any(p =>
                p.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.RefKeyword)));

            if (hasOutOrRef)
            {
                warnings.Add(
                    $"Skipping {interfaceName}.{member.Identifier.ValueText}: " +
                    "out/ref parameters are incompatible with async delegates.");
                continue;
            }

            result.Add(member);
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Async interface generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAsyncInterface(
        CompilationUnitSyntax originalCompilationUnit,
        string asyncInterfaceName,
        TypeParameterListSyntax? typeParameterList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        List<MethodDeclarationSyntax> methods,
        SemanticModel semanticModel,
        bool nullableEnable)
    {
        var members = new List<MemberDeclarationSyntax>();

        foreach (var method in methods)
        {
            var (asyncReturnType, alreadyTaskBased) = GetAsyncReturnType(method, semanticModel);
            var asyncMethodName = AsyncMethodName(method.Identifier.ValueText, alreadyTaskBased);

            var methodDecl = MethodDeclaration(asyncReturnType, asyncMethodName)
                .WithTypeParameterList(method.TypeParameterList)
                .WithConstraintClauses(method.ConstraintClauses)
                .WithParameterList(method.ParameterList.WithoutTrivia())
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

            members.Add(methodDecl);
        }

        var namespaceName = GetNamespace(originalCompilationUnit);

        var interfaceDecl = InterfaceDeclaration(asyncInterfaceName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithAttributeLists(SingletonList(GeneratedCodeAttributeList()))
            .WithTypeParameterList(typeParameterList)
            .WithConstraintClauses(constraintClauses)
            .WithMembers(List(members));

        return WrapInCompilationUnit(
            originalCompilationUnit,
            namespaceName,
            interfaceDecl,
            extraUsings: ["System.Threading.Tasks"],
            nullableEnable);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Adapter class generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAdapter(
        CompilationUnitSyntax originalCompilationUnit,
        string adapterClassName,
        string originalInterfaceName,
        string asyncInterfaceName,
        TypeParameterListSyntax? typeParameterList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        string asyncHelperNamespace,
        List<MethodDeclarationSyntax> methods,
        SemanticModel semanticModel,
        bool nullableEnable)
    {
        // private readonly IFooAsync<T> _inner;
        var asyncInterfaceType = MakeTypeName(asyncInterfaceName, typeParameterList);
        var originalInterfaceType = MakeTypeName(originalInterfaceName, typeParameterList);

        var fieldDecl = FieldDeclaration(
                VariableDeclaration(asyncInterfaceType)
                    .WithVariables(SingletonSeparatedList(VariableDeclarator(Identifier("_inner")))))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword),
                Token(SyntaxKind.ReadOnlyKeyword)));

        // constructor
        var ctorDecl = ConstructorDeclaration(adapterClassName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList(SingletonSeparatedList(
                Parameter(Identifier("inner"))
                    .WithType(asyncInterfaceType))))
            .WithBody(Block(ExpressionStatement(
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName("_inner"),
                    IdentifierName("inner")))));

        var methodDecls = new List<MemberDeclarationSyntax> { fieldDecl, ctorDecl };

        foreach (var method in methods)
        {
            var (_, alreadyTaskBased) = GetAsyncReturnType(method, semanticModel);
            var isVoid = method.ReturnType is PredefinedTypeSyntax pt &&
                         pt.Keyword.IsKind(SyntaxKind.VoidKeyword);

            var asyncMethodName = AsyncMethodName(method.Identifier.ValueText, alreadyTaskBased);

            // _inner.MethodAsync<T>(p1, p2, ...)
            SimpleNameSyntax innerMethodName = method.TypeParameterList is { Parameters.Count: > 0 } tpl
                ? GenericName(Identifier(asyncMethodName))
                    .WithTypeArgumentList(TypeArgumentList(SeparatedList<TypeSyntax>(
                        tpl.Parameters.Select(tp => IdentifierName(tp.Identifier.ValueText)))))
                : IdentifierName(asyncMethodName);

            var innerCall = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("_inner"),
                    innerMethodName),
                BuildArgumentList(method));

            StatementSyntax bodyStatement;

            if (alreadyTaskBased)
            {
                // Already returns Task — delegate directly, no sync wrapper.
                bodyStatement = ReturnStatement(innerCall);
            }
            else
            {
                var lambda = ParenthesizedLambdaExpression(innerCall);
                var helperCall = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        IdentifierName("AsyncHelper"),
                        IdentifierName("RunTaskSynchronously")),
                    ArgumentList(SingletonSeparatedList(Argument(lambda))));

                bodyStatement = isVoid
                    ? ExpressionStatement(helperCall)
                    : ReturnStatement(helperCall);
            }

            var methodDecl = MethodDeclaration(method.ReturnType.WithoutTrivia(), method.Identifier.ValueText)
                .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                .WithTypeParameterList(method.TypeParameterList)
                .WithConstraintClauses(method.ConstraintClauses)
                .WithParameterList(method.ParameterList.WithoutTrivia())
                .WithBody(Block(bodyStatement));

            methodDecls.Add(methodDecl);
        }

        var namespaceName = GetNamespace(originalCompilationUnit);

        var classDecl = ClassDeclaration(adapterClassName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithAttributeLists(SingletonList(GeneratedCodeAttributeList()))
            .WithTypeParameterList(typeParameterList)
            .WithConstraintClauses(constraintClauses)
            .WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(
                SimpleBaseType(originalInterfaceType))))
            .WithMembers(List(methodDecls));

        return WrapInCompilationUnit(
            originalCompilationUnit,
            namespaceName,
            classDecl,
            extraUsings: [asyncHelperNamespace, "System.Threading.Tasks"],
            nullableEnable);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the async return type for the generated interface method and a flag
    /// indicating whether the original method was already Task-based.
    /// The returned type syntax is derived from the original source syntax, not from
    /// the fully-qualified symbol display string.
    /// </summary>
    private static (TypeSyntax ReturnType, bool AlreadyTaskBased) GetAsyncReturnType(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(method.ReturnType);
        var typeSymbol = typeInfo.Type;

        // void → Task
        if (typeSymbol?.SpecialType == SpecialType.System_Void)
        {
            return (IdentifierName("Task"), false);
        }

        // Already Task/ValueTask — keep the original syntax as written.
        if (IsTaskLikeSymbol(typeSymbol))
        {
            return (method.ReturnType.WithoutTrivia(), true);
        }

        // T → Task<T> using the original type syntax.
        var wrapped = GenericName(Identifier("Task"))
            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                method.ReturnType.WithoutTrivia())));

        return (wrapped, false);
    }

    /// <summary>
    /// Returns <c>Name</c> when there are no type parameters, or <c>Name&lt;T, U&gt;</c>
    /// when there are, using the parameter names as written in the source.
    /// </summary>
    private static TypeSyntax MakeTypeName(string name, TypeParameterListSyntax? tpl) =>
        tpl is { Parameters.Count: > 0 }
            ? GenericName(Identifier(name)).WithTypeArgumentList(
                TypeArgumentList(SeparatedList<TypeSyntax>(
                    tpl.Parameters.Select(tp => IdentifierName(tp.Identifier.ValueText)))))
            : IdentifierName(name);

    private static AttributeListSyntax GeneratedCodeAttributeList() =>
        AttributeList(SingletonSeparatedList(
            Attribute(
                QualifiedName(
                    QualifiedName(
                        QualifiedName(
                            IdentifierName("System"),
                            IdentifierName("CodeDom")),
                        IdentifierName("Compiler")),
                    IdentifierName("GeneratedCode")))
            .WithArgumentList(AttributeArgumentList(SeparatedList<AttributeArgumentSyntax>(
                new[]
                {
                    AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression,
                        Literal("AsyncRewriter"))),
                    AttributeArgument(LiteralExpression(SyntaxKind.StringLiteralExpression,
                        Literal("1.0")))
                })))));

    private static bool IsTaskLikeSymbol(ITypeSymbol? type)
    {
        if (type == null) return false;

        var name = type is INamedTypeSymbol { IsGenericType: true } generic
            ? generic.ConstructedFrom.ToDisplayString()
            : type.ToDisplayString();

        return name is
            "System.Threading.Tasks.Task" or
            "System.Threading.Tasks.Task<TResult>" or
            "System.Threading.Tasks.ValueTask" or
            "System.Threading.Tasks.ValueTask<TResult>";
    }

    private static string AsyncMethodName(string original, bool alreadyTaskBased) =>
        alreadyTaskBased || original.EndsWith("Async", StringComparison.Ordinal)
            ? original
            : original + "Async";

    private static ArgumentListSyntax BuildArgumentList(MethodDeclarationSyntax method)
    {
        var arguments = method.ParameterList.Parameters
            .Select(p => Argument(IdentifierName(p.Identifier.ValueText)));
        return ArgumentList(SeparatedList(arguments));
    }

    /// <summary>
    /// Extracts the namespace name from a file-scoped or block-scoped namespace declaration,
    /// or returns null for the global namespace.
    /// </summary>
    private static string? GetNamespace(CompilationUnitSyntax compilationUnit)
    {
        // File-scoped namespace: namespace Foo.Bar;
        var fileScoped = compilationUnit.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped != null)
            return fileScoped.Name.ToString();

        // Block-scoped namespace: namespace Foo.Bar { ... }
        var blockScoped = compilationUnit.Members
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (blockScoped != null)
            return blockScoped.Name.ToString();

        return null;
    }

    private static bool HasNullableEnable(CompilationUnitSyntax compilationUnit) =>
        compilationUnit.DescendantTrivia()
            .Where(t => t.HasStructure)
            .Select(t => t.GetStructure())
            .OfType<NullableDirectiveTriviaSyntax>()
            .Any(d => d.SettingToken.IsKind(SyntaxKind.EnableKeyword));

    private static string WrapInCompilationUnit(
        CompilationUnitSyntax originalCompilationUnit,
        string? namespaceName,
        MemberDeclarationSyntax member,
        IEnumerable<string> extraUsings,
        bool nullableEnable)
    {
        // Start with the original usings, then add any extras that aren't already present.
        var existingUsings = originalCompilationUnit.Usings;
        var existingNames = existingUsings
            .Select(u => u.Name?.ToString())
            .Where(n => n != null)
            .ToHashSet()!;

        var mergedUsings = existingUsings.ToList();
        foreach (var ns in extraUsings.Distinct())
        {
            if (!existingNames.Contains(ns))
            {
                mergedUsings.Add(UsingDirective(ParseName(ns)));
            }
        }

        CompilationUnitSyntax result;

        if (namespaceName != null)
        {
            // Preserve file-scoped namespace style when the original used it.
            var usedFileScoped = originalCompilationUnit.Members
                .OfType<FileScopedNamespaceDeclarationSyntax>()
                .Any();

            MemberDeclarationSyntax nsMember = usedFileScoped
                ? FileScopedNamespaceDeclaration(ParseName(namespaceName))
                    .WithMembers(SingletonList(member))
                : NamespaceDeclaration(ParseName(namespaceName))
                    .WithMembers(SingletonList(member));

            result = CompilationUnit()
                .WithUsings(List(mergedUsings))
                .WithMembers(SingletonList(nsMember));
        }
        else
        {
            result = CompilationUnit()
                .WithUsings(List(mergedUsings))
                .WithMembers(SingletonList(member));
        }

        var source = result.NormalizeWhitespace().ToFullString();

        if (nullableEnable)
            source = "#nullable enable\n" + source;

        return source;
    }
}
