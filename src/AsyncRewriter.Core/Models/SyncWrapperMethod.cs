namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method that wraps async operations synchronously
/// (has Func&lt;Task&gt; or Func&lt;Task&lt;TResult&gt;&gt; parameters and returns void or TResult)
/// </summary>
public class SyncWrapperMethod
{
    /// <summary>
    /// Unique method identifier
    /// </summary>
    public string MethodId { get; set; } = string.Empty;

    /// <summary>
    /// Method name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified containing type
    /// </summary>
    public string ContainingType { get; set; } = string.Empty;

    /// <summary>
    /// File path where the method is defined
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Line number where the method starts
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// The return type of the method
    /// </summary>
    public string ReturnType { get; set; } = string.Empty;

    /// <summary>
    /// Full method signature
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Description of the async parameter pattern detected
    /// </summary>
    public string PatternDescription { get; set; } = string.Empty;
}
