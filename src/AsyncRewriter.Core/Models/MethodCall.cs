using System;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a call from one method to another in the call graph
/// </summary>
public record MethodCall : IMethodCall
{
    public required string CallGraphId { get; init; }
    public required string Id { get; init; }
    public required string CallerId { get; init; }
    public required string CalleeId { get; init; }
    public required int LineNumber { get; init; }
    public required string FilePath { get; init; }

    public virtual bool Equals(IMethodCall? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return CallGraphId == other.CallGraphId && Id == other.Id;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CallGraphId, Id);
    }
}
