using System;
using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method node in the call graph
/// </summary>
public record MethodNode : IMethodNode
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

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>
        {
            { "CallGraphId", CallGraphId },
            { "Id", Id },
            { "Name", Name },
            { "ContainingType", ContainingType },
            { "ContainingNamespace", ContainingNamespace },
            { "ReturnType", ReturnType },
            { "Parameters", string.Join(", ", Parameters) },
            { "FilePath", FilePath },
            { "StartLine", StartLine.ToString() },
            { "EndLine", EndLine.ToString() },
            { "IsReturnTypeParameter", IsReturnTypeParameter.ToString() }
        };
    }

    public static IMethodNode Create(IReadOnlyDictionary<string, string> data)
    {
        return new MethodNode
        {
            CallGraphId = data["CallGraphId"],
            Id = data["Id"],
            Name = data["Name"],
            ContainingType = data["ContainingType"],
            ContainingNamespace = data["ContainingNamespace"],
            ReturnType = data["ReturnType"],
            Parameters = data["Parameters"].Split(',').Select(p => p.Trim()).ToList(),
            FilePath = data["FilePath"],
            StartLine = int.Parse(data["StartLine"]),
            EndLine = int.Parse(data["EndLine"]),
            IsReturnTypeParameter = bool.Parse(data["IsReturnTypeParameter"])
        };
    }

    /// <summary>
    /// Ref kind for each parameter ("out", "ref", "in", or null for none).
    /// Parallel to the Parameters list. Null if no parameters have ref kinds.
    /// </summary>
    public List<string?>? ParameterRefKinds { get; init; }

    /// <summary>
    /// Returns true if any parameter has the "out" ref kind.
    /// </summary>
    public bool HasOutParameters => ParameterRefKinds?.Any(k => k == "out") ?? false;

    public virtual bool Equals(IMethodNode? other)
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