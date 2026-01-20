namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a mapping from a synchronous interface to its async equivalent.
/// When a sync interface needs to be made async, it will be replaced with the async interface instead.
/// Example: IRepository -> IRepositoryAsync
/// </summary>
public class InterfaceMapping
{
    /// <summary>
    /// The full name of the synchronous interface to be replaced
    /// Example: "MyNamespace.IRepository"
    /// </summary>
    public string SyncInterfaceName { get; set; } = string.Empty;

    /// <summary>
    /// The full name of the async interface to use as replacement
    /// Example: "MyNamespace.IRepositoryAsync"
    /// </summary>
    public string AsyncInterfaceName { get; set; } = string.Empty;

    /// <summary>
    /// Optional: Additional namespaces that might need to be added when using the async interface
    /// </summary>
    public List<string> RequiredNamespaces { get; set; } = new();
}
