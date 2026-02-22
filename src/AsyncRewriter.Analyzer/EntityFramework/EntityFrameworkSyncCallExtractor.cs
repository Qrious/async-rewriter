using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer.EntityFramework;

/// <summary>
/// Detects callers of Entity Framework 6 synchronous methods that have async overloads,
/// and returns those callers as root methods for async flooding.
/// </summary>
public class EntityFrameworkSyncCallExtractor : IEntityFrameworkSyncCallExtractor
{
    // EF6 sync methods (from System.Data.Entity namespace) that have async equivalents (e.g., ToListAsync, SaveChangesAsync)
    private static readonly ImmutableHashSet<string> EfSyncMethodsWithAsyncOverloads = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "ToList",
        "ToArray",
        "ToDictionary",
        "ToLookup",
        "First",
        "FirstOrDefault",
        "Single",
        "SingleOrDefault",
        "Last",
        "LastOrDefault",
        "Count",
        "LongCount",
        "Any",
        "All",
        "Min",
        "Max",
        "Sum",
        "Average",
        "Contains",
        "Find",
        "SaveChanges",
        "ExecuteSqlCommand",
        "SqlQuery",
        "Load",
        "ForEachAsync"
    );

    // Known EF6 types that provide these methods
    private static readonly ImmutableHashSet<string> EfTypes = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "QueryableExtensions",
        "DbContext",
        "DbSet",
        "DbQuery",
        "Database",
        "DbExtensions"
    );

    // Known EF6 namespaces
    private static readonly ImmutableHashSet<string> EfNamespaces = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "System.Data.Entity",
        "System.Data.Entity.Infrastructure",
        "System.Data.Entity.Utilities"
    );

    public List<DirtyTaskMethodInfo> Extract(ICallGraph callGraph)
    {
        var callerIds = new HashSet<string>();
        var results = new List<DirtyTaskMethodInfo>();

        foreach (var call in callGraph.Calls)
        {
            if (!callGraph.Methods.TryGetValue(call.CalleeId, out var callee))
                continue;

            if (!IsEfSyncMethodWithAsyncOverload(callee))
                continue;

            if (!callGraph.Methods.TryGetValue(call.CallerId, out var caller))
                continue;

            if (callerIds.Add(caller.Id))
            {
                results.Add(new DirtyTaskMethodInfo(
                    caller.Id,
                    $"Calls Entity Framework sync method '{callee.Name}' which has an async overload"));
            }
        }

        return results;
    }

    private static bool IsEfSyncMethodWithAsyncOverload(IMethodNode method)
    {
        if (!EfSyncMethodsWithAsyncOverloads.Contains(method.Name))
            return false;

        return EfNamespaces.Contains(method.ContainingNamespace)
               || EfTypes.Contains(method.ContainingType);
    }
}
