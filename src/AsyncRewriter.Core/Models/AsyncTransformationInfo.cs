using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Contains information about methods that need async transformation
/// </summary>
public class AsyncTransformationInfo
{
    public string MethodId { get; set; } = string.Empty;
    public string OriginalReturnType { get; set; } = string.Empty;
    public string NewReturnType { get; set; } = string.Empty;
    public bool NeedsAsyncKeyword { get; set; }
    public List<CallSiteTransformation> CallSitesToTransform { get; set; } = new();
}

/// <summary>
/// Represents a call site that needs to be transformed to use await
/// </summary>
public class CallSiteTransformation
{
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string OriginalCallExpression { get; set; } = string.Empty;
    public string NewCallExpression { get; set; } = string.Empty;
    public string CalledMethodSignature { get; set; } = string.Empty;
}
