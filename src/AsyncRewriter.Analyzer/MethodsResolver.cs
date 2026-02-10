using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Resolves method declarations from a syntax tree using an async visitor pattern
/// </summary>
public class MethodsResolver : AsyncCSharpSyntaxWalker, IMethodsResolver
{
    private readonly Dictionary<string, MethodNode> _methods = new();
    private SemanticModel _semanticModel = null!;
    private string _filePath = string.Empty;

    public async Task<IReadOnlyDictionary<string, MethodNode>> ResolveMethodsAsync(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        _methods.Clear();
        _semanticModel = semanticModel;
        _filePath = filePath;

        await VisitAsync(root, cancellationToken);

        return _methods;
    }

    public override async Task VisitMethodDeclarationAsync(MethodDeclarationSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol != null)
        {
            var methodNode = CreateMethodNode(node, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }

        // Don't recurse into method bodies for method discovery
    }

    public override async Task VisitInterfaceDeclarationAsync(InterfaceDeclarationSyntax node, CancellationToken cancellationToken = default)
    {
        var interfaceSymbol = _semanticModel.GetDeclaredSymbol(node);
        if (interfaceSymbol == null)
            return;

        foreach (var member in node.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodSymbol = _semanticModel.GetDeclaredSymbol(member) as IMethodSymbol;
            if (methodSymbol == null)
                continue;

            var methodNode = CreateInterfaceMethodNode(member, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }
    }

    private MethodNode CreateMethodNode(MethodDeclarationSyntax methodDecl, IMethodSymbol methodSymbol)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();

        var implementedInterfaces = new List<string>();

        foreach (var explicitImpl in methodSymbol.ExplicitInterfaceImplementations)
        {
            implementedInterfaces.Add(GetMethodId(explicitImpl));
        }

        var containingType = methodSymbol.ContainingType;
        if (containingType != null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var interfaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                    if (SymbolEqualityComparer.Default.Equals(implementation, methodSymbol))
                    {
                        implementedInterfaces.Add(GetMethodId(interfaceMember));
                    }
                }
            }
        }

        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsAsync = methodSymbol.IsAsync,
            Signature = GetMethodSignature(methodSymbol),
            SourceCode = methodDecl.ToFullString(),
            ImplementsInterfaceMethods = implementedInterfaces
        };
    }

    private MethodNode CreateInterfaceMethodNode(MethodDeclarationSyntax methodDecl, IMethodSymbol methodSymbol)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();

        var isReturnTypeParameter = false;
        int? returnTypeParameterIndex = null;

        if (methodSymbol.ReturnType is ITypeParameterSymbol typeParam &&
            methodSymbol.ContainingType is INamedTypeSymbol containingInterface &&
            containingInterface.IsGenericType)
        {
            for (int i = 0; i < containingInterface.TypeParameters.Length; i++)
            {
                var interfaceTypeParam = containingInterface.TypeParameters[i];
                if (SymbolEqualityComparer.Default.Equals(interfaceTypeParam, typeParam))
                {
                    if (interfaceTypeParam.Variance == VarianceKind.Out)
                    {
                        isReturnTypeParameter = true;
                        returnTypeParameterIndex = i;
                    }
                    break;
                }
            }
        }

        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsAsync = false,
            IsInterfaceMethod = true,
            IsReturnTypeParameter = isReturnTypeParameter,
            ReturnTypeParameterIndex = returnTypeParameterIndex,
            Signature = GetMethodSignature(methodSymbol),
            SourceCode = methodDecl.ToFullString()
        };
    }

    internal static string GetMethodId(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        return $"{originalMethod.ContainingType?.ToDisplayString()}.{GetMethodSignature(originalMethod)}";
    }

    internal static string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{originalMethod.Name}({parameters})";
    }
}
