using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface IGraphMetadata<T> where T : IGraphMetadata<T>
{
    IReadOnlyDictionary<string, string> ToDictionary();

    static abstract T FromDictionary(IReadOnlyDictionary<string, string> dictionary);
}