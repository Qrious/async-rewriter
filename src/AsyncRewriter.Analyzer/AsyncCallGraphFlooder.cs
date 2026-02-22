using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Analyzer;

public class AsyncCallGraphFlooder : IAsyncCallGraphFlooder
{
    private readonly ILogger<AsyncCallGraphFlooder> _logger;

    public AsyncCallGraphFlooder(ILogger<AsyncCallGraphFlooder> logger)
    {
        _logger = logger;
    }

    public Task<ICallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>> Flood(
        ICallGraph callGraph,
        HashSet<string> rootMethodIds,
        string? newGraphId = null,
        CancellationToken cancellationToken = default)
    {
        var floodedMethodInfos = new Dictionary<string, FloodedMethodInfo>();
        var floodedIds = new HashSet<string>();
        var queue = new Queue<string>();
        var processed = 0;

        // Seed roots
        foreach (var rootId in rootMethodIds)
        {
            floodedIds.Add(rootId);
            queue.Enqueue(rootId);
            floodedMethodInfos[rootId] = new FloodedMethodInfo(rootId, null, 0, FloodReason.Root);
        }

        // BFS upstream through callers
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methodId = queue.Dequeue();
            processed++;
            var currentDepth = floodedMethodInfos[methodId].Depth;

            if (callGraph.Methods.TryGetValue(methodId, out var method))
            {
                _logger.LogTrace("Processing method {MethodName} ({Processed}/{Total})", $"{method.ContainingType}.{method.Name}", processed, processed + queue.Count);
            }

            void TryEnqueue(string targetId, FloodReason reason)
            {
                if (floodedIds.Add(targetId))
                {
                    queue.Enqueue(targetId);
                    floodedMethodInfos[targetId] = new FloodedMethodInfo(targetId, methodId, currentDepth + 1, reason);
                }
            }

            // Flood to callers
            foreach (var caller in callGraph.GetCallers(methodId))
            {
                TryEnqueue(caller.Id, FloodReason.Caller);
            }

            // // Flood through interface implementations (both directions)
            // foreach (var impl in callGraph.GetInterfaceMethodsFor(methodId))
            // {
            //     TryEnqueue(impl.InterfaceMethodId, FloodReason.InterfaceMethod);
            // }
            //
            // foreach (var impl in callGraph.GetImplementationsOf(methodId))
            // {
            //     TryEnqueue(impl.ImplementingMethodId, FloodReason.InterfaceImpl);
            // }
            //
            // // Flood through generic instantiations:
            // // Only traverse when the return type on the generic node is NOT a type parameter.
            // // Also skip if the generic method is in the blocked set.
            // foreach (var gi in callGraph.GetGenericMethodsFor(methodId))
            // {
            //     if (!HasGenericReturnType(callGraph, gi.GenericMethodId))
            //     {
            //         TryEnqueue(gi.GenericMethodId, FloodReason.GenericInstantiation);
            //     }
            // }
            // foreach (var gi in callGraph.GetInstantiationsOf(methodId))
            // {
            //     if (!HasGenericReturnType(callGraph, methodId))
            //     {
            //         TryEnqueue(gi.InstantiatedMethodId, FloodReason.GenericInstantiation);
            //     }
            // }
            //
            // // Flood through overrides (both directions)
            // foreach (var ovr in callGraph.GetBaseMethodsFor(methodId))
            // {
            //     TryEnqueue(ovr.BaseMethodId, FloodReason.BaseMethod);
            // }
            //
            // foreach (var ovr in callGraph.GetOverridesOf(methodId))
            // {
            //     TryEnqueue(ovr.OverridingMethodId, FloodReason.Override);
            // }
        }

        // Build new call graph with transformed return types
        newGraphId ??= $"{callGraph.Id}_flooded";
        var newMethods = new ConcurrentDictionary<string, IMethodNode>();
        var methodMetadata = new Dictionary<string, FloodingMethodMetadata>();

        foreach (var (id, methodNode) in callGraph.Methods)
        {
            var m = (MethodNode)methodNode;
            var originalReturnType = m.ReturnType;
            var newReturnType = floodedIds.Contains(id)
                ? TransformReturnType(originalReturnType)
                : originalReturnType;

            newMethods[id] = m with { CallGraphId = newGraphId, ReturnType = newReturnType };

            if (floodedMethodInfos.TryGetValue(id, out var info))
            {
                methodMetadata[id] = new FloodingMethodMetadata
                {
                    FloodedById = info.FloodedById,
                    Depth = info.Depth,
                    Reason = info.Reason,
                    OriginalReturnType = originalReturnType,
                };
            }
        }

