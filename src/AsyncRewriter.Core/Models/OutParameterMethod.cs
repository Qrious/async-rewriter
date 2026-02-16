using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Classifies how an out-parameter method should be transformed for async.
/// </summary>
public enum OutParameterTransformKind
{
    /// <summary>Bool return + out params → AsyncOutResult&lt;T&gt;</summary>
    BoolTryPattern,
    /// <summary>Non-bool return + out params → tuple return</summary>
    TuplePattern
}

/// <summary>
/// Describes a flooded method that has out parameters and needs special transformation.
/// </summary>
public record OutParameterMethod
{
    public required string MethodId { get; init; }
    public required MethodNode Method { get; init; }
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
}
