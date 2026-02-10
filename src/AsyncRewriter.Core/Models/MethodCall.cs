using System;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a call from one method to another in the call graph
/// </summary>
public record MethodCall
{
    public required string CallGraphId { get; init; }
    public required string Id { get; init; }
    public required string CallerId { get; init; }
    public required string CalleeId { get; init; }
    public required int LineNumber { get; init; }
    public required string FilePath { get; init; }
}
