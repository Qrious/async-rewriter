using System.Collections.Concurrent;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Server.Repositories;

/// <summary>
/// In-memory implementation of ICallGraphRepository
/// </summary>
public class InMemoryCallGraphRepository : ICallGraphRepository
{
    private readonly ConcurrentDictionary<string, CallGraph> _callGraphsById = new();
    private readonly ConcurrentDictionary<string, List<string>> _callGraphIdsByProject = new();

    public Task StoreCallGraphAsync(CallGraph callGraph, CancellationToken cancellationToken = default)
    {
        _callGraphsById[callGraph.Id] = callGraph;

        _callGraphIdsByProject.AddOrUpdate(
            callGraph.ProjectName,
            _ => new List<string> { callGraph.Id },
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(callGraph.Id);
                }
                return list;
            });

        return Task.CompletedTask;
    }

    public Task<CallGraph?> GetCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        _callGraphsById.TryGetValue(id, out var callGraph);
        return Task.FromResult(callGraph);
    }

    public Task<CallGraph?> GetCallGraphByProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        if (!_callGraphIdsByProject.TryGetValue(projectName, out var ids) || ids.Count == 0)
        {
            return Task.FromResult<CallGraph?>(null);
        }

        // Return the most recently added (last in list)
        CallGraph? mostRecent = null;
        lock (ids)
        {
            for (int i = ids.Count - 1; i >= 0; i--)
            {
                if (_callGraphsById.TryGetValue(ids[i], out var cg))
                {
                    if (mostRecent == null || cg.CreatedAt > mostRecent.CreatedAt)
                    {
                        mostRecent = cg;
                    }
                }
            }
        }

        return Task.FromResult(mostRecent);
    }

    public Task<List<MethodNode>> FindCallersAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        var callers = new Dictionary<string, MethodNode>();

        foreach (var callGraph in _callGraphsById.Values)
        {
            if (!callGraph.Methods.ContainsKey(methodId))
                continue;

            // Build caller lookup: calleeId -> list of callerIds
            var callerLookup = new Dictionary<string, List<string>>();
            foreach (var call in callGraph.Calls)
            {
                if (!callerLookup.TryGetValue(call.CalleeId, out var list))
                {
                    list = new List<string>();
                    callerLookup[call.CalleeId] = list;
                }
                list.Add(call.CallerId);
            }

            // BFS to find all callers up to specified depth
            var visited = new HashSet<string> { methodId };
            var queue = new Queue<(string MethodId, int Depth)>();
            queue.Enqueue((methodId, 0));

            while (queue.Count > 0)
            {
                var (currentId, currentDepth) = queue.Dequeue();

                if (depth > 0 && currentDepth >= depth)
                    continue;

                if (callerLookup.TryGetValue(currentId, out var callerIds))
                {
                    foreach (var callerId in callerIds)
                    {
                        if (visited.Add(callerId) && callGraph.Methods.TryGetValue(callerId, out var caller))
                        {
                            callers[callerId] = caller;
                            queue.Enqueue((callerId, currentDepth + 1));
                        }
                    }
                }
            }
        }

        return Task.FromResult(callers.Values.ToList());
    }

    public Task<List<MethodNode>> FindCalleesAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        var callees = new Dictionary<string, MethodNode>();

        foreach (var callGraph in _callGraphsById.Values)
        {
            if (!callGraph.Methods.ContainsKey(methodId))
                continue;

            // Build callee lookup: callerId -> list of calleeIds
            var calleeLookup = new Dictionary<string, List<string>>();
            foreach (var call in callGraph.Calls)
            {
                if (!calleeLookup.TryGetValue(call.CallerId, out var list))
                {
                    list = new List<string>();
                    calleeLookup[call.CallerId] = list;
                }
                list.Add(call.CalleeId);
            }

            // BFS to find all callees up to specified depth
            var visited = new HashSet<string> { methodId };
            var queue = new Queue<(string MethodId, int Depth)>();
            queue.Enqueue((methodId, 0));

            while (queue.Count > 0)
            {
                var (currentId, currentDepth) = queue.Dequeue();

                if (depth > 0 && currentDepth >= depth)
                    continue;

                if (calleeLookup.TryGetValue(currentId, out var calleeIds))
                {
                    foreach (var calleeId in calleeIds)
                    {
                        if (visited.Add(calleeId) && callGraph.Methods.TryGetValue(calleeId, out var callee))
                        {
                            callees[calleeId] = callee;
                            queue.Enqueue((calleeId, currentDepth + 1));
                        }
                    }
                }
            }
        }

        return Task.FromResult(callees.Values.ToList());
    }

    public Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        if (_callGraphsById.TryRemove(id, out var removed))
        {
            if (_callGraphIdsByProject.TryGetValue(removed.ProjectName, out var ids))
            {
                lock (ids)
                {
                    ids.Remove(id);
                }
            }
        }

        return Task.CompletedTask;
    }
}
