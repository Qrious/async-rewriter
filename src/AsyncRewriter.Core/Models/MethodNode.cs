using System.Collections.Generic;

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
}
