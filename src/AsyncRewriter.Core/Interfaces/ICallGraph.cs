using System.Collections.Concurrent;
using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface ICallGraph
{
    public string Id { get; }

    public ConcurrentDictionary<string, IMethodNode> Methods { get; }
    public ConcurrentBag<IMethodCall> Calls { get; }
    public ConcurrentBag<IInterfaceImplementation> InterfaceImplementations { get; }
    public ConcurrentBag<IMethodOverride> MethodOverrides { get; }
    public ConcurrentBag<IGenericInstantiation> GenericInstantiations { get; }

    /// <summary>
    /// Get all methods that call the specified method
    /// </summary>
    public IEnumerable<IMethodNode> GetCallers(string methodId);

    /// <summary>
    /// Get all methods called by the specified method
    /// </summary>
    public IEnumerable<IMethodNode> GetCallees(string methodId);

    /// <summary>
    /// Get interface methods that the specified method implements
    /// </summary>
    public IEnumerable<IInterfaceImplementation> GetInterfaceMethodsFor(string methodId);

    /// <summary>
    /// Get implementations of the specified interface method
    /// </summary>
    public IEnumerable<IInterfaceImplementation> GetImplementationsOf(string interfaceMethodId);

    /// <summary>
    /// Get base methods that the specified method overrides
    /// </summary>
    public IEnumerable<IMethodOverride> GetBaseMethodsFor(string methodId);

    /// <summary>
    /// Get methods that override the specified base method
    /// </summary>
    public IEnumerable<IMethodOverride> GetOverridesOf(string baseMethodId);

    /// <summary>
    /// Get the generic method(s) for an instantiated interface method
    /// </summary>
    public IEnumerable<IGenericInstantiation> GetGenericMethodsFor(string instantiatedMethodId);

    /// <summary>
    /// Get all instantiations of a generic interface method
    /// </summary>
    public IEnumerable<IGenericInstantiation> GetInstantiationsOf(string genericMethodId);
}