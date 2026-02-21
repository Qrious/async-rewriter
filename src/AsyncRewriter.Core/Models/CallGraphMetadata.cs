using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Marker interface for metadata that can be attached to a call graph.
/// </summary>
public interface ICallGraphMetadata { }

/// <summary>
/// Metadata produced by the async call graph flooding process, capturing
/// which methods were flooded and how the flooding propagated.
/// </summary>
public class CallGraphMetadata : ICallGraphMetadata
{
    /// <summary>
    /// The BFS flooding trace, including each flooded method's depth,
    /// the method that caused it to be flooded, and the flood reason.
    /// </summary>
    public FloodingResult FloodingResult { get; init; } = new();

    /// <summary>
    /// The root method IDs that seeded the flooding (i.e. the methods
    /// initially marked as requiring async transformation).
    /// </summary>
    public IReadOnlySet<string> RootMethodIds { get; init; } = new HashSet<string>();
}
