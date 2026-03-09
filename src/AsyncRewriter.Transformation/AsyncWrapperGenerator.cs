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

    public string Generate(
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
        var asyncInterfaceName = originalName;

        // Async interface: only direct members (inherited ones come via the async base interfaces).
        var directMembers = GetEligibleDirectMembers(interfaceDeclaration, originalName, warningList);

        var nullableEnable = HasNullableEnable(compilationUnit);

        var asyncInterfaceSource = BuildAsyncInterface(
            compilationUnit, asyncInterfaceName, interfaceDeclaration.TypeParameterList,
            interfaceDeclaration.ConstraintClauses, interfaceDeclaration.BaseList,
            postfix, directMembers, semanticModel, nullableEnable);

        return asyncInterfaceSource;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Member selection
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the methods and properties declared directly on the interface (for the async interface).
    /// </summary>
    private static List<MemberDeclarationSyntax> GetEligibleDirectMembers(
        InterfaceDeclarationSyntax interfaceDeclaration,
        string interfaceName,
        List<string> warnings)
    {
        var result = new List<MemberDeclarationSyntax>();

        foreach (var member in interfaceDeclaration.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    if (HasOutOrRef(method))
                    {
                        warnings.Add(
                            $"Skipping {interfaceName}.{method.Identifier.ValueText}: " +
                            "out/ref parameters are incompatible with async delegates.");
                        continue;
                    }
                    result.Add(method);
                    break;

                case PropertyDeclarationSyntax:
                    result.Add(member);
                    break;
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
        List<MemberDeclarationSyntax> directMembers,
        SemanticModel semanticModel,
        bool nullableEnable)
    {
        var members = new List<MemberDeclarationSyntax>();

        foreach (var member in directMembers)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                {
                    var (asyncReturnType, alreadyTaskBased) = GetAsyncReturnType(method, semanticModel);
                    var asyncMethodName = AsyncMethodName(method.Identifier.ValueText, alreadyTaskBased);
                    var parametersWithCancellationToken = method.ParameterList;
                    // Add a CancellationToken parameter. If the method has a 'params' parameter,
                    // insert the cancellation token before the params parameter so the params
                    // argument remains the last parameter in the signature.
                    var ctParam = Parameter(Identifier("cancellationToken"))
                        .WithType(IdentifierName("CancellationToken"))
                        .WithDefault(EqualsValueClause(
                            LiteralExpression(SyntaxKind.DefaultLiteralExpression)));

                    var parameters = parametersWithCancellationToken.Parameters;
                    // Find first parameter that has the 'params' modifier.
                    var paramsIndex = -1;
                    for (int i = 0; i < parameters.Count; i++)
                    {
                        if (parameters[i].Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                        {
                            paramsIndex = i;
                            break;
                        }
                    }

                    parametersWithCancellationToken = paramsIndex >= 0
                        ? parametersWithCancellationToken.WithParameters(parameters.Insert(paramsIndex, ctParam))
                        : parametersWithCancellationToken.AddParameters(ctParam);

                    var methodDecl = MethodDeclaration(asyncReturnType, asyncMethodName)
                        .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
                        .WithTypeParameterList(method.TypeParameterList)
                        .WithConstraintClauses(method.ConstraintClauses)
                        .WithParameterList(parametersWithCancellationToken.WithoutTrivia())
                        .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                        .WithTriviaFrom(method);

                    members.Add(methodDecl);
                    break;
                }
                case PropertyDeclarationSyntax property:
                    // Properties are copied verbatim — they cannot be async.
                    members.Add(property);
                    break;
            }
        }

        var namespaceName = GetNamespace(originalCompilationUnit);
        var asyncBaseList = BuildAsyncBaseList(baseList, postfix);

        var originalInterface = originalCompilationUnit.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .Single(id => id.Identifier.ValueText == asyncInterfaceName);

        var interfaceDecl = InterfaceDeclaration(asyncInterfaceName)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithTypeParameterList(typeParameterList)
            .WithConstraintClauses(constraintClauses)
            .WithTriviaFrom(originalInterface)
            .WithBaseList(asyncBaseList)
            .WithMembers(List(members));

        return WrapInCompilationUnit(
            originalCompilationUnit, namespaceName, interfaceDecl,
            extraUsings: ["System.Threading.Tasks", "System.Threading"], nullableEnable);
    }


    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static (TypeSyntax ReturnType, bool AlreadyTaskBased) GetAsyncReturnType(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
        => GetAsyncReturnType(
            semanticModel.GetTypeInfo(method.ReturnType).Type,
            method.ReturnType);

    /// <summary>
    /// Overload used when type info comes directly from a symbol (constructed generic base).
    /// <paramref name="concreteSyntax"/> is the already-substituted return type syntax built
    /// from the symbol (e.g. <c>User</c> instead of <c>T</c>).
    /// </summary>
    private static (TypeSyntax ReturnType, bool AlreadyTaskBased) GetAsyncReturnType(
        ITypeSymbol? typeSymbol,
        TypeSyntax concreteSyntax)
    {
        if (typeSymbol?.SpecialType == SpecialType.System_Void)
        {
            return (IdentifierName("Task"), false);
        }

        if (IsTaskLikeSymbol(typeSymbol))
        {
            return (concreteSyntax.WithoutTrivia(), true);
        }

        var wrapped = GenericName(Identifier("Task"))
            .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                concreteSyntax.WithoutTrivia())));

        return (wrapped, false);
    }

    private static BaseListSyntax? BuildAsyncBaseList(BaseListSyntax? baseList, string? postfix)
    {
        if (baseList == null)
        {
            return null;
        }

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
                {
                    return null;
                }

                return IdentifierName(name + "Async");
            }
            case GenericNameSyntax generic:
            {
                var name = generic.Identifier.ValueText;
                if (!name.EndsWith(postfix, StringComparison.Ordinal) ||
                    name.EndsWith("Async", StringComparison.Ordinal))
                {
                    return null;
                }

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

    private static bool IsTaskLikeSymbol(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

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

    private static string? GetNamespace(CompilationUnitSyntax compilationUnit)
    {
        var fileScoped = compilationUnit.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped != null)
        {
            return fileScoped.Name.ToString();
        }

        var blockScoped = compilationUnit.Members
            .OfType<NamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (blockScoped != null)
        {
            return blockScoped.Name.ToString();
        }

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
                {
                    mergedUsings.Add(u.WithoutTrivia());
                }
            }
        }

        // Add programmatic extras (e.g. System.Threading.Tasks, AsyncHelper namespace).
        foreach (var ns in extraUsings.Distinct())
        {
            if (seenNames.Add(ns))
            {
                mergedUsings.Add(UsingDirective(ParseName(ns)));
            }
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
                    .WithTrailingTrivia(Whitespace(Environment.NewLine))
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

        // NormalizeWhitespace strips all trivia, so add blank lines between
        // interface members (lines starting with 4-space indent + "public " or "/// ") here.
        source = System.Text.RegularExpressions.Regex.Replace(
            source, @";(\r?\n)( {4}(public |/// ))", ";\r\n\r\n$2");

        if (nullableEnable)
        {
            source = "#nullable enable\n" + source;
        }

        return source;
    }
}
