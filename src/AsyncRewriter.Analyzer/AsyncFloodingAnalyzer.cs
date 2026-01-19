using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Analyzes async flooding: determines which methods need to be async
/// based on the call graph and root async methods
/// </summary>
public class AsyncFloodingAnalyzer : IAsyncFloodingAnalyzer
{
    public Task<CallGraph> AnalyzeFloodingAsync(
        CallGraph callGraph,
        HashSet<string> rootMethodIds,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeFloodingAsync(callGraph, rootMethodIds, null, cancellationToken);
    }

    public Task<CallGraph> AnalyzeFloodingAsync(
        CallGraph callGraph,
        HashSet<string> rootMethodIds,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        // Store root methods
        callGraph.RootAsyncMethods = new HashSet<string>(rootMethodIds);

        // Methods that need to become async
        var methodsToFlood = new HashSet<string>();

        // Queue for BFS traversal
        var queue = new Queue<string>(rootMethodIds);
        var visited = new HashSet<string>();
        var totalMethods = callGraph.Methods.Count;
        var processedCount = 0;
        var interfaceImplementations = BuildInterfaceImplementationLookup(callGraph);

        progressCallback?.Invoke("Starting flood from root methods", 0, totalMethods);

        // BFS to find all methods that need to become async
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentMethodId = queue.Dequeue();

            if (!visited.Add(currentMethodId))
                continue;

            processedCount++;

            if (!callGraph.Methods.TryGetValue(currentMethodId, out var currentMethod))
                continue;


            // Report progress with current method being analyzed
            var methodDisplayName = $"{currentMethod.ContainingType}.{currentMethod.Name}";
            progressCallback?.Invoke(methodDisplayName, processedCount, totalMethods);

            // If this method is not already async, mark it for transformation
            // Skip sync wrapper methods themselves - they'll be unwrapped, not transformed
            if (!currentMethod.IsAsync && !callGraph.SyncWrapperMethods.Contains(currentMethodId))
            {
                methodsToFlood.Add(currentMethodId);
                currentMethod.RequiresAsyncTransformation = true;
                currentMethod.AsyncReturnType = DetermineAsyncReturnType(currentMethod.ReturnType);

                // Also mark any interface methods this method implements
                foreach (var interfaceMethodId in currentMethod.ImplementsInterfaceMethods)
                {
                    if (!callGraph.Methods.TryGetValue(interfaceMethodId, out var interfaceMethod))
                    {
                        continue;
                    }

                    interfaceImplementations.TryGetValue(interfaceMethodId, out var implementations);
                    implementations ??= new List<MethodNode>();

                    var asyncImplementationCount = implementations.Count(method => method.IsAsync || method.RequiresAsyncTransformation);
                    var shouldPropagateInterface = interfaceMethod.IsAsync || interfaceMethod.RequiresAsyncTransformation || asyncImplementationCount > 1;

                    if (!shouldPropagateInterface)
                    {
                        continue;
                    }

                    if (asyncImplementationCount > 1 || interfaceMethod.IsAsync || interfaceMethod.RequiresAsyncTransformation)
                    {
                        if (!interfaceMethod.RequiresAsyncTransformation && !interfaceMethod.IsAsync)
                        {
                            methodsToFlood.Add(interfaceMethodId);
                            interfaceMethod.RequiresAsyncTransformation = true;
                            interfaceMethod.AsyncReturnType = DetermineAsyncReturnType(interfaceMethod.ReturnType);
                            SetPropagationSource(interfaceMethod, currentMethodId);
                        }

                        // Find and mark ALL other implementations of this interface method
                        foreach (var method in implementations)
                        {
                            if (method.Id == currentMethodId)
                            {
                                continue;
                            }

                            if (!method.RequiresAsyncTransformation && !method.IsAsync)
                            {
                                SetPropagationSource(method, interfaceMethodId);
                                queue.Enqueue(method.Id);
                            }
                        }
                    }
                }
            }

            // Find all callers (including through interface calls) and add them to the queue
            var callers = callGraph.GetCallersIncludingInterfaceCalls(currentMethodId);
            foreach (var caller in callers)
            {
                SetPropagationSource(caller, currentMethodId);
                queue.Enqueue(caller.Id);
            }
        }

        callGraph.FloodedMethods = methodsToFlood;
        EnsureFloodedMethodReasons(callGraph);

        progressCallback?.Invoke("Marking calls that require await", visited.Count, totalMethods);

