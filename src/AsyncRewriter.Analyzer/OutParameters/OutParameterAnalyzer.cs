using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Detects flooded methods that have out parameters and classifies their transformation strategy.
/// </summary>
public class OutParameterAnalyzer : IOutParameterAnalyzer
{
    /// <summary>
    /// Finds all methods in the async (flooded) call graph that have out parameters and need transformation.
    /// </summary>
    public ICallGraphWithMetadata<OutParameterMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> DetectOutParameterMethods(
        ICallGraph originalGraph, ICallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> asyncGraph)
    {
        var results = new Dictionary<string, OutParameterMetadata>();

        foreach (var (id, asyncMethod) in asyncGraph.Methods)
        {
            if (!originalGraph.Methods.TryGetValue(id, out var originalMethodValue) || originalMethodValue is not MethodNode originalMethod)
            {
                continue;
            }

            // Only consider methods whose return type changed (i.e., flooded)
            if (originalMethod.ReturnType == asyncMethod.ReturnType)
            {
                continue;
            }

            if (!originalMethod.HasOutParameters)
            {
                continue;
            }

            if (string.IsNullOrEmpty(originalMethod.FilePath) || originalMethod.FilePath == "external")
            {
                continue;
            }

            var refKinds = originalMethod.ParameterRefKinds!;
            var outIndices = new List<int>();
            var outTypes = new List<string>();
            var outNames = new List<string>();

            for (int i = 0; i < refKinds.Count; i++)
            {
                if (refKinds[i] == "out")
                {
                    outIndices.Add(i);
                    var param = originalMethod.Parameters[i];
                    var spaceIdx = param.LastIndexOf(' ');
                    outTypes.Add(spaceIdx >= 0 ? param.Substring(0, spaceIdx) : param);
                    outNames.Add(spaceIdx >= 0 ? param.Substring(spaceIdx + 1) : $"out{i}");
                }
            }

            var originalReturnType = originalMethod.ReturnType;
            var isBoolReturn = originalReturnType is "bool" or "Boolean" or "System.Boolean";
            var kind = isBoolReturn ? OutParameterTransformKind.BoolTryPattern : OutParameterTransformKind.TuplePattern;
            var newAsyncReturnType = ComputeNewReturnType(kind, originalReturnType, outTypes, outNames);

            results.Add(id, new OutParameterMetadata
            {
                OriginalReturnType = originalReturnType,
                TransformKind = kind,
                OutParameterIndices = outIndices,
                OutParameterTypes = outTypes,
                OutParameterNames = outNames,
                NewAsyncReturnType = newAsyncReturnType
            });
        }

        return new CallGraphWithMetadata<OutParameterMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(asyncGraph.Id, originalGraph, results,
            new Dictionary<string, EmptyGraphMetadata>(), new Dictionary<string, EmptyGraphMetadata>(), new Dictionary<string, EmptyGraphMetadata>());
    }

    private static string ComputeNewReturnType(
        OutParameterTransformKind kind,
        string originalReturnType,
        List<string> outTypes,
        List<string> outNames)
    {
        if (kind == OutParameterTransformKind.BoolTryPattern)
        {
            string innerType;

            if (outTypes.Count == 1)
            {
                innerType = outTypes[0];
            }
            else
            {
                var tupleElements = outTypes.Zip(outNames, (t, n) => $"{t} {n}");
                innerType = $"({string.Join(", ", tupleElements)})";
            }

            return $"Task<AsyncOutResult<{innerType}>>";
        }
        else
        {
            var elements = new List<string>
            {
                $"{originalReturnType} Result"
            };

            for (int i = 0; i < outTypes.Count; i++)
            {
                elements.Add($"{outTypes[i]} {outNames[i]}");
            }

            return $"Task<({string.Join(", ", elements)})>";
        }
    }
}