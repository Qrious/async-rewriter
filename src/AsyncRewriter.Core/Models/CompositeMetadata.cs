using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Combines two metadata types into a single <see cref="IGraphMetadata{T}"/> that can be used
/// where a single metadata type is required.  Each component is accessed via the typed
/// <see cref="First"/> and <see cref="Second"/> properties.
/// </summary>
public record CompositeMetadata<T1, T2> : IGraphMetadata<CompositeMetadata<T1, T2>>
    where T1 : IGraphMetadata<T1>
    where T2 : IGraphMetadata<T2>
{
    public required T1 First { get; init; }
    public required T2 Second { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in First.ToDictionary())
            result["0:" + k] = v;
        foreach (var (k, v) in Second.ToDictionary())
            result["1:" + k] = v;
        return result;
    }

    public static CompositeMetadata<T1, T2> FromDictionary(IReadOnlyDictionary<string, string> dictionary)
    {
        var d1 = dictionary
            .Where(kv => kv.Key.StartsWith("0:"))
            .ToDictionary(kv => kv.Key[2..], kv => kv.Value);
        var d2 = dictionary
            .Where(kv => kv.Key.StartsWith("1:"))
            .ToDictionary(kv => kv.Key[2..], kv => kv.Value);
        return new CompositeMetadata<T1, T2>
        {
            First = T1.FromDictionary(d1),
            Second = T2.FromDictionary(d2)
        };
    }
}

/// <summary>
/// Combines three metadata types into a single <see cref="IGraphMetadata{T}"/>.
/// Components are accessed via the typed <see cref="First"/>, <see cref="Second"/>,
/// and <see cref="Third"/> properties.
/// </summary>
public record CompositeMetadata<T1, T2, T3> : IGraphMetadata<CompositeMetadata<T1, T2, T3>>
    where T1 : IGraphMetadata<T1>
    where T2 : IGraphMetadata<T2>
    where T3 : IGraphMetadata<T3>
{
    public required T1 First { get; init; }
    public required T2 Second { get; init; }
    public required T3 Third { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>();
        foreach (var (k, v) in First.ToDictionary())
            result["0:" + k] = v;
        foreach (var (k, v) in Second.ToDictionary())
            result["1:" + k] = v;
        foreach (var (k, v) in Third.ToDictionary())
            result["2:" + k] = v;
        return result;
    }

    public static CompositeMetadata<T1, T2, T3> FromDictionary(IReadOnlyDictionary<string, string> dictionary)
    {
        var d1 = dictionary
            .Where(kv => kv.Key.StartsWith("0:"))
            .ToDictionary(kv => kv.Key[2..], kv => kv.Value);
        var d2 = dictionary
            .Where(kv => kv.Key.StartsWith("1:"))
            .ToDictionary(kv => kv.Key[2..], kv => kv.Value);
        var d3 = dictionary
            .Where(kv => kv.Key.StartsWith("2:"))
            .ToDictionary(kv => kv.Key[2..], kv => kv.Value);
        return new CompositeMetadata<T1, T2, T3>
        {
            First = T1.FromDictionary(d1),
            Second = T2.FromDictionary(d2),
            Third = T3.FromDictionary(d3)
        };
    }
}
