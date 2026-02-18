using System;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record GenericInstantiation : IGenericInstantiation
{
    public required string CallGraphId { get; init; }
    public required string InstantiatedMethodId { get; init; }
    public required string GenericMethodId { get; init; }

    public virtual bool Equals(IGenericInstantiation? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return CallGraphId == other.CallGraphId && InstantiatedMethodId == other.InstantiatedMethodId && GenericMethodId == other.GenericMethodId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CallGraphId, InstantiatedMethodId, GenericMethodId);
    }
}