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
    private static readonly ImmutableHashSet<string> EfSyncMethodsWithAsyncOverloads = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "ToList", "ToArray", "ToDictionary", "ToLookup",
        "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Last", "LastOrDefault", "Count", "LongCount",
        "Any", "All", "Min", "Max", "Sum", "Average", "Contains",
        "Find", "SaveChanges", "ExecuteSqlCommand", "SqlQuery", "Load", "ForEachAsync");

    private static readonly ImmutableHashSet<string> EfTypes = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "QueryableExtensions", "DbContext", "DbSet", "DbQuery", "Database", "DbExtensions");

    private static readonly ImmutableHashSet<string> EfNamespaces = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "System.Linq.Queryable",
        "System.Data.Entity",
        "System.Data.Entity.Infrastructure",
        "System.Data.Entity.Utilities");

    public ICallGraphWithMetadata<EntityFrameworkMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph)
    {
        var metadata = new Dictionary<string, EntityFrameworkMethodMetadata>();

        foreach (var call in callGraph.Calls)
        {
            if (!callGraph.Methods.TryGetValue(call.CalleeId, out var callee))
            {
                continue;
            }

            if (!IsEfSyncMethodWithAsyncOverload(callee))
            {
                continue;
            }

            if (!callGraph.Methods.TryGetValue(call.CallerId, out var caller))
            {
                continue;
            }

            if (!metadata.ContainsKey(caller.Id))
            {
                metadata[caller.Id] = new EntityFrameworkMethodMetadata
                {
                    IsEntityFrameworkCaller = true,
                    Reason = $"Calls Entity Framework sync method '{callee.Name}' which has an async overload"
                };
            }
        }

        return new CallGraphWithMetadata<EntityFrameworkMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraph.Id,
            callGraph,
            metadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());
    }

    private static bool IsEfSyncMethodWithAsyncOverload(IMethodNode method)
    {
        if (!EfSyncMethodsWithAsyncOverloads.Contains(method.Name))
        {
            return false;
        }

        return EfNamespaces.Contains(method.ContainingNamespace)
               || EfTypes.Contains(method.ContainingType);
    }
}
