using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method node in the call graph
/// </summary>
public class MethodNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string ContainingNamespace { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }

    /// <summary>
    /// Indicates if this method is already async
    /// </summary>
    public bool IsAsync { get; set; }

    /// <summary>
    /// Indicates if this method needs to be converted to async
    /// </summary>
    public bool RequiresAsyncTransformation { get; set; }

    /// <summary>
    /// Indicates this method is a sync wrapper around async code
    /// </summary>
    public bool IsSyncWrapper { get; set; }

    /// <summary>
    /// The new return type after async transformation (e.g., Task<T>)
    /// </summary>
    public string? AsyncReturnType { get; set; }

    /// <summary>
    /// Full method signature
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Source code of the method
    /// </summary>
    public string? SourceCode { get; set; }

    /// <summary>
    /// IDs of interface methods that this method implements
    /// </summary>
    public List<string> ImplementsInterfaceMethods { get; set; } = new();

    /// <summary>
    /// Indicates if this method is declared in an interface
    /// </summary>
    public bool IsInterfaceMethod { get; set; }
}
