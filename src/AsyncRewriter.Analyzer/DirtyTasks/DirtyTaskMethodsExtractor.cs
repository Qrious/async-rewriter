using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

    public List<DirtyTaskMethodInfo> Extract(ICallGraph callGraph)
    {
        var results = new List<DirtyTaskMethodInfo>();

        foreach (var method in callGraph.Methods.Values)
        {
            if (IsDirtyTaskMethod(method, out var dirtyTaskMethodInfo))
            {
                results.Add(dirtyTaskMethodInfo);
            }
        }

        return results;
    }

    private static bool IsDirtyTaskMethod(IMethodNode method, [NotNullWhen(true)] out DirtyTaskMethodInfo? dirtyTaskMethodInfo)
    {
        dirtyTaskMethodInfo = null;

        foreach (var param in method.Parameters)
        {
            // Check Func<Task> with void return
            if (FuncTaskRegex.IsMatch(param) && method.ReturnType == "void")
            {
                dirtyTaskMethodInfo = new DirtyTaskMethodInfo(method.Id, " Func<Task> parameter with void return");

                return true;
            }

            // Check Func<Task<T>> with T return
            var match = FuncTaskOfTRegex.Match(param);

            if (match.Success)
            {
                var innerType = match.Groups[1].Value;

                if (method.ReturnType == innerType)
                {
                    dirtyTaskMethodInfo = new DirtyTaskMethodInfo(method.Id, $" Func<Task<{innerType}>> parameter with {innerType} return");

                    return true;
                }
            }
        }

        return false;
    }
}