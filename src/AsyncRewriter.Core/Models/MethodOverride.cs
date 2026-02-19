using System;
using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record MethodOverride : IMethodOverride
{
    public string Id => $"{BaseMethodId}{OverridingMethodId}";

    public required string CallGraphId { get; init; }
    public required string OverridingMethodId { get; init; }
    public required string BaseMethodId { get; init; }

    public IDictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>()
        {
            [nameof(CallGraphId)] = CallGraphId,
            [nameof(OverridingMethodId)] = OverridingMethodId,
            [nameof(BaseMethodId)] = BaseMethodId
        };
    }

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

    public static IMethodOverride Create(IReadOnlyDictionary<string, object> relationshipProperties)
    {
        return new MethodOverride()
        {
            CallGraphId = relationshipProperties[nameof(CallGraphId)].ToString()!,
            OverridingMethodId = relationshipProperties[nameof(OverridingMethodId)].ToString()!,
            BaseMethodId = relationshipProperties[nameof(BaseMethodId)].ToString()!
        };
    }
}
