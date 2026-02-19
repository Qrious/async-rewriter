using System;
using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record InterfaceImplementation : IInterfaceImplementation
{
    public string Id => $"{ImplementingMethodId}-{InterfaceMethodId}";
    public required string CallGraphId { get; init; }
    public required string ImplementingMethodId { get; init; }
    public required string InterfaceMethodId { get; init; }

    public IDictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>()
        {
            [nameof(CallGraphId)] = CallGraphId,
            [nameof(ImplementingMethodId)] = ImplementingMethodId,
            [nameof(InterfaceMethodId)] = InterfaceMethodId
        };
    }

    public virtual bool Equals(IInterfaceImplementation? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return CallGraphId == other.CallGraphId && ImplementingMethodId == other.ImplementingMethodId && InterfaceMethodId == other.InterfaceMethodId;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CallGraphId, ImplementingMethodId, InterfaceMethodId);
    }

    public static IInterfaceImplementation Create(IReadOnlyDictionary<string, object> relationshipProperties)
    {
        return new InterfaceImplementation
        {
            CallGraphId = relationshipProperties[nameof(CallGraphId)].ToString()!,
            ImplementingMethodId = relationshipProperties[nameof(ImplementingMethodId)].ToString()!,
            InterfaceMethodId = relationshipProperties[nameof(InterfaceMethodId)].ToString()!
        };
    }
}
