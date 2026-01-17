namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a call from one method to another in the call graph
/// </summary>
public class MethodCall
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CallerId { get; set; } = string.Empty;
    public string CalleeId { get; set; } = string.Empty;
    public string CallerSignature { get; set; } = string.Empty;
    public string CalleeSignature { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Indicates if this call needs await keyword after transformation
    /// </summary>
    public bool RequiresAwait { get; set; }
}
