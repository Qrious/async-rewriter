using System.Collections.Generic;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface IEntityFrameworkSyncCallExtractor
{
    List<DirtyTaskMethodInfo> Extract(ICallGraph callGraph);
}