        var newCalls = new ConcurrentBag<IMethodCall>(
            callGraph.Calls.Select(c => (MethodCall)c with { CallGraphId = newGraphId }));
        
        var newImpls = new ConcurrentBag<IInterfaceImplementation>(
            callGraph.InterfaceImplementations.Select(i => new InterfaceImplementation
            {
                CallGraphId = newGraphId,
                ImplementingMethodId = i.ImplementingMethodId,
                InterfaceMethodId = i.InterfaceMethodId
            }));

        var newOverrides = new ConcurrentBag<IMethodOverride>(
            callGraph.MethodOverrides.Select(o => new MethodOverride
            {
                CallGraphId = newGraphId,
                OverridingMethodId = o.OverridingMethodId,
                BaseMethodId = o.BaseMethodId
            }));

        var newGenericInstantiations = new ConcurrentBag<IGenericInstantiation>(
            callGraph.GenericInstantiations.Select(gi => new GenericInstantiation
            {
                CallGraphId = newGraphId,
                InstantiatedMethodId = gi.InstantiatedMethodId,
                GenericMethodId = gi.GenericMethodId
            }));

        var newGraph = new CallGraph(newGraphId, newMethods, newCalls, newImpls, newOverrides, newGenericInstantiations);
        var result = new CallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            newGraphId,
            newGraph,
            methodMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());

        return Task.FromResult<ICallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>>(result);
    }

    public Task<List<AsyncTransformationInfo>> GetTransformationInfoAsync(ICallGraph callGraph, CancellationToken cancellationToken = default)
    {
        var results = new List<AsyncTransformationInfo>();

        foreach (var (id, method) in callGraph.Methods)
        {
            // A method was flooded if its return type is Task-based
            var returnType = method.ReturnType;
            if (!IsTaskType(returnType))
            {
                continue;
            }

            var interfaceMethods = callGraph.GetInterfaceMethodsFor(id)
                .Select(i => i.InterfaceMethodId)
                .ToList();

            results.Add(new AsyncTransformationInfo
            {
                MethodId = id,
                OriginalReturnType = returnType, // already transformed in the new graph
                NewReturnType = returnType,
                NeedsAsyncKeyword = true,
                ImplementsInterfaceMethods = interfaceMethods
            });
        }

        return Task.FromResult(results);
    }

    public static string TransformReturnType(string returnType)
    {
        if (IsTaskType(returnType))
        {
            return returnType;
        }

        if (returnType == "void")
        {
            return "Task";
        }

        return $"Task<{returnType}>";
    }

    /// <summary>
    /// Checks whether the interface method's return type is a generic type parameter
    /// of the containing interface. E.g. IMapper&lt;TSource, TDestination&gt;.Map returns
    /// TDestination — the return type can be adjusted by changing the type argument.
    /// </summary>
    private static bool HasGenericReturnType(ICallGraph callGraph, string interfaceMethodId)
    {
        if (!callGraph.Methods.TryGetValue(interfaceMethodId, out var method))
        {
            return false;
        }

        var typeParams = ParseGenericTypeParameters(method.ContainingType);
        if (typeParams.Count == 0)
        {
            return false;
        }

        var returnType = method.ReturnType.TrimEnd('?');
        return typeParams.Contains(returnType);
    }

    /// <summary>
    /// Extracts generic type parameter names from a containing type string.
    /// E.g. "IMapper&lt;TSource, TDestination&gt;" → ["TSource", "TDestination"]
    /// </summary>
    public static List<string> ParseGenericTypeParameters(string containingType)
    {
        var startIndex = containingType.IndexOf('<');
        if (startIndex < 0)
        {
            return [];
        }

        var endIndex = containingType.LastIndexOf('>');
        if (endIndex < 0)
        {
            return [];
        }

        var paramString = containingType.Substring(startIndex + 1, endIndex - startIndex - 1);

        // Handle nested generics by only splitting at top-level commas
        var result = new List<string>();
        var depth = 0;
        var current = 0;
        for (var i = 0; i < paramString.Length; i++)
        {
            switch (paramString[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    result.Add(paramString.Substring(current, i - current).Trim());
                    current = i + 1;
                    break;
            }
        }
        result.Add(paramString.Substring(current).Trim());
        return result;
    }

    private static bool IsTaskType(string returnType)
    {
        return returnType == "Task"
            || returnType.StartsWith("Task<")
            || returnType == "System.Threading.Tasks.Task"
            || returnType.StartsWith("System.Threading.Tasks.Task<")
            || returnType == "ValueTask"
            || returnType.StartsWith("ValueTask<")
            || returnType.StartsWith("System.Threading.Tasks.ValueTask");
    }
}
