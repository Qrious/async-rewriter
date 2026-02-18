using System;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public record InterfaceImplementation : IInterfaceImplementation
{
    public required string CallGraphId { get; init; }
    public required string ImplementingMethodId { get; init; }
    public required string InterfaceMethodId { get; init; }

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
}
