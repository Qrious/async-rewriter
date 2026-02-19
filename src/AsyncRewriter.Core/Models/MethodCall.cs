using System;
using System.Collections.Generic;
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

    public IDictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>()
        {
            [nameof(CallGraphId)] = CallGraphId,
            [nameof(Id)] = Id,
            [nameof(CallerId)] = CallerId,
            [nameof(CalleeId)] = CalleeId,
            [nameof(LineNumber)] = LineNumber.ToString(),
            [nameof(FilePath)] = FilePath
        };
    }

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

    public static IMethodCall Create(IReadOnlyDictionary<string, object> data)
    {
        return new MethodCall
        {
            CallGraphId = data[nameof(CallGraphId)].ToString()!,
            Id = data[nameof(Id)].ToString()!,
            CallerId = data[nameof(CallerId)].ToString()!,
            CalleeId = data[nameof(CalleeId)].ToString()!,
            LineNumber = int.Parse(data[nameof(LineNumber)].ToString()!),
            FilePath = data[nameof(FilePath)].ToString()!
        };
    }
}
