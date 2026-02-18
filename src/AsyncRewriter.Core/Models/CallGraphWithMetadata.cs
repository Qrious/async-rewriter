using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public class CallGraphWithMetadata<TMethodMetadata, TCallMetadata> : ICallGraphWithMetadata<TMethodMetadata, TCallMetadata>
    where TMethodMetadata : IGraphMetadata<TMethodMetadata>
    where TCallMetadata : IGraphMetadata<TCallMetadata>
{
    private ICallGraph _callGraph;

    public CallGraphWithMetadata(
        Guid id,
        ICallGraph callGraph,
        IReadOnlyDictionary<string, TMethodMetadata> methodMetadata,
        IReadOnlyDictionary<string, TCallMetadata> callMetadata
    )
    {
        _callGraph = callGraph;
        Id = id.ToString();
        MethodMetadata = methodMetadata;
        CallMetadata = callMetadata;
    }

    public IReadOnlyDictionary<string, TCallMetadata> CallMetadata { get; }

    public TMethodMetadata GetMethodMetadata(string methodId) => MethodMetadata[methodId];

    public bool TryGetMethodMetadata(string methodId, out TMethodMetadata? metadata) => MethodMetadata.TryGetValue(methodId, out metadata);

    public TCallMetadata GetCallMetadata(string callId) => CallMetadata[callId];

    public bool TryGetCallMetadata(string callId, out TCallMetadata? metadata) => CallMetadata.TryGetValue(callId, out metadata);

    public IReadOnlyDictionary<string, TMethodMetadata> MethodMetadata { get; }

    public string Id { get; }
    public ConcurrentDictionary<string, IMethodNode> Methods => _callGraph.Methods;

    public ConcurrentBag<IMethodCall> Calls => _callGraph.Calls;

    public ConcurrentBag<IInterfaceImplementation> InterfaceImplementations => _callGraph.InterfaceImplementations;

    public ConcurrentBag<IMethodOverride> MethodOverrides => _callGraph.MethodOverrides;

    public ConcurrentBag<IGenericInstantiation> GenericInstantiations => _callGraph.GenericInstantiations;

    public IEnumerable<IMethodNode> GetCallers(string methodId)
    {
        return _callGraph.GetCallers(methodId);
    }

    public IEnumerable<IMethodNode> GetCallees(string methodId)
    {
        return _callGraph.GetCallees(methodId);
    }

    public IEnumerable<IInterfaceImplementation> GetInterfaceMethodsFor(string methodId)
    {
        return _callGraph.GetInterfaceMethodsFor(methodId);
    }

    public IEnumerable<IInterfaceImplementation> GetImplementationsOf(string interfaceMethodId)
    {
        return _callGraph.GetImplementationsOf(interfaceMethodId);
    }

    public IEnumerable<IMethodOverride> GetBaseMethodsFor(string methodId)
    {
        return _callGraph.GetBaseMethodsFor(methodId);
    }

    public IEnumerable<IMethodOverride> GetOverridesOf(string baseMethodId)
    {
        return _callGraph.GetOverridesOf(baseMethodId);
    }

    public IEnumerable<IGenericInstantiation> GetGenericMethodsFor(string instantiatedMethodId)
    {
        return _callGraph.GetGenericMethodsFor(instantiatedMethodId);
    }

    public IEnumerable<IGenericInstantiation> GetInstantiationsOf(string genericMethodId)
    {
        return _callGraph.GetInstantiationsOf(genericMethodId);
    }
}