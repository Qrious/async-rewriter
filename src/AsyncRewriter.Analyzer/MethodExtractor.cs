using System;
using System.Collections.Concurrent;
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
public class MethodExtractor : AsyncCSharpSyntaxWalker, IMethodExtractor
{
    private SemanticModel _semanticModel = null!;
    private string _filePath = string.Empty;
    private ConcurrentDictionary<string, MethodNode> _methods = null!;
    private ConcurrentBag<InterfaceImplementation> _interfaceImplementations = null!;
    private ConcurrentBag<MethodOverride> _methodOverrides = null!;
    private Guid _callGraphId;

    public async Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<InterfaceImplementation> interfaceImplementations,
        ConcurrentBag<MethodOverride> methodOverrides,
        CancellationToken cancellationToken = default)
    {
        _callGraphId = callGraphId;
        _methods = methods;
        _interfaceImplementations = interfaceImplementations;
        _methodOverrides = methodOverrides;
        _semanticModel = semanticModel;
        _filePath = filePath;

        await VisitAsync(root, cancellationToken);
    }

    public override async Task VisitMethodDeclarationAsync(MethodDeclarationSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol != null)
        {
            var methodNode = CreateMethodNode(node, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }

        // Recurse into method body to find local functions
        await DefaultVisitAsync(node, cancellationToken);
    }

    public override async Task VisitLocalFunctionStatementAsync(LocalFunctionStatementSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol != null)
        {
            var methodNode = CreateMethodNode(node, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }

        // Recurse to find nested local functions
        await DefaultVisitAsync(node, cancellationToken);
    }

    public override async Task VisitParenthesizedLambdaExpressionAsync(ParenthesizedLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol != null)
        {
            var methodNode = CreateMethodNode(node, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }

        await DefaultVisitAsync(node, cancellationToken);
    }

    public override async Task VisitSimpleLambdaExpressionAsync(SimpleLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol != null)
        {
            var methodNode = CreateMethodNode(node, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }

        await DefaultVisitAsync(node, cancellationToken);
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

    private MethodNode CreateMethodNode(SyntaxNode methodDecl, IMethodSymbol methodSymbol)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();

        var name = methodSymbol.Name;
        if (methodSymbol.MethodKind == MethodKind.AnonymousFunction)
        {
            var containingName = (methodSymbol.ContainingSymbol as IMethodSymbol)?.Name ?? "";
            var line = lineSpan.StartLinePosition.Line;
            name = $"<{containingName}>b__{line}";
        }

        var methodId = GetMethodId(methodSymbol);

        foreach (var explicitImpl in methodSymbol.ExplicitInterfaceImplementations)
        {
            _interfaceImplementations.Add(new InterfaceImplementation
            {
                CallGraphId = _callGraphId.ToString(),
                ImplementingMethodId = methodId,
                InterfaceMethodId = GetMethodId(explicitImpl),
            });
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
                        _interfaceImplementations.Add(new InterfaceImplementation
                        {
                            CallGraphId = _callGraphId.ToString(),
                            ImplementingMethodId = methodId,
                            InterfaceMethodId = GetMethodId(interfaceMember),
                        });
                    }
                }
            }
        }

        if (methodSymbol.IsOverride)
        {
            var overridden = methodSymbol.OverriddenMethod;
            while (overridden != null)
            {
                var baseMethodId = GetMethodId(overridden);
                _methodOverrides.Add(new MethodOverride
                {
                    CallGraphId = _callGraphId.ToString(),
                    OverridingMethodId = methodId,
                    BaseMethodId = baseMethodId,
                });

                // Ensure the base method node exists (use OriginalDefinition for consistent generic type display)
                var overriddenOriginal = overridden.OriginalDefinition;
                _methods.TryAdd(baseMethodId, new MethodNode
                {
                    CallGraphId = _callGraphId.ToString(),
                    Id = baseMethodId,
                    Name = overriddenOriginal.Name,
                    ContainingType = overriddenOriginal.ContainingType?.ToDisplayString() ?? "",
                    ContainingNamespace = overriddenOriginal.ContainingNamespace?.ToDisplayString() ?? "",
                    ReturnType = overriddenOriginal.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Parameters = overriddenOriginal.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
                    FilePath = overridden.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "external",
                    StartLine = overridden.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                    EndLine = overridden.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
                });

                overridden = overridden.OverriddenMethod;
            }
        }

        return new MethodNode
        {
            CallGraphId = _callGraphId.ToString(),
            Id = GetMethodId(methodSymbol),
            Name = name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
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
            CallGraphId = _callGraphId.ToString(),
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
        };
    }

    internal static string GetMethodId(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;

        // For local functions and lambdas, build the full chain: Type.ParentMethod(params).LocalFunc(params)
        if (originalMethod.MethodKind == MethodKind.LocalFunction || originalMethod.MethodKind == MethodKind.AnonymousFunction)
        {
            var parts = new List<string>();
            var current = originalMethod;
            while (current != null && (current.MethodKind == MethodKind.LocalFunction || current.MethodKind == MethodKind.AnonymousFunction))
            {
                if (current.MethodKind == MethodKind.AnonymousFunction)
                {
                    // Synthesize IL-style name: <ContainingMethod>b__<line>
                    var containingName = (current.ContainingSymbol as IMethodSymbol)?.Name ?? "";
                    var location = current.Locations.FirstOrDefault();
                    var line = location?.GetLineSpan().StartLinePosition.Line ?? 0;
                    var lambdaName = $"<{containingName}>b__{line}";
                    var parameters = string.Join(", ", current.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    parts.Add($"{lambdaName}({parameters})");
                }
                else
                {
                    parts.Add(GetMethodSignature(current));
                }
                current = current.ContainingSymbol as IMethodSymbol;
            }

            // current is now the outermost non-local method (or null)
            if (current != null)
                parts.Add(GetMethodSignature(current));

            parts.Reverse();
            var containingType = originalMethod.ContainingType?.ToDisplayString() ?? "";
            return $"{containingType}.{string.Join(".", parts)}";
        }

        return $"{originalMethod.ContainingType?.ToDisplayString()}.{GetMethodSignature(originalMethod)}";
    }

    internal static string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{originalMethod.Name}({parameters})";
    }
}
