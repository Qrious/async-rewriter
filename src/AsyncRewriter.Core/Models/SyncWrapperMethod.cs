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
    public string MethodId { get; init; } = string.Empty;

    /// <summary>
    /// Method name
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Fully qualified containing type
    /// </summary>
    public string ContainingType { get; init; } = string.Empty;

    /// <summary>
    /// File path where the method is defined
    /// </summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>
    /// Line number where the method starts
    /// </summary>
    public int StartLine { get; init; }

    /// <summary>
    /// The return type of the method
    /// </summary>
    public string ReturnType { get; init; } = string.Empty;

    /// <summary>
    /// Full method signature
    /// </summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Description of the async parameter pattern detected
    /// </summary>
    public string PatternDescription { get; init; } = string.Empty;
}
