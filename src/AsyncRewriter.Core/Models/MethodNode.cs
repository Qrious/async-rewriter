using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method node in the call graph
/// </summary>
public record MethodNode
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ContainingType { get; init; } = string.Empty;
    public string ContainingNamespace { get; init; } = string.Empty;
    public string ReturnType { get; init; } = string.Empty;
    public List<string> Parameters { get; init; } = new();
    public string FilePath { get; init; } = string.Empty;
    public int StartLine { get; init; }
    public int EndLine { get; init; }

    /// <summary>
    /// Indicates if this method is already async
    /// </summary>
    public bool IsAsync { get; init; }

    /// <summary>
    /// Indicates if this method needs to be converted to async
    /// </summary>
    public bool RequiresAsyncTransformation { get; init; }

    /// <summary>
    /// Method that caused this method to require async propagation
    /// </summary>
    public string? AsyncPropagationSourceMethodId { get; init; }

    /// <summary>
    /// Indicates this method is a sync wrapper around async code
    /// </summary>
    public bool IsSyncWrapper { get; init; }

    /// <summary>
    /// The new return type after async transformation (e.g., Task<T>)
    /// </summary>
    public string? AsyncReturnType { get; init; }

    /// <summary>
    /// Full method signature
    /// </summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Source code of the method
    /// </summary>
    public string? SourceCode { get; init; }

    /// <summary>
    /// IDs of interface methods that this method implements
    /// </summary>
    public List<string> ImplementsInterfaceMethods { get; init; } = new();

    /// <summary>
    /// Indicates if this method is declared in an interface
    /// </summary>
    public bool IsInterfaceMethod { get; init; }

    /// <summary>
    /// Indicates if the return type is a type parameter of a generic interface.
    /// When true, the interface should not be modified - instead implementations
    /// should change their base type argument (e.g., IMapper&lt;A, B&gt; becomes IMapper&lt;A, Task&lt;B&gt;&gt;)
    /// </summary>
    public bool IsReturnTypeParameter { get; init; }

    /// <summary>
    /// For interface methods with type parameter returns, this is the index of the
    /// type parameter in the generic interface's type parameter list
    /// </summary>
    public int? ReturnTypeParameterIndex { get; init; }
}
