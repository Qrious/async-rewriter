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

    public IDictionary<string, string> ToDictionary()
    {
        return new Dictionary<string, string>
        {
            { nameof(CallGraphId), CallGraphId },
            { nameof(Id), Id },
            { nameof(Name).ToLower(), Name }, // To lower, so the db viewer shows it in the graph view
            { nameof(ContainingType), ContainingType },
            { nameof(ContainingNamespace), ContainingNamespace },
            { nameof(ReturnType), ReturnType },
            { nameof(Parameters), string.Join(", ", Parameters) },
            { nameof(FilePath), FilePath },
            { nameof(StartLine), StartLine.ToString() },
            { nameof(EndLine), EndLine.ToString() },
            { nameof(IsReturnTypeParameter), IsReturnTypeParameter.ToString() }
        };
    }

    public static IMethodNode Create(IReadOnlyDictionary<string, object> data)
    {
        return new MethodNode
        {
            CallGraphId = data[nameof(CallGraphId)].ToString() ?? string.Empty,
            Id = data[nameof(Id)].ToString(),
            Name = data[nameof(Name).ToLower()].ToString(),
            ContainingType = data[nameof(ContainingType)].ToString(),
            ContainingNamespace = data[nameof(ContainingNamespace)].ToString(),
            ReturnType = data[nameof(ReturnType)].ToString(),
            Parameters = data[nameof(Parameters)].ToString().Split(", ").ToList(),
            FilePath = data[nameof(FilePath)].ToString(),
            StartLine = int.Parse(data[nameof(StartLine)]!.ToString()),
            EndLine = int.Parse(data[nameof(EndLine)].ToString()),
            IsReturnTypeParameter =  bool.Parse(data[nameof(IsReturnTypeParameter)].ToString()),
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