using System;
using System.Collections.Generic;

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
