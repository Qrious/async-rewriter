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
///     <c>AsyncHelper.RunTaskSynchronously</c>.  The adapter includes methods inherited
///     from all base interfaces in the hierarchy.
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
        Compilation compilation,
        string asyncHelperNamespace,
        string? postfix,
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

        // Async interface: only direct members (inherited ones come via the async base interfaces).
        var directMethods = GetEligibleDirectMethods(interfaceDeclaration, originalName, warningList);

        // Adapter: must implement every method in the full hierarchy.
        var allAdapterMethods = GetAllAdapterMethods(
            interfaceSymbol, interfaceDeclaration, semanticModel, compilation, warningList);

        // Collect compilation units from all base interface files so their usings
        // are available in the adapter.
        var baseCompilationUnits = interfaceSymbol.AllInterfaces
            .SelectMany(i => i.DeclaringSyntaxReferences)
            .Select(r => r.SyntaxTree.GetRoot())
            .OfType<CompilationUnitSyntax>()
            .ToList();

        var nullableEnable = HasNullableEnable(compilationUnit);

        var asyncInterfaceSource = BuildAsyncInterface(
            compilationUnit, asyncInterfaceName, interfaceDeclaration.TypeParameterList,
            interfaceDeclaration.ConstraintClauses, interfaceDeclaration.BaseList,
            postfix, directMethods, semanticModel, nullableEnable);

        var adapterSource = BuildAdapter(
            compilationUnit, baseCompilationUnits, adapterClassName, originalName, asyncInterfaceName,
            interfaceDeclaration.TypeParameterList, interfaceDeclaration.ConstraintClauses,
            asyncHelperNamespace, allAdapterMethods, nullableEnable);

        return (asyncInterfaceSource, adapterSource);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Method selection
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns only the methods declared directly on the interface (for the async interface).
    /// </summary>
    private static List<MethodDeclarationSyntax> GetEligibleDirectMethods(
        InterfaceDeclarationSyntax interfaceDeclaration,
        string interfaceName,
        List<string> warnings)
    {
        var result = new List<MethodDeclarationSyntax>();

        foreach (var member in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (HasOutOrRef(member))
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

    /// <summary>
    /// Returns all eligible methods from the full interface hierarchy (for the adapter).
    /// Direct methods come first, followed by each base interface's methods in order.
    /// Duplicate signatures (same display string) are skipped.
    /// </summary>
    private static List<(MethodDeclarationSyntax Method, SemanticModel Model)> GetAllAdapterMethods(
        INamedTypeSymbol interfaceSymbol,
        InterfaceDeclarationSyntax interfaceDeclaration,
        SemanticModel semanticModel,
        Compilation compilation,
        List<string> warnings)
    {
        var result = new List<(MethodDeclarationSyntax, SemanticModel)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Direct methods.
        foreach (var member in interfaceDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (HasOutOrRef(member))
                continue; // already warned in GetEligibleDirectMethods

            var symbol = semanticModel.GetDeclaredSymbol(member);
            if (symbol == null || !seen.Add(symbol.ToDisplayString()))
                continue;

            result.Add((member, semanticModel));
        }

        // Inherited methods from all base interfaces.
        foreach (var baseInterface in interfaceSymbol.AllInterfaces)
        {
            foreach (var member in baseInterface.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary)
                    continue;

                if (!seen.Add(member.ToDisplayString()))
                    continue;

                if (member.Parameters.Any(p => p.RefKind is RefKind.Out or RefKind.Ref))
                {
                    warnings.Add(
                        $"Skipping inherited {baseInterface.Name}.{member.Name}: " +
                        "out/ref parameters are incompatible with async delegates.");
                    continue;
                }

                var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
                if (syntaxRef == null) continue;

                if (syntaxRef.GetSyntax() is not MethodDeclarationSyntax methodSyntax) continue;

                var model = compilation.GetSemanticModel(syntaxRef.SyntaxTree);
                result.Add((methodSyntax, model));
            }
        }

        return result;
    }

    private static bool HasOutOrRef(MethodDeclarationSyntax method) =>
        method.ParameterList.Parameters.Any(p =>
            p.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.RefKeyword)));

    // ──────────────────────────────────────────────────────────────────────────
    // Async interface generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAsyncInterface(
        CompilationUnitSyntax originalCompilationUnit,
        string asyncInterfaceName,
        TypeParameterListSyntax? typeParameterList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        BaseListSyntax? baseList,
        string? postfix,
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
        var asyncBaseList = BuildAsyncBaseList(baseList, postfix);

        var interfaceDecl = InterfaceDeclaration(asyncInterfaceName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithAttributeLists(SingletonList(GeneratedCodeAttributeList()))
            .WithTypeParameterList(typeParameterList)
            .WithConstraintClauses(constraintClauses)
            .WithBaseList(asyncBaseList)
            .WithMembers(List(members));

        return WrapInCompilationUnit(
            originalCompilationUnit, namespaceName, interfaceDecl,
            extraUsings: ["System.Threading.Tasks"], nullableEnable);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Adapter class generation
    // ──────────────────────────────────────────────────────────────────────────

    private static string BuildAdapter(
        CompilationUnitSyntax originalCompilationUnit,
        List<CompilationUnitSyntax> baseCompilationUnits,
        string adapterClassName,
        string originalInterfaceName,
        string asyncInterfaceName,
        TypeParameterListSyntax? typeParameterList,
        SyntaxList<TypeParameterConstraintClauseSyntax> constraintClauses,
        string asyncHelperNamespace,
        List<(MethodDeclarationSyntax Method, SemanticModel Model)> methods,
        bool nullableEnable)
    {
        var asyncInterfaceType = MakeTypeName(asyncInterfaceName, typeParameterList);
        var originalInterfaceType = MakeTypeName(originalInterfaceName, typeParameterList);

        // private readonly IFooAsync<T> _inner;
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
                Parameter(Identifier("inner")).WithType(asyncInterfaceType))))
            .WithBody(Block(ExpressionStatement(
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName("_inner"),
                    IdentifierName("inner")))));

        var methodDecls = new List<MemberDeclarationSyntax> { fieldDecl, ctorDecl };

        foreach (var (method, model) in methods)
        {
            var (_, alreadyTaskBased) = GetAsyncReturnType(method, model);
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
            originalCompilationUnit, baseCompilationUnits, namespaceName, classDecl,
            extraUsings: [asyncHelperNamespace, "System.Threading.Tasks"], nullableEnable);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static (TypeSyntax ReturnType, bool AlreadyTaskBased) GetAsyncReturnType(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
    {
        var typeSymbol = semanticModel.GetTypeInfo(method.ReturnType).Type;

        if (typeSymbol?.SpecialType == SpecialType.System_Void)
            return (IdentifierName("Task"), false);

        if (IsTaskLikeSymbol(typeSymbol))
            return (method.ReturnType.WithoutTrivia(), true);

        var wrapped = GenericName(Identifier("Task"))
            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                method.ReturnType.WithoutTrivia())));

        return (wrapped, false);
    }

    private static BaseListSyntax? BuildAsyncBaseList(BaseListSyntax? baseList, string? postfix)
    {
        if (baseList == null) return null;

        var rewritten = baseList.Types.Select(bt =>
        {
            var asyncType = postfix != null ? TryMakeAsyncTypeSyntax(bt.Type, postfix) : null;
            return asyncType != null ? (BaseTypeSyntax)SimpleBaseType(asyncType) : bt;
        });

        return BaseList(SeparatedList(rewritten));
    }

    private static TypeSyntax? TryMakeAsyncTypeSyntax(TypeSyntax type, string postfix)
    {
        switch (type)
        {
            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.ValueText;
                if (!name.EndsWith(postfix, StringComparison.Ordinal) ||
                    name.EndsWith("Async", StringComparison.Ordinal))
                    return null;
                return IdentifierName(name + "Async");
            }
            case GenericNameSyntax generic:
            {
                var name = generic.Identifier.ValueText;
                if (!name.EndsWith(postfix, StringComparison.Ordinal) ||
                    name.EndsWith("Async", StringComparison.Ordinal))
                    return null;
                return generic.WithIdentifier(Identifier(name + "Async"));
            }
            case QualifiedNameSyntax qualified:
            {
                var asyncRight = TryMakeAsyncTypeSyntax(qualified.Right, postfix);
                return asyncRight != null
                    ? qualified.WithRight((SimpleNameSyntax)asyncRight)
                    : null;
            }
            default:
                return null;
        }
    }

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

    private static string? GetNamespace(CompilationUnitSyntax compilationUnit)
    {
        var fileScoped = compilationUnit.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped != null)
            return fileScoped.Name.ToString();

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
        bool nullableEnable) =>
        WrapInCompilationUnit(originalCompilationUnit, [], namespaceName, member, extraUsings, nullableEnable);

    private static string WrapInCompilationUnit(
        CompilationUnitSyntax originalCompilationUnit,
        List<CompilationUnitSyntax> additionalCompilationUnits,
        string? namespaceName,
        MemberDeclarationSyntax member,
        IEnumerable<string> extraUsings,
        bool nullableEnable)
    {
        var mergedUsings = originalCompilationUnit.Usings.ToList();
        var seenNames = mergedUsings
            .Select(u => u.Name?.ToString())
            .Where(n => n != null)
            .ToHashSet()!;

        // Pull in usings from base interface files.
        foreach (var cu in additionalCompilationUnits)
        {
            foreach (var u in cu.Usings)
            {
                var name = u.Name?.ToString();
                if (name != null && seenNames.Add(name))
                    mergedUsings.Add(u.WithoutTrivia());
            }
        }

        // Add programmatic extras (e.g. System.Threading.Tasks, AsyncHelper namespace).
        foreach (var ns in extraUsings.Distinct())
        {
            if (seenNames.Add(ns))
                mergedUsings.Add(UsingDirective(ParseName(ns)));
        }

        CompilationUnitSyntax result;

        if (namespaceName != null)
        {
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
