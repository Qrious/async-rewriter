using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Metadata indicating that a method calls one or more Entity Framework synchronous methods
/// that have async overloads. Used to identify flood roots and to annotate the combined
/// call graph for downstream tooling.
/// </summary>
public class EntityFrameworkMethodMetadata : IGraphMetadata<EntityFrameworkMethodMetadata>
{
    public static readonly EntityFrameworkMethodMetadata None = new() { IsEntityFrameworkCaller = false, Reason = null };

    public bool IsEntityFrameworkCaller { get; init; }
    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        ["IsEntityFrameworkCaller"] = IsEntityFrameworkCaller.ToString(),
        ["Reason"] = Reason ?? "",
    };

    public static EntityFrameworkMethodMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary) => new()
    {
        IsEntityFrameworkCaller = dictionary.TryGetValue("IsEntityFrameworkCaller", out var v) && bool.Parse(v),
        Reason = dictionary.TryGetValue("Reason", out var r) && r != "" ? r : null,
    };
}