        // Mark all calls that need await
        foreach (var call in callGraph.Calls)
        {
            if (callGraph.Methods.TryGetValue(call.CalleeId, out var callee))
            {
                // If the callee is async or needs to be async, this call needs await
                call.RequiresAwait = callee.IsAsync || callee.RequiresAsyncTransformation;
            }
        }

        progressCallback?.Invoke("Flooding complete", visited.Count, totalMethods);

        return Task.FromResult(callGraph);
    }

    public Task<List<AsyncTransformationInfo>> GetTransformationInfoAsync(
        CallGraph callGraph,
        CancellationToken cancellationToken = default)
    {
        var transformations = new List<AsyncTransformationInfo>();

        foreach (var methodId in callGraph.FloodedMethods)
        {
            if (!callGraph.Methods.TryGetValue(methodId, out var method))
                continue;

            var transformation = new AsyncTransformationInfo
            {
                MethodId = methodId,
                OriginalReturnType = method.ReturnType,
                NewReturnType = method.AsyncReturnType ?? DetermineAsyncReturnType(method.ReturnType),
                NeedsAsyncKeyword = true,
                ImplementsInterfaceMethods = method.ImplementsInterfaceMethods
                    .Where(interfaceMethodId => callGraph.Methods.TryGetValue(interfaceMethodId, out var interfaceMethod)
                        && (interfaceMethod.RequiresAsyncTransformation || interfaceMethod.IsAsync))
                    .ToList()
            };

            // Find all call sites in this method that need await
            var callSites = callGraph.Calls
                .Where(c => c.CallerId == methodId && c.RequiresAwait)
                .Select(c => new CallSiteTransformation
                {
                    FilePath = c.FilePath,
                    LineNumber = c.LineNumber,
                    CalledMethodSignature = c.CalleeSignature,
                    OriginalCallExpression = "", // Will be filled by the transformer
                    NewCallExpression = "" // Will be filled by the transformer
                })
                .ToList();

            transformation.CallSitesToTransform = callSites;
            transformations.Add(transformation);
        }

        return Task.FromResult(transformations);
    }

    private string DetermineAsyncReturnType(string originalReturnType)
    {
        // Remove any leading/trailing whitespace
        originalReturnType = originalReturnType.Trim();

        // If already returns Task or Task<T>, keep it
        if (originalReturnType.StartsWith("Task<") || originalReturnType == "Task")
        {
            return originalReturnType;
        }

        // If void, return Task
        if (originalReturnType == "void")
        {
            return "Task";
        }

        // Otherwise wrap in Task<T>
        return $"Task<{originalReturnType}>";
    }

    private static void EnsureFloodedMethodReasons(CallGraph callGraph)
    {
        foreach (var methodId in callGraph.FloodedMethods)
        {
            if (!callGraph.Methods.TryGetValue(methodId, out var method))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(method.AsyncPropagationSourceMethodId))
            {
                continue;
            }

            if (callGraph.RootAsyncMethods.Contains(methodId))
            {
                method.AsyncPropagationSourceMethodId = methodId;
                continue;
            }

            if (method.ImplementsInterfaceMethods.Count > 0)
            {
                var interfaceId = method.ImplementsInterfaceMethods[0];
                if (callGraph.Methods.TryGetValue(interfaceId, out var interfaceMethod) &&
                    (interfaceMethod.IsAsync || interfaceMethod.RequiresAsyncTransformation))
                {
                    method.AsyncPropagationSourceMethodId = interfaceId;
                    continue;
                }
            }

            var fallbackCaller = callGraph.GetCallersIncludingInterfaceCalls(methodId).FirstOrDefault();
            if (fallbackCaller != null)
            {
                method.AsyncPropagationSourceMethodId = fallbackCaller.Id;
            }
        }
    }

    private static void SetPropagationSource(MethodNode method, string sourceMethodId)
    {
        if (string.IsNullOrWhiteSpace(method.AsyncPropagationSourceMethodId))
        {
            method.AsyncPropagationSourceMethodId = sourceMethodId;
        }
    }

    private static Dictionary<string, List<MethodNode>> BuildInterfaceImplementationLookup(CallGraph callGraph)
    {
        var lookup = new Dictionary<string, List<MethodNode>>();

        foreach (var method in callGraph.Methods.Values)
        {
            foreach (var interfaceMethodId in method.ImplementsInterfaceMethods)
            {
                if (!lookup.TryGetValue(interfaceMethodId, out var implementations))
                {
                    implementations = new List<MethodNode>();
                    lookup[interfaceMethodId] = implementations;
                }

                implementations.Add(method);
            }
        }

        return lookup;
    }
}
