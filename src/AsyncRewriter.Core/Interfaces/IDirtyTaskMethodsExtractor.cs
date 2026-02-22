using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface IDirtyTaskMethodsExtractor
{
    ICallGraphWithMetadata<SyncWrapperMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph);
}
