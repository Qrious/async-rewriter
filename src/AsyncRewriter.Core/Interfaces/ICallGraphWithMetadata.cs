using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface ICallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata> : ICallGraph
    where TMethodMetadata : IGraphMetadata<TMethodMetadata>
    where TCallMetadata : IGraphMetadata<TCallMetadata>
    where TOverridesMetadata : IGraphMetadata<TOverridesMetadata>
    where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
{
    public IReadOnlyDictionary<string, TMethodMetadata> MethodMetadata { get; }

    public IReadOnlyDictionary<string, TCallMetadata> CallMetadata { get; }

    public IReadOnlyDictionary<string, TOverridesMetadata> OverridesMetadata { get; }

    public IReadOnlyDictionary<string, TImplementsMetadata> ImplementsMetadata { get; }

    public TMethodMetadata GetMethodMetadata(string methodId);

    public bool TryGetMethodMetadata(string methodId, out TMethodMetadata? metadata);

    public TCallMetadata GetCallMetadata(string callId);

    public bool TryGetCallMetadata(string callId, out TCallMetadata? metadata);

    public TOverridesMetadata GetOverridesMetadata(string overrideId);

    public bool TryGetOverridesMetadata(string overrideId, out TOverridesMetadata? metadata);

    public TImplementsMetadata GetImplementsMetadata(string implementsId);

    public bool TryGetImplementsMetadata(string implementsId, out TImplementsMetadata? metadata);
}