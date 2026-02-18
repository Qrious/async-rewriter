using System;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record MethodOverride : IMethodOverride
{
    public required string CallGraphId { get; init; }
    public required string OverridingMethodId { get; init; }
    public required string BaseMethodId { get; init; }

    public virtual bool Equals(IMethodOverride? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return CallGraphId == other.CallGraphId && OverridingMethodId == other.OverridingMethodId && BaseMethodId == other.BaseMethodId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CallGraphId, OverridingMethodId, BaseMethodId);
    }
}
