namespace AsyncRewriter.Core.Models;

/// <summary>
/// A call graph paired with strongly-typed metadata of type <typeparamref name="TMetadata"/>.
/// </summary>
/// <typeparam name="TMetadata">The type of metadata attached to this call graph.</typeparam>
public interface ICallGraphWithMetadata<out TMetadata> where TMetadata : ICallGraphMetadata
{
    /// <summary>The call graph.</summary>
    CallGraph CallGraph { get; }

    /// <summary>Metadata associated with the call graph.</summary>
    TMetadata Metadata { get; }
}

/// <summary>
/// The result of an async flooding analysis: the transformed call graph
/// paired with flooding metadata.
/// </summary>
public class FloodedCallGraph : ICallGraphWithMetadata<CallGraphMetadata>
{
    /// <summary>
    /// The call graph produced by the flooding analysis, with return types
    /// already transformed (void → Task, T → Task&lt;T&gt;) for all flooded methods.
    /// </summary>
    public required CallGraph CallGraph { get; init; }

    /// <summary>
    /// Metadata describing the flooding: which methods were flooded, why,
    /// and from which root methods the flooding originated.
    /// </summary>
    public required CallGraphMetadata Metadata { get; init; }
}
