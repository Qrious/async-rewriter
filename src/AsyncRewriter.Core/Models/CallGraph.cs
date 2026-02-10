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
    
    /// <summary>
    /// Calls indexed by the caller
    /// </summary>
    private ConcurrentDictionary<string, List<MethodCall>> _callsByCaller { get; init; } = new();
    
    private ConcurrentDictionary<string, List<MethodCall>> _callsByCallee { get; init; } = new();

    public CallGraph(ConcurrentBag<MethodCall> methodCalls)
    {
        Calls = methodCalls;
        _callsByCaller = new ConcurrentDictionary<string, List<MethodCall>>(methodCalls
            .GroupBy(v => v.CallerId)
            .Select(grouping => new KeyValuePair<string, List<MethodCall>>(grouping.Key, grouping.ToList())));
        _callsByCallee = new ConcurrentDictionary<string, List<MethodCall>>(methodCalls
            .GroupBy(v => v.CalleeId)
            .Select(grouping => new KeyValuePair<string, List<MethodCall>>(grouping.Key, grouping.ToList())));
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
}
