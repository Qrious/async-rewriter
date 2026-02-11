using System.Collections.Generic;
using System.Text.RegularExpressions;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

public class TaskWrapperExtractor : ITaskWrapperExtractor
{
    private static readonly Regex FuncTaskRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task>",
        RegexOptions.Compiled);

    private static readonly Regex FuncTaskOfTRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task<(.+?)>>",
        RegexOptions.Compiled);

    public List<SyncWrapperMethod> Extract(CallGraph callGraph)
    {
        var results = new List<SyncWrapperMethod>();

        foreach (var method in callGraph.Methods.Values)
        {
            var wrapper = TryMatchTaskWrapper(method);
            if (wrapper != null)
                results.Add(wrapper);
        }

        return results;
    }

    private static SyncWrapperMethod? TryMatchTaskWrapper(MethodNode method)
    {
        foreach (var param in method.Parameters)
        {
            // Check Func<Task> with void return
            if (FuncTaskRegex.IsMatch(param) && method.ReturnType == "void")
            {
                return CreateWrapper(method, "Func<Task> parameter with void return");
            }

            // Check Func<Task<T>> with T return
            var match = FuncTaskOfTRegex.Match(param);
            if (match.Success)
            {
                var innerType = match.Groups[1].Value;
                if (method.ReturnType == innerType)
                {
                    return CreateWrapper(method, $"Func<Task<{innerType}>> parameter with {innerType} return");
                }
            }
        }

        return null;
    }

    private static SyncWrapperMethod CreateWrapper(MethodNode method, string pattern)
    {
        return new SyncWrapperMethod
        {
            MethodId = method.Id,
            Name = method.Name,
            ContainingType = method.ContainingType,
            FilePath = method.FilePath,
            StartLine = method.StartLine,
            ReturnType = method.ReturnType,
            Signature = $"{method.ReturnType} {method.ContainingType}.{method.Name}({string.Join(", ", method.Parameters)})",
            PatternDescription = pattern
        };
    }
}
