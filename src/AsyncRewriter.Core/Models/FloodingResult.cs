using System;
using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public enum FloodReason
{
    Root,
    Caller,
    InterfaceImpl,
    InterfaceMethod,
    Override,
    BaseMethod,
    GenericInstantiation
}

public record FloodedMethodInfo(string MethodId, string? FloodedById, int Depth, FloodReason Reason);

public class FloodingResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public Dictionary<string, FloodedMethodInfo> FloodedMethods { get; init; } = new();
}

public class FloodingMethodMetadata : IGraphMetadata<FloodingMethodMetadata>
{
    public string? FloodedById { get; init; }
    public int Depth { get; init; }
    public FloodReason Reason { get; init; }
    public required string OriginalReturnType { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        ["FloodedById"] = FloodedById ?? "",
        ["Depth"] = Depth.ToString(),
        ["Reason"] = Reason.ToString(),
        ["OriginalReturnType"] = OriginalReturnType,
    };

    public static FloodingMethodMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary) => new()
    {
        FloodedById = dictionary.TryGetValue("FloodedById", out var fby) && fby != "" ? fby : null,
        Depth = dictionary.TryGetValue("Depth", out var depth) ? int.Parse(depth) : 0,
        Reason = dictionary.TryGetValue("Reason", out var reason) ? Enum.Parse<FloodReason>(reason) : FloodReason.Root,
        OriginalReturnType = dictionary.TryGetValue("OriginalReturnType", out var orig) ? orig : "",
    };
}
