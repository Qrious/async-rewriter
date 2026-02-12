using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents the complete call graph for a codebase or project
/// </summary>
public class CallGraph
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ProjectName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public ConcurrentDictionary<string, MethodNode> Methods { get; init; } = new();
    
    public ConcurrentBag<MethodCall> Calls { get; } = new();

    public ConcurrentBag<InterfaceImplementation> InterfaceImplementations { get; } = new();

    public ConcurrentBag<MethodOverride> MethodOverrides { get; } = new();

    /// <summary>
    /// Interface mappings from problematic interface analysis (sync → async interface names).
    /// When set, the transformer will replace interface references in source files.
    /// </summary>
    public List<InterfaceMapping> InterfaceMappings { get; set; } = new();

    /// <summary>
    /// Calls indexed by the caller
    /// </summary>
    private ConcurrentDictionary<string, List<MethodCall>> _callsByCaller { get; init; } = new();

    private ConcurrentDictionary<string, List<MethodCall>> _callsByCallee { get; init; } = new();

    private ConcurrentDictionary<string, List<InterfaceImplementation>> _implsByImplementing { get; init; } = new();

    private ConcurrentDictionary<string, List<InterfaceImplementation>> _implsByInterface { get; init; } = new();

    private ConcurrentDictionary<string, List<MethodOverride>> _overridesByOverriding { get; init; } = new();

    private ConcurrentDictionary<string, List<MethodOverride>> _overridesByBase { get; init; } = new();

    public CallGraph(ConcurrentBag<MethodCall> methodCalls, ConcurrentBag<InterfaceImplementation>? interfaceImplementations = null, ConcurrentBag<MethodOverride>? methodOverrides = null)
    {
        Calls = methodCalls;
        _callsByCaller = new ConcurrentDictionary<string, List<MethodCall>>(methodCalls
            .GroupBy(v => v.CallerId)
            .Select(grouping => new KeyValuePair<string, List<MethodCall>>(grouping.Key, grouping.ToList())));
        _callsByCallee = new ConcurrentDictionary<string, List<MethodCall>>(methodCalls
            .GroupBy(v => v.CalleeId)
            .Select(grouping => new KeyValuePair<string, List<MethodCall>>(grouping.Key, grouping.ToList())));

        if (interfaceImplementations != null)
        {
            InterfaceImplementations = interfaceImplementations;
            _implsByImplementing = new ConcurrentDictionary<string, List<InterfaceImplementation>>(interfaceImplementations
                .GroupBy(i => i.ImplementingMethodId)
                .Select(g => new KeyValuePair<string, List<InterfaceImplementation>>(g.Key, g.ToList())));
            _implsByInterface = new ConcurrentDictionary<string, List<InterfaceImplementation>>(interfaceImplementations
                .GroupBy(i => i.InterfaceMethodId)
                .Select(g => new KeyValuePair<string, List<InterfaceImplementation>>(g.Key, g.ToList())));
        }

        if (methodOverrides != null)
        {
            MethodOverrides = methodOverrides;
            _overridesByOverriding = new ConcurrentDictionary<string, List<MethodOverride>>(methodOverrides
                .GroupBy(o => o.OverridingMethodId)
                .Select(g => new KeyValuePair<string, List<MethodOverride>>(g.Key, g.ToList())));
            _overridesByBase = new ConcurrentDictionary<string, List<MethodOverride>>(methodOverrides
                .GroupBy(o => o.BaseMethodId)
                .Select(g => new KeyValuePair<string, List<MethodOverride>>(g.Key, g.ToList())));
        }
    }
    
    /// <summary>
    /// Get all methods that call the specified method
    /// </summary>
    public IEnumerable<MethodNode> GetCallers(string methodId)
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
    public IEnumerable<MethodNode> GetCallees(string methodId)
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
    public IEnumerable<InterfaceImplementation> GetInterfaceMethodsFor(string methodId)
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
    public IEnumerable<InterfaceImplementation> GetImplementationsOf(string interfaceMethodId)
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
    public IEnumerable<MethodOverride> GetBaseMethodsFor(string methodId)
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
    public IEnumerable<MethodOverride> GetOverridesOf(string baseMethodId)
    {
        if (!_overridesByBase.TryGetValue(baseMethodId, out var overrides))
        {
            return [];
        }
        return overrides;
    }
}
