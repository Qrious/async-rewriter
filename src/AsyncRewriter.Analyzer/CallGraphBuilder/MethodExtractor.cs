using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core;
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
    private ConcurrentDictionary<string, IMethodNode> _methods = null!;
    private ConcurrentDictionary<string, IInterfaceImplementation> _interfaceImplementations = null!;
    private ConcurrentDictionary<string, IMethodOverride> _methodOverrides = null!;
    private ConcurrentDictionary<string, IGenericInstantiation> _genericInstantiations = null!;
    private string _callGraphId;

    public Task Extract(
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IInterfaceImplementation> interfaceImplementations,
        ConcurrentDictionary<string, IMethodOverride> methodOverrides,
        CancellationToken cancellationToken = default)
    {
        return Extract(callGraphId, root, semanticModel, filePath, methods, interfaceImplementations, methodOverrides, new ConcurrentDictionary<string, IGenericInstantiation>(), cancellationToken);
    }

    public async Task Extract(
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IInterfaceImplementation> interfaceImplementations,
        ConcurrentDictionary<string, IMethodOverride> methodOverrides,
        ConcurrentDictionary<string, IGenericInstantiation> genericInstantiations,
        CancellationToken cancellationToken = default)
    {
        _callGraphId = callGraphId;
        _methods = methods;
        _interfaceImplementations = interfaceImplementations;
        _methodOverrides = methodOverrides;
        _genericInstantiations = genericInstantiations;
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
        {
            return;
        }

        foreach (var member in node.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodSymbol = _semanticModel.GetDeclaredSymbol(member) as IMethodSymbol;
            if (methodSymbol == null)
            {
                continue;
            }

            var methodNode = CreateInterfaceMethodNode(member, methodSymbol);
            _methods[methodNode.Id] = methodNode;
        }
    }

    private MethodNode CreateMethodNode(SyntaxNode methodDecl, IMethodSymbol methodSymbol)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();
        bool isReturnTypeParam = false;

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
            var explicitContainingType = explicitImpl.ContainingType;
            var isGenericInstantiation = explicitContainingType is INamedTypeSymbol nt
                && nt.IsGenericType
                && !SymbolEqualityComparer.Default.Equals(nt, nt.OriginalDefinition);

            if (isGenericInstantiation)
            {
                var instantiatedId = GetInstantiatedMethodId(explicitImpl);
                var genericId = GetMethodId(explicitImpl);
                var implementation = new InterfaceImplementation
                {
                    CallGraphId = _callGraphId,
                    ImplementingMethodId = methodId,
                    InterfaceMethodId = instantiatedId,
                };
                _interfaceImplementations.TryAdd(implementation.Id, implementation);
                var genericInstantation = new GenericInstantiation
                {
                    CallGraphId = _callGraphId,
                    InstantiatedMethodId = instantiatedId,
                    GenericMethodId = genericId,
                };
                _genericInstantiations.TryAdd(genericInstantation.Id, genericInstantation);
            }
            else
            {
                var implementation = new InterfaceImplementation
                {
                    CallGraphId = _callGraphId,
                    ImplementingMethodId = methodId,
                    InterfaceMethodId = GetMethodId(explicitImpl),
                };
                _interfaceImplementations.TryAdd(implementation.Id, implementation);
            }
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
                        var genericMethodId = GetMethodId(interfaceMember);
                        var originalMember = interfaceMember.OriginalDefinition;

                        // Check if this is a generic interface with instantiated type args
                        var isGenericInstantiation = iface.IsGenericType
                            && !SymbolEqualityComparer.Default.Equals(iface, iface.OriginalDefinition);

                        if (isGenericInstantiation)
                        {
                            // Create instantiated node: IMapper<Foo, Bar>.Map(Foo)
                            var instantiatedMethodId = GetInstantiatedMethodId(interfaceMember);

                            isReturnTypeParam = originalMember.ReturnType is ITypeParameterSymbol tp2
                                                && originalMember.ContainingType is { } ci2
                                                && ci2.IsGenericType
                                                && ci2.TypeParameters.Any(p =>
                                                    SymbolEqualityComparer.Default.Equals(p, tp2) && p.Variance == VarianceKind.Out);

                            _methods.TryAdd(instantiatedMethodId, new MethodNode
                            {
                                CallGraphId = _callGraphId,
                                Id = instantiatedMethodId,
                                Name = interfaceMember.Name,
                                ContainingType = interfaceMember.ContainingType?.ToDisplayString() ?? "",
                                ContainingNamespace = interfaceMember.ContainingNamespace?.ToDisplayString() ?? "",
                                ReturnType = interfaceMember.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                Parameters = interfaceMember.Parameters.Select(p => new MethodParameter { Type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Name = p.Name, RefKind = ToRefKindString(p.RefKind) }).ToList(),
                                FilePath = interfaceMember.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "external",
                                StartLine = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                                EndLine = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
                                StartCharacter = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Character ?? 0,
                                EndCharacter = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Character ?? 0,
                                IsReturnTypeParameter = isReturnTypeParam,
                                IsInterfaceMethod = true,
                            });

                            // InterfaceImplementation: implementing method → instantiated
                            var interfaceImplementation = new InterfaceImplementation
                            {
                                CallGraphId = _callGraphId,
                                ImplementingMethodId = methodId,
                                InterfaceMethodId = instantiatedMethodId,
                            };
                            _interfaceImplementations.TryAdd(interfaceImplementation.Id, interfaceImplementation);

                            // GenericInstantiation: instantiated → generic
                            var genericInstantation = new GenericInstantiation
                            {
                                CallGraphId = _callGraphId,
                                InstantiatedMethodId = instantiatedMethodId,
                                GenericMethodId = genericMethodId,
                            };
                            _genericInstantiations.TryAdd(genericInstantation.Id, genericInstantation);
                        }
                        else
                        {
                            // Non-generic interface: link directly
                            var directImplementation = new InterfaceImplementation
                            {
                                CallGraphId = _callGraphId,
                                ImplementingMethodId = methodId,
                                InterfaceMethodId = genericMethodId,
                            };
                            _interfaceImplementations.TryAdd(directImplementation.Id, directImplementation);
                        }

                        // Ensure the generic interface method node exists
                        isReturnTypeParam = originalMember.ReturnType is ITypeParameterSymbol tp
                                            && originalMember.ContainingType is INamedTypeSymbol ci
                                            && ci.IsGenericType
                                            && ci.TypeParameters.Any(p =>
                                                SymbolEqualityComparer.Default.Equals(p, tp) && p.Variance == VarianceKind.Out);

                        _methods.TryAdd(genericMethodId, new MethodNode
                        {
                            CallGraphId = _callGraphId,
                            Id = genericMethodId,
                            Name = originalMember.Name,
                            ContainingType = originalMember.ContainingType?.ToDisplayString() ?? "",
                            ContainingNamespace = originalMember.ContainingNamespace?.ToDisplayString() ?? "",
                            ReturnType = originalMember.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                             Parameters = originalMember.Parameters.Select(p => new MethodParameter { Type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Name = p.Name, RefKind = ToRefKindString(p.RefKind) }).ToList(),
                            FilePath = interfaceMember.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "external",
                            StartLine = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                            EndLine = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
                            StartCharacter = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Character ?? 0,
                            EndCharacter = interfaceMember.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Character ?? 0,
                            IsReturnTypeParameter = isReturnTypeParam,
                            IsInterfaceMethod = true,
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
                var methodOverride = new MethodOverride
                {
                    CallGraphId = _callGraphId,
                    OverridingMethodId = methodId,
                    BaseMethodId = baseMethodId,
                };
                _methodOverrides.TryAdd(methodOverride.Id, methodOverride);

                // Ensure the base method node exists (use OriginalDefinition for consistent generic type display)
                var overriddenOriginal = overridden.OriginalDefinition;
                isReturnTypeParam = overriddenOriginal.ReturnType is ITypeParameterSymbol tp
                                        && overriddenOriginal.ContainingType is INamedTypeSymbol ci
                                        && ci.IsGenericType
                                        && ci.TypeParameters.Any(p =>
                                            SymbolEqualityComparer.Default.Equals(p, tp) && p.Variance == VarianceKind.Out);
                _methods.TryAdd(baseMethodId, new MethodNode
                {
                    CallGraphId = _callGraphId,
                    Id = baseMethodId,
                    Name = overriddenOriginal.Name,
                    ContainingType = overriddenOriginal.ContainingType?.ToDisplayString() ?? "",
                    ContainingNamespace = overriddenOriginal.ContainingNamespace?.ToDisplayString() ?? "",
                    ReturnType = overriddenOriginal.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                     Parameters = overriddenOriginal.Parameters.Select(p => new MethodParameter { Type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Name = p.Name, RefKind = ToRefKindString(p.RefKind) }).ToList(),
                    FilePath = overridden.Locations.FirstOrDefault()?.SourceTree?.FilePath ?? "external",
                    StartLine = overridden.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                    EndLine = overridden.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
                    StartCharacter = overridden.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Character ?? 0,
                    EndCharacter = overridden.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Character ?? 0,
                    IsReturnTypeParameter = isReturnTypeParam
                });

                overridden = overridden.OverriddenMethod;
            }
        }


        return new MethodNode
        {
            CallGraphId = _callGraphId,
            Id = GetMethodId(methodSymbol),
            Name = name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => new MethodParameter { Type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Name = p.Name, RefKind = ToRefKindString(p.RefKind) }).ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            StartCharacter = lineSpan.StartLinePosition.Character,
            EndCharacter = lineSpan.EndLinePosition.Character,
            IsReturnTypeParameter = isReturnTypeParam
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
            CallGraphId = _callGraphId,
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => new MethodParameter { Type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), Name = p.Name, RefKind = ToRefKindString(p.RefKind) }).ToList(),
            FilePath = _filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            StartCharacter = lineSpan.StartLinePosition.Character,
            EndCharacter = lineSpan.EndLinePosition.Character,
            IsReturnTypeParameter = isReturnTypeParameter,
            IsInterfaceMethod = true,
        };
    }

    internal static string? ToRefKindString(RefKind refKind) => refKind switch
    {
        RefKind.Out => "out",
        RefKind.Ref => "ref",
        RefKind.In => "in",
        RefKind.RefReadOnlyParameter => "in",
        _ => null,
    };

    /// <summary>
    /// Gets a method ID that preserves the instantiated containing type (e.g. IMapper&lt;Foo, Bar&gt;.Map(Foo))
    /// instead of using OriginalDefinition for the containing type.
    /// </summary>
    internal static string GetInstantiatedMethodId(IMethodSymbol methodSymbol)
    {
        var containingType = methodSymbol.ContainingType?.ToDisplayString() ?? "";
        var parameters = string.Join(", ", methodSymbol.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{containingType}.{methodSymbol.Name}({parameters})";
    }

    // Delegate to shared MethodIdFactory in Core so the ID format stays in sync
    internal static string GetMethodId(IMethodSymbol methodSymbol) =>
        MethodIdFactory.GetMethodId(methodSymbol);

    internal static string GetMethodSignature(IMethodSymbol methodSymbol) =>
        MethodIdFactory.GetMethodSignature(methodSymbol);
}
