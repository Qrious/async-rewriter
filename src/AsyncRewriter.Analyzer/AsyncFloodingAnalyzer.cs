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
        // Store root methods
        callGraph.RootAsyncMethods = new HashSet<string>(rootMethodIds);

        // Methods that need to become async
        var methodsToFlood = new HashSet<string>();

        // Queue for BFS traversal
        var queue = new Queue<string>(rootMethodIds);
        var visited = new HashSet<string>();

        // BFS to find all methods that need to become async
        while (queue.Count > 0)
        {
            var currentMethodId = queue.Dequeue();

            if (!visited.Add(currentMethodId))
                continue;

            if (!callGraph.Methods.TryGetValue(currentMethodId, out var currentMethod))
                continue;

            // If this method is not already async, mark it for transformation
            if (!currentMethod.IsAsync)
            {
                methodsToFlood.Add(currentMethodId);
                currentMethod.RequiresAsyncTransformation = true;
                currentMethod.AsyncReturnType = DetermineAsyncReturnType(currentMethod.ReturnType);
            }

            // Find all callers and add them to the queue
            var callers = callGraph.GetCallers(currentMethodId);
            foreach (var caller in callers)
            {
                queue.Enqueue(caller.Id);
            }
        }

        callGraph.FloodedMethods = methodsToFlood;

        // Mark all calls that need await
        foreach (var call in callGraph.Calls)
        {
            if (callGraph.Methods.TryGetValue(call.CalleeId, out var callee))
            {
                // If the callee is async or needs to be async, this call needs await
                call.RequiresAwait = callee.IsAsync || callee.RequiresAsyncTransformation;
            }
        }

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
                NeedsAsyncKeyword = true
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
}
