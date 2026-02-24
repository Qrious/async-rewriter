using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer.ServiceInterface;

/// <summary>
/// Identifies methods that belong to an interface whose name ends with <c>Service</c>,
/// as well as concrete implementations of those interface methods.
/// These methods are seeded as flooding roots so the async transformation covers
/// every method on every <c>*Service</c> interface and its implementations.
/// </summary>
public class AsyncInterfaceMethodExtractor : IAsyncInterfaceMethodExtractor
{
    private const string ServiceSuffix = "Service";
    private const string RepositorySuffix = "Repository";

    public ICallGraphWithMetadata<ServiceInterfaceMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph)
    {
        var metadata = new Dictionary<string, ServiceInterfaceMethodMetadata>();

        // Step 1: find all methods declared directly on a *Service interface.
        foreach (var method in callGraph.Methods.Values)
        {
            if ((method.ContainingType.EndsWith(ServiceSuffix, System.StringComparison.Ordinal) ||
                method.ContainingType.EndsWith(RepositorySuffix, System.StringComparison.Ordinal)) &&
                !IsSystemInterfaceImplementation(method, callGraph))
            {
                metadata[method.Id] = new ServiceInterfaceMethodMetadata
                {
                    IsServiceInterfaceMethod = true,
                    InterfaceName = method.ContainingType
                };
            }
        }

        // Step 2: find all concrete implementations of those interface methods.
        foreach (var impl in callGraph.InterfaceImplementations)
        {
            // Only process if the interface method exists in the graph and is on a *Service interface.
            if (!callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var interfaceMethod))
            {
                continue;
            }

            if (!interfaceMethod.ContainingType.EndsWith(ServiceSuffix, System.StringComparison.Ordinal) &&
                !interfaceMethod.ContainingType.EndsWith(RepositorySuffix, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (IsSystemInterfaceImplementation(interfaceMethod, callGraph))
            {
                continue;
            }

            // Add the implementing method if not already marked.
            if (!metadata.ContainsKey(impl.ImplementingMethodId))
            {
                metadata[impl.ImplementingMethodId] = new ServiceInterfaceMethodMetadata
                {
                    IsServiceInterfaceMethod = true,
                    InterfaceName = interfaceMethod.ContainingType
                };
            }
        }

        return new CallGraphWithMetadata<ServiceInterfaceMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraph.Id,
            callGraph,
            metadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());
    }

    private bool IsSystemInterfaceImplementation(IMethodNode method, ICallGraph callGraph)
    {
        var interfaceMethods = callGraph.GetInterfaceMethodsFor(method.Id);
        if (interfaceMethods.Count() == 0) {
            return false;
        }

        return interfaceMethods.Any(m => callGraph.Methods.TryGetValue(m.InterfaceMethodId, out var interfaceMethod) && interfaceMethod.ContainingNamespace.StartsWith("System"));
    }
}