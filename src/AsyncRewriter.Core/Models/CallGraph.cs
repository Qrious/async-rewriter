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
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ConcurrentDictionary<string, MethodNode> Methods { get; set; } = new();
    public ConcurrentBag<MethodCall> Calls { get; set; } = new();

    /// <summary>
    /// Root methods that were marked for async transformation
    /// </summary>
    public HashSet<string> RootAsyncMethods { get; set; } = new();

    /// <summary>
    /// Sync wrapper methods (like AsyncHelper.RunSync) whose calls should be unwrapped
    /// </summary>
    public HashSet<string> SyncWrapperMethods { get; set; } = new();

    /// <summary>
    /// Methods affected by async flooding (need to become async)
    /// </summary>
    public HashSet<string> FloodedMethods { get; set; } = new();

    /// <summary>
    /// Add a method to the graph
    /// </summary>
    public void AddMethod(MethodNode method)
    {
        Methods.AddOrUpdate(method.Id, method, (key, existing) => method);
    }

    /// <summary>
    /// Add a call relationship
    /// </summary>
    public void AddCall(MethodCall call)
    {
        Calls.Add(call);
    }

    /// <summary>
    /// Get all methods that call the specified method
    /// </summary>
    public IEnumerable<MethodNode> GetCallers(string methodId)
    {
        var callerIds = Calls
            .Where(c => c.CalleeId == methodId)
            .Select(c => c.CallerId)
            .Distinct();

        return callerIds
            .Where(id => Methods.ContainsKey(id))
            .Select(id => Methods[id])
            .Where(m => m != null);
    }

    /// <summary>
    /// Get all methods that call the specified method, including calls through interface methods
    /// </summary>
    public IEnumerable<MethodNode> GetCallersIncludingInterfaceCalls(string methodId)
    {
        // Start with direct callers
        var directCallers = GetCallers(methodId);

        // If this method implements interface methods, also get callers of those interface methods
        if (Methods.TryGetValue(methodId, out var method))
        {
            foreach (var interfaceMethodId in method.ImplementsInterfaceMethods)
            {
                var interfaceCallers = GetCallers(interfaceMethodId);
                directCallers = directCallers.Concat(interfaceCallers);
            }
        }

        // If this method is an interface method, also get callers of its implementations
        if (Methods.TryGetValue(methodId, out var interfaceMethod) && interfaceMethod.IsInterfaceMethod)
        {
            var implementationCallers = Methods.Values
                .Where(m => m.ImplementsInterfaceMethods.Contains(methodId))
                .SelectMany(m => GetCallers(m.Id));

            directCallers = directCallers.Concat(implementationCallers);
        }

        return directCallers.DistinctBy(m => m.Id);
    }

    /// <summary>
    /// Get all methods called by the specified method
    /// </summary>
    public IEnumerable<MethodNode> GetCallees(string methodId)
    {
        var calleeIds = Calls
            .Where(c => c.CallerId == methodId)
            .Select(c => c.CalleeId)
            .Distinct();

        return calleeIds.Select(id => Methods[id]).Where(m => m != null);
    }
}
