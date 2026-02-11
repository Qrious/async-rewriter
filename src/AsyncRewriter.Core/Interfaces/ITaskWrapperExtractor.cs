using System.Collections.Generic;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

public interface ITaskWrapperExtractor
{
    List<SyncWrapperMethod> Extract(CallGraph callGraph);
}
