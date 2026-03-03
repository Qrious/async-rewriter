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
    public required List<MethodParameter> Parameters { get; init; } = new();
    public required string FilePath { get; init; } = string.Empty;
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public int StartCharacter { get; init; }
    public int EndCharacter { get; init; }
    public required bool IsReturnTypeParameter { get; init; }
    public bool IsInterfaceMethod { get; init; }

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
            { nameof(Parameters), string.Join("|", Parameters.Select(p => p.ToString())) },
            { nameof(FilePath), FilePath },
            { nameof(StartLine), StartLine.ToString() },
            { nameof(EndLine), EndLine.ToString() },
            { nameof(StartCharacter), StartCharacter.ToString() },
            { nameof(EndCharacter), EndCharacter.ToString() },
            { nameof(IsReturnTypeParameter), IsReturnTypeParameter.ToString() },
            { nameof(IsInterfaceMethod), IsInterfaceMethod.ToString() }
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
            Parameters = data[nameof(Parameters)].ToString() is { Length: > 0 } raw
                ? raw.Split('|').Select(MethodParameter.Parse).ToList()
                : new List<MethodParameter>(),
            FilePath = data[nameof(FilePath)].ToString(),
            StartLine = int.Parse(data[nameof(StartLine)]!.ToString()),
            EndLine = int.Parse(data[nameof(EndLine)].ToString()),
            StartCharacter = data.TryGetValue(nameof(StartCharacter), out var sc) ? int.Parse(sc.ToString()) : 0,
            EndCharacter = data.TryGetValue(nameof(EndCharacter), out var ec) ? int.Parse(ec.ToString()) : 0,
            IsReturnTypeParameter =  bool.Parse(data[nameof(IsReturnTypeParameter)].ToString()),
            IsInterfaceMethod = data.TryGetValue(nameof(IsInterfaceMethod), out var im) && bool.Parse(im.ToString()),
        };
    }

    /// <summary>
    /// Returns true if any parameter has the "out" ref kind.
    /// </summary>
    public bool HasOutParameters => Parameters.Any(p => p.RefKind == "out");

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