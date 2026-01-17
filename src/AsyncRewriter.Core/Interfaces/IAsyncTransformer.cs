using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Transforms synchronous code to async code based on call graph analysis
/// </summary>
public interface IAsyncTransformer
{
    /// <summary>
    /// Transforms a project from sync to async based on the call graph
    /// </summary>
    Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transforms a single file from sync to async
    /// </summary>
    Task<FileTransformation> TransformFileAsync(
        string filePath,
        List<AsyncTransformationInfo> transformations,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transforms source code from sync to async
    /// </summary>
    Task<string> TransformSourceAsync(
        string sourceCode,
        List<AsyncTransformationInfo> transformations,
        CancellationToken cancellationToken = default);
}
