using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

public class DirtyTaskMethodsExtractor : IDirtyTaskMethodsExtractor
{
    private static readonly Regex FuncTaskRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task>",
        RegexOptions.Compiled);

    private static readonly Regex FuncTaskOfTRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task<(.+?)>>",
        RegexOptions.Compiled);

    public ICallGraphWithMetadata<SyncWrapperMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph)
    {
        var metadata = new Dictionary<string, SyncWrapperMethodMetadata>();

        foreach (var method in callGraph.Methods.Values)
        {
            if (TryGetSyncWrapperReason(method, out var reason))
            {
                metadata[method.Id] = new SyncWrapperMethodMetadata { IsSyncWrapper = true, Reason = reason };
            }
        }

        return new CallGraphWithMetadata<SyncWrapperMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraph.Id,
            callGraph,
            metadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());
    }

    private static bool TryGetSyncWrapperReason(IMethodNode method, out string reason)
    {
        foreach (var param in method.Parameters)
        {
            if (FuncTaskRegex.IsMatch(param) && method.ReturnType == "void")
            {
                reason = "Func<Task> parameter with void return";
                return true;
            }

            var match = FuncTaskOfTRegex.Match(param);
            if (match.Success)
            {
                var innerType = match.Groups[1].Value;
                if (method.ReturnType == innerType)
                {
                    reason = $"Func<Task<{innerType}>> parameter with {innerType} return";
                    return true;
                }
            }
        }

        reason = null!;
        return false;
    }
}
