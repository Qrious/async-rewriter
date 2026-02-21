using System;
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

    public Task<CallGraph> Flood(
        ICallGraph callGraph,
        HashSet<string> rootMethodIds,
        string? newGraphId = null,
        CancellationToken cancellationToken = default)
    {
        var floodedIds = new HashSet<string>(rootMethodIds);
        var queue = new Queue<string>(rootMethodIds);
        var processed = 0;

        // BFS upstream through callers, interface implementations, and overrides
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methodId = queue.Dequeue();
            processed++;

            if (callGraph.Methods.TryGetValue(methodId, out var method))
            {
                _logger.LogTrace("Processing method {MethodName} ({Processed}/{Total})", $"{method.ContainingType}.{method.Name}", processed, processed + queue.Count);
            }

            // Flood to callers
            foreach (var caller in callGraph.GetCallers(methodId))
            {
                if (floodedIds.Add(caller.Id))
                {
                    queue.Enqueue(caller.Id);
                }
            }

            // Flood through interface implementations (both directions)
            foreach (var impl in callGraph.GetInterfaceMethodsFor(methodId))
            {
                if (floodedIds.Add(impl.InterfaceMethodId))
                {
                    queue.Enqueue(impl.InterfaceMethodId);
                }
            }
            foreach (var impl in callGraph.GetImplementationsOf(methodId))
            {
                if (floodedIds.Add(impl.ImplementingMethodId))
                {
                    queue.Enqueue(impl.ImplementingMethodId);
                }
            }

            // Flood through generic instantiations:
            // Only traverse when the return type on the generic node is NOT a type parameter.
            // Also skip if the generic method is in the blocked set.
            foreach (var gi in callGraph.GetGenericMethodsFor(methodId))
            {
                if (!HasGenericReturnType(callGraph, gi.GenericMethodId) && floodedIds.Add(gi.GenericMethodId))
                {
                    queue.Enqueue(gi.GenericMethodId);
                }
            }
            foreach (var gi in callGraph.GetInstantiationsOf(methodId))
            {
                if (!HasGenericReturnType(callGraph, methodId) && floodedIds.Add(gi.InstantiatedMethodId))
                {
                    queue.Enqueue(gi.InstantiatedMethodId);
                }
            }

            // Flood through overrides (both directions)
            foreach (var ovr in callGraph.GetBaseMethodsFor(methodId))
            {
                if (floodedIds.Add(ovr.BaseMethodId))
                {
                    queue.Enqueue(ovr.BaseMethodId);
                }
            }
            foreach (var ovr in callGraph.GetOverridesOf(methodId))
            {
                if (floodedIds.Add(ovr.OverridingMethodId))
                {
                    queue.Enqueue(ovr.OverridingMethodId);
                }
            }
        }

        // Build new call graph with transformed return types
        newGraphId ??= $"{callGraph.Id}_flooded";
        var newMethods = new ConcurrentDictionary<string, IMethodNode>();

        foreach (var (id, methodNode) in callGraph.Methods)
        {
            var method = (MethodNode)methodNode;
            var newReturnType = floodedIds.Contains(id)
                ? TransformReturnType(method.ReturnType)
                : method.ReturnType;

            newMethods[id] = method with
            {
                CallGraphId = newGraphId,
                ReturnType = newReturnType
            };
        }

        var allCalls = callGraph.Calls;
        var newCalls = new ConcurrentBag<IMethodCall>(
            allCalls.Select(c => (MethodCall)c with { CallGraphId = newGraphId }));

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

        return Task.FromResult(newGraph);
    }

    public Task<(CallGraph, FloodingResult)> AnalyzeFloodingWithDebugAsync(
        CallGraph callGraph,
        HashSet<string> rootMethodIds,
        Action<string, int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
        => AnalyzeFloodingWithDebugAsync(callGraph, rootMethodIds, blockedGenericMethodIds: null, progressCallback, cancellationToken);

    public Task<(CallGraph, FloodingResult)> AnalyzeFloodingWithDebugAsync(
        CallGraph callGraph,
        HashSet<string> rootMethodIds,
        HashSet<string>? blockedGenericMethodIds,
        Action<string, int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var floodingResult = new FloodingResult();
        var floodedIds = new HashSet<string>();
        var queue = new Queue<string>();
        var processed = 0;

        // Seed roots
        foreach (var rootId in rootMethodIds)
        {
            floodedIds.Add(rootId);
            queue.Enqueue(rootId);
            floodingResult.FloodedMethods[rootId] = new FloodedMethodInfo(rootId, null, 0, FloodReason.Root);
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methodId = queue.Dequeue();
            processed++;
            var currentDepth = floodingResult.FloodedMethods[methodId].Depth;

            if (callGraph.Methods.TryGetValue(methodId, out var method))
            {
                progressCallback?.Invoke(
                    $"{method.ContainingType}.{method.Name}",
                    processed,
                    processed + queue.Count);
            }

            void TryEnqueue(string targetId, FloodReason reason)
            {
                if (floodedIds.Add(targetId))
                {
                    queue.Enqueue(targetId);
                    floodingResult.FloodedMethods[targetId] = new FloodedMethodInfo(targetId, methodId, currentDepth + 1, reason);
                }
            }

            foreach (var caller in callGraph.GetCallers(methodId))
            {
                TryEnqueue(caller.Id, FloodReason.Caller);
            }

            foreach (var impl in callGraph.GetInterfaceMethodsFor(methodId))
            {
                TryEnqueue(impl.InterfaceMethodId, FloodReason.InterfaceMethod);
            }

            foreach (var impl in callGraph.GetImplementationsOf(methodId))
            {
                TryEnqueue(impl.ImplementingMethodId, FloodReason.InterfaceImpl);
            }

            foreach (var gi in callGraph.GetGenericMethodsFor(methodId))
            {
                if (!HasGenericReturnType(callGraph, gi.GenericMethodId)
                    && blockedGenericMethodIds?.Contains(gi.GenericMethodId) != true)
                {
                    TryEnqueue(gi.GenericMethodId, FloodReason.GenericInstantiation);
                }
            }
            foreach (var gi in callGraph.GetInstantiationsOf(methodId))
            {
                if (!HasGenericReturnType(callGraph, methodId)
                    && blockedGenericMethodIds?.Contains(methodId) != true)
                {
                    TryEnqueue(gi.InstantiatedMethodId, FloodReason.GenericInstantiation);
                }
            }

            foreach (var ovr in callGraph.GetBaseMethodsFor(methodId))
            {
                TryEnqueue(ovr.BaseMethodId, FloodReason.BaseMethod);
            }

            foreach (var ovr in callGraph.GetOverridesOf(methodId))
            {
                TryEnqueue(ovr.OverridingMethodId, FloodReason.Override);
            }
        }

        // Build new call graph with transformed return types (same as AnalyzeFloodingAsync)
        var newGraphId = Guid.NewGuid().ToString();
        var newMethods = new ConcurrentDictionary<string, IMethodNode>();

        foreach (var (id, methodNode) in callGraph.Methods)
        {
            var m = (MethodNode)methodNode;
            var newReturnType = floodedIds.Contains(id)
                ? TransformReturnType(m.ReturnType)
                : m.ReturnType;

            newMethods[id] = m with
            {
                CallGraphId = newGraphId,
                ReturnType = newReturnType
            };
        }

        var allCalls = callGraph.Calls;
        var newCalls = new ConcurrentBag<IMethodCall>(
            allCalls.Select(c => (MethodCall)c with { CallGraphId = newGraphId }));

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

        return Task.FromResult((newGraph, floodingResult));
    }

    public Task<List<AsyncTransformationInfo>> GetTransformationInfoAsync(CallGraph callGraph, CancellationToken cancellationToken = default)
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
