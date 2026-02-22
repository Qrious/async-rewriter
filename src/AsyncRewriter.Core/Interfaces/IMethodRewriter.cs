using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Transforms synchronous code to async code based on call graph analysis
/// </summary>
public interface IAsyncTransformer
{
    /// <summary>
    /// Transforms a project from sync to async based on the call graph with progress reporting and debug output
    /// </summary>
    Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        Action<string, int, int> progressCallback,
        bool debug,
        CancellationToken cancellationToken = default);
    
}
