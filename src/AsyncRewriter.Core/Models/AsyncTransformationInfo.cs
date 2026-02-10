using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Contains information about methods that need async transformation
/// </summary>
public class AsyncTransformationInfo
{
    public string MethodId { get; init; } = string.Empty;
    public string OriginalReturnType { get; init; } = string.Empty;
    public string NewReturnType { get; init; } = string.Empty;
    public bool NeedsAsyncKeyword { get; init; }
    public List<CallSiteTransformation> CallSitesToTransform { get; init; } = new();

    /// <summary>
    /// Interface method IDs that this method implements (for adding await to interface calls)
    /// </summary>
    public List<string> ImplementsInterfaceMethods { get; init; } = new();
}

/// <summary>
/// Represents a call site that needs to be transformed to use await
/// </summary>
public class CallSiteTransformation
{
    public string FilePath { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string OriginalCallExpression { get; init; } = string.Empty;
    public string NewCallExpression { get; init; } = string.Empty;
    public string CalledMethodSignature { get; init; } = string.Empty;
}
