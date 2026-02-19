using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

public class EmptyGraphMetadata : IGraphMetadata<EmptyGraphMetadata>
{
    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>();

    public static EmptyGraphMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary) => new();
}