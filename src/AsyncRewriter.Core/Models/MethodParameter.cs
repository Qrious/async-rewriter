using System;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a single method parameter with its type, name, and optional ref kind.
/// Serializes to and parses from a flat string of the form "[refkind ]type name"
/// (e.g. "int x", "out string value", "Func&lt;Task&lt;int&gt;&gt; func").
/// </summary>
public record MethodParameter
{
    /// <summary>
    /// The fully-qualified or minimally-qualified type name of the parameter.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// The parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The ref kind: "out", "ref", "in", or null for none.
    /// </summary>
    public string? RefKind { get; init; }

    /// <summary>
    /// Keywords like "params" or "this" that may appear before the type.
    /// </summary>
    public string? Keywords { get; init; }

    /// <summary>
    /// Returns the flat string representation: "[refkind ]type name".
    /// </summary>
    public override string ToString()
    {
        var prefix = Keywords != null ? $"{Keywords} " : string.Empty;
        return RefKind is not null
            ? $"{prefix}{RefKind} {Type} {Name}"
            : $"{prefix}{Type} {Name}";
    }

    /// <summary>
    /// Parses a flat parameter string of the form "[refkind ]type name".
    /// The ref-kind prefix is one of "out", "ref", or "in" followed by a space.
    /// The name is the last whitespace-delimited token; everything before it is the type.
    /// </summary>
    public static MethodParameter Parse(string flat)
    {
        if (string.IsNullOrWhiteSpace(flat))
        {
            throw new ArgumentException("Parameter string must not be empty.", nameof(flat));
        }

        string? refKind = null;
        var remainder = flat.Trim();

        // Strip optional leading ref-kind token
        foreach (var keyword in new[] { "out ", "ref ", "in " })
        {
            if (remainder.StartsWith(keyword, StringComparison.Ordinal))
            {
                refKind = keyword.Trim();
                remainder = remainder.Substring(keyword.Length);
                break;
            }
        }

        // The name is the last space-delimited token; everything before it is the type
        var lastSpace = remainder.LastIndexOf(' ');
        if (lastSpace < 0)
        {
            // No space: treat the whole string as the type with an empty name
            return new MethodParameter { Type = remainder, Name = string.Empty, RefKind = refKind };
        }

        return new MethodParameter
        {
            Type = remainder.Substring(0, lastSpace),
            Name = remainder.Substring(lastSpace + 1),
            RefKind = refKind,
        };
    }
}
