using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface ICallGraphWithMetadata<TMethodMetadata, TCallMetadata> : ICallGraph
    where TMethodMetadata : IGraphMetadata<TMethodMetadata>
    where TCallMetadata : IGraphMetadata<TCallMetadata>
{
    public IReadOnlyDictionary<string, TMethodMetadata> MethodMetadata { get; }

    public IReadOnlyDictionary<string, TCallMetadata> CallMetadata { get; }

    public TMethodMetadata GetMethodMetadata(string methodId);

    public bool TryGetMethodMetadata(string methodId, out TMethodMetadata? metadata);

    public TCallMetadata GetCallMetadata(string callId);

    public bool TryGetCallMetadata(string callId, out TCallMetadata? metadata);
}