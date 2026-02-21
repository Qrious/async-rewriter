using System.Collections.Generic;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface IDirtyTaskMethodsExtractor
{
    List<DirtyTaskMethodInfo> Extract(ICallGraph callGraph);
}
