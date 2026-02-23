using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Classifies how an out-parameter method should be transformed for async.
/// </summary>
public enum OutParameterTransformKind
{
    None,
    /// <summary>Bool return + out params → AsyncOutResult&lt;T&gt;</summary>
    BoolTryPattern,
    /// <summary>Non-bool return + out params → tuple return</summary>
    TuplePattern
}

/// <summary>
/// Describes a flooded method that has out parameters and needs special transformation.
/// </summary>
public record OutParameterMetadata : IGraphMetadata<OutParameterMetadata>
{
    public static readonly OutParameterMetadata None = new()
    {
        OriginalReturnType = string.Empty,
        TransformKind = OutParameterTransformKind.None,
        OutParameterIndices = [],
        OutParameterTypes = [],
        OutParameterNames = [],
        NewAsyncReturnType = string.Empty
    };

    public required string OriginalReturnType { get; init; }
    public required OutParameterTransformKind TransformKind { get; init; }

    /// <summary>Indices of parameters that are 'out' (into Method.Parameters).</summary>
    public required List<int> OutParameterIndices { get; init; }

    /// <summary>The types of the out parameters (without the "out" keyword).</summary>
    public required List<string> OutParameterTypes { get; init; }

    /// <summary>The names of the out parameters.</summary>
    public required List<string> OutParameterNames { get; init; }

    /// <summary>The computed new async return type (e.g. "Task&lt;AsyncOutResult&lt;Foo&gt;&gt;").</summary>
    public required string NewAsyncReturnType { get; init; }

    private const string ListSeparator = "|";

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        ["OriginalReturnType"] = OriginalReturnType,
        ["TransformKind"] = TransformKind.ToString(),
        ["OutParameterIndices"] = string.Join(ListSeparator, OutParameterIndices),
        ["OutParameterTypes"] = string.Join(ListSeparator, OutParameterTypes),
        ["OutParameterNames"] = string.Join(ListSeparator, OutParameterNames),
        ["NewAsyncReturnType"] = NewAsyncReturnType,
    };

    public static OutParameterMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary)
    {
        var originalReturnType = dictionary["OriginalReturnType"];
        var transformKind = System.Enum.Parse<OutParameterTransformKind>(dictionary["TransformKind"]);
        var outParameterIndices = dictionary.TryGetValue("OutParameterIndices", out var indices) && indices != ""
            ? new List<int>(System.Array.ConvertAll(indices.Split(ListSeparator), int.Parse))
            : new List<int>();
        var outParameterTypes = dictionary.TryGetValue("OutParameterTypes", out var types) && types != ""
            ? new List<string>(types.Split(ListSeparator))
            : new List<string>();
        var outParameterNames = dictionary.TryGetValue("OutParameterNames", out var names) && names != ""
            ? new List<string>(names.Split(ListSeparator))
            : new List<string>();
        var newAsyncReturnType = dictionary["NewAsyncReturnType"];

        return new OutParameterMetadata
        {
            OriginalReturnType = originalReturnType,
            TransformKind = transformKind,
            OutParameterIndices = outParameterIndices,
            OutParameterTypes = outParameterTypes,
            OutParameterNames = outParameterNames,
            NewAsyncReturnType = newAsyncReturnType,
        };
    }
}