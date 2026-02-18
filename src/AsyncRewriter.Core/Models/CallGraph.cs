using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents the complete call graph for a codebase or project
/// </summary>
public class CallGraph : ICallGraph
{
    public string Id { get; }

    public ConcurrentDictionary<string, IMethodNode> Methods { get; } = new();

    public ConcurrentBag<IMethodCall> Calls { get; }

    public ConcurrentBag<IInterfaceImplementation> InterfaceImplementations { get; } = [];

    public ConcurrentBag<IMethodOverride> MethodOverrides { get; } = [];

    public ConcurrentBag<IGenericInstantiation> GenericInstantiations { get; } = [];

    public ConcurrentBag<LambdaAsyncOverload> LambdaAsyncOverloads { get; } = [];

    /// <summary>
    /// Interface mappings from problematic interface analysis (sync → async interface names).
    /// When set, the transformer will replace interface references in source files.
    /// </summary>
    public List<InterfaceMapping> InterfaceMappings { get; set; } = [];

    /// <summary>
    /// Namespace where the AsyncOutResult&lt;T&gt; helper class resides.
    /// Used to add the correct using directive when BoolTryPattern out-parameter transformations are applied.
    /// When null, the transformer will scan source files for an existing AsyncOutResult class.
    /// </summary>
    public string? AsyncOutResultNamespace { get; set; }

    /// <summary>
    /// Calls indexed by the caller
    /// </summary>
    private ConcurrentDictionary<string, List<IMethodCall>> _callsByCaller { get; } = new();

    private ConcurrentDictionary<string, List<IMethodCall>> _callsByCallee { get; } = new();

    private ConcurrentDictionary<string, List<IInterfaceImplementation>> _implsByImplementing { get; } = new();

    private ConcurrentDictionary<string, List<IInterfaceImplementation>> _implsByInterface { get; } = new();

    private ConcurrentDictionary<string, List<IMethodOverride>> _overridesByOverriding { get;  } = new();

    private ConcurrentDictionary<string, List<IMethodOverride>> _overridesByBase { get; } = new();

    private ConcurrentDictionary<string, List<IGenericInstantiation>> _instantiationsByInstantiated { get; } = new();

    private ConcurrentDictionary<string, List<IGenericInstantiation>> _instantiationsByGeneric { get;  } = new();

    public CallGraph(
        string id,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentBag<IMethodCall> methodCalls,
        ConcurrentBag<IInterfaceImplementation>? interfaceImplementations = null,
        ConcurrentBag<IMethodOverride>? methodOverrides = null,
        ConcurrentBag<IGenericInstantiation>? genericInstantiations = null,
        ConcurrentBag<LambdaAsyncOverload>? lambdaAsyncOverloads = null)
    {
        Id = id;
        Methods = methods;
        Calls = methodCalls;
        _callsByCaller = new ConcurrentDictionary<string, List<IMethodCall>>(methodCalls
            .GroupBy(v => v.CallerId)
            .Select(grouping => new KeyValuePair<string, List<IMethodCall>>(grouping.Key, grouping.ToList())));
        _callsByCallee = new ConcurrentDictionary<string, List<IMethodCall>>(methodCalls
            .GroupBy(v => v.CalleeId)
            .Select(grouping => new KeyValuePair<string, List<IMethodCall>>(grouping.Key, grouping.ToList())));

        if (interfaceImplementations != null)
        {
            InterfaceImplementations = interfaceImplementations;
            _implsByImplementing = new ConcurrentDictionary<string, List<IInterfaceImplementation>>(interfaceImplementations
                .GroupBy(i => i.ImplementingMethodId)
                .Select(g => new KeyValuePair<string, List<IInterfaceImplementation>>(g.Key, g.ToList())));
            _implsByInterface = new ConcurrentDictionary<string, List<IInterfaceImplementation>>(interfaceImplementations
                .GroupBy(i => i.InterfaceMethodId)
                .Select(g => new KeyValuePair<string, List<IInterfaceImplementation>>(g.Key, g.ToList())));
        }

        if (methodOverrides != null)
        {
            MethodOverrides = methodOverrides;
            _overridesByOverriding = new ConcurrentDictionary<string, List<IMethodOverride>>(methodOverrides
                .GroupBy(o => o.OverridingMethodId)
                .Select(g => new KeyValuePair<string, List<IMethodOverride>>(g.Key, g.ToList())));
            _overridesByBase = new ConcurrentDictionary<string, List<IMethodOverride>>(methodOverrides
                .GroupBy(o => o.BaseMethodId)
                .Select(g => new KeyValuePair<string, List<IMethodOverride>>(g.Key, g.ToList())));
        }

        if (genericInstantiations != null)
        {
            GenericInstantiations = genericInstantiations;
            _instantiationsByInstantiated = new ConcurrentDictionary<string, List<IGenericInstantiation>>(genericInstantiations
                .GroupBy(gi => gi.InstantiatedMethodId)
                .Select(g => new KeyValuePair<string, List<IGenericInstantiation>>(g.Key, g.ToList())));
            _instantiationsByGeneric = new ConcurrentDictionary<string, List<IGenericInstantiation>>(genericInstantiations
                .GroupBy(gi => gi.GenericMethodId)
                .Select(g => new KeyValuePair<string, List<IGenericInstantiation>>(g.Key, g.ToList())));
        }

        if (lambdaAsyncOverloads != null)
        {
            LambdaAsyncOverloads = lambdaAsyncOverloads;
        }
    }

