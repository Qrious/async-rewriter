using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface IEntityFrameworkSyncCallExtractor
{
    ICallGraphWithMetadata<EntityFrameworkMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> Extract(ICallGraph callGraph);
}
