using System;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record GenericInstantiation : IGenericInstantiation, IIdentifiable
{
    public required string CallGraphId { get; init; }
    public required string InstantiatedMethodId { get; init; }
    public required string GenericMethodId { get; init; }

    public string Id => $"{InstantiatedMethodId}_generic_{GenericMethodId}";

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