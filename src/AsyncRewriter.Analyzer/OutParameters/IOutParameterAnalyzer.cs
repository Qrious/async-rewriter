using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

public interface IOutParameterAnalyzer
{
    /// <summary>
    /// Finds all methods in the async (flooded) call graph that have out parameters and need transformation.
    /// </summary>
    ICallGraphWithMetadata<OutParameterMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> DetectOutParameterMethods(
        ICallGraph originalGraph, ICallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> asyncGraph);
}