using System;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a call from one method to another in the call graph
/// </summary>
public class MethodCall
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string CallerId { get; init; } = string.Empty;
    public string CalleeId { get; init; } = string.Empty;
    public string CallerSignature { get; init; } = string.Empty;
    public string CalleeSignature { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Indicates if this call needs await keyword after transformation
    /// </summary>
    public bool RequiresAwait { get; set; }
}
