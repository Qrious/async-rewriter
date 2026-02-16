using System.Collections.Generic;
using System.Linq;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method node in the call graph
/// </summary>
public record MethodNode
{
    public required string CallGraphId { get; init; }
    public required string Id { get; init; } = string.Empty;
    public required string Name { get; init; } = string.Empty;
    public required string ContainingType { get; init; } = string.Empty;
    public required string ContainingNamespace { get; init; } = string.Empty;
    public required string ReturnType { get; init; } = string.Empty;
    public required List<string> Parameters { get; init; } = new();
    public required string FilePath { get; init; } = string.Empty;
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public bool IsReturnTypeParameter { get; init; }

    /// <summary>
    /// Ref kind for each parameter ("out", "ref", "in", or null for none).
    /// Parallel to the Parameters list. Null if no parameters have ref kinds.
    /// </summary>
    public List<string?>? ParameterRefKinds { get; init; }

    /// <summary>
    /// Returns true if any parameter has the "out" ref kind.
    /// </summary>
    public bool HasOutParameters => ParameterRefKinds?.Any(k => k == "out") ?? false;
}