    /// <summary>
    /// Get all methods that call the specified method
    /// </summary>
    public IEnumerable<IMethodNode> GetCallers(string methodId)
    {
        if (!_callsByCallee.TryGetValue(methodId, out var callsByCaller))
        {
            return [];
        }

        return callsByCaller
            .Select(c => Methods[c.CallerId]);
    }

    /// <summary>
    /// Get all methods called by the specified method
    /// </summary>
    public IEnumerable<IMethodNode> GetCallees(string methodId)
    {
        if (!_callsByCaller.TryGetValue(methodId, out var callees))
        {
            return [];
        }

        return callees
            .Select(c => Methods[c.CallerId]);
    }

    /// <summary>
    /// Get interface methods that the specified method implements
    /// </summary>
    public IEnumerable<IInterfaceImplementation> GetInterfaceMethodsFor(string methodId)
    {
        if (!_implsByImplementing.TryGetValue(methodId, out var impls))
        {
            return [];
        }

        return impls;
    }

    /// <summary>
    /// Get implementations of the specified interface method
    /// </summary>
    public IEnumerable<IInterfaceImplementation> GetImplementationsOf(string interfaceMethodId)
    {
        if (!_implsByInterface.TryGetValue(interfaceMethodId, out var impls))
        {
            return [];
        }

        return impls;
    }

    /// <summary>
    /// Get base methods that the specified method overrides
    /// </summary>
    public IEnumerable<IMethodOverride> GetBaseMethodsFor(string methodId)
    {
        if (!_overridesByOverriding.TryGetValue(methodId, out var overrides))
        {
            return [];
        }

        return overrides;
    }

    /// <summary>
    /// Get methods that override the specified base method
    /// </summary>
    public IEnumerable<IMethodOverride> GetOverridesOf(string baseMethodId)
    {
        if (!_overridesByBase.TryGetValue(baseMethodId, out var overrides))
        {
            return [];
        }

        return overrides;
    }

    /// <summary>
    /// Get the generic method(s) for an instantiated interface method
    /// </summary>
    public IEnumerable<IGenericInstantiation> GetGenericMethodsFor(string instantiatedMethodId)
    {
        if (!_instantiationsByInstantiated.TryGetValue(instantiatedMethodId, out var instantiations))
        {
            return [];
        }

        return instantiations;
    }

    /// <summary>
    /// Get all instantiations of a generic interface method
    /// </summary>
    public IEnumerable<IGenericInstantiation> GetInstantiationsOf(string genericMethodId)
    {
        if (!_instantiationsByGeneric.TryGetValue(genericMethodId, out var instantiations))
        {
            return [];
        }

        return instantiations;
    }
}