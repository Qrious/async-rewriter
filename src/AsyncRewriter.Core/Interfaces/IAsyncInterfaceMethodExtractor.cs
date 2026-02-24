using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface IAsyncInterfaceMethodExtractor
{
    ICallGraphWithMetadata<ServiceInterfaceMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph);
}
