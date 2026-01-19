namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a base type transformation needed when an implementation method
/// becomes async but the generic interface uses a type parameter for the return type.
/// Instead of modifying the interface, the implementation's base type argument is wrapped in Task.
/// Example: IMapper&lt;A, B&gt; becomes IMapper&lt;A, Task&lt;B&gt;&gt;
/// </summary>
public class BaseTypeTransformation
{
    /// <summary>
    /// The full name of the class that needs its base type transformed
    /// </summary>
    public string ContainingTypeName { get; set; } = string.Empty;

    /// <summary>
    /// The full name of the generic interface being implemented
    /// Example: "IMapper&lt;A, B&gt;"
    /// </summary>
    public string InterfaceTypeName { get; set; } = string.Empty;

    /// <summary>
    /// The index of the type argument that should be wrapped in Task&lt;&gt;
    /// </summary>
    public int TypeArgumentIndex { get; set; }
}
