using System;
using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodNode : IEquatable<IMethodNode>
{
    string CallGraphId { get; }
    string Id { get; }
    string Name { get;  }
    string ContainingType { get;  }
    string ContainingNamespace { get;  }
    string ReturnType { get;  }
    List<string> Parameters { get; }
    string FilePath { get;  }
    int StartLine { get;  }
    int EndLine { get;  }
    bool IsReturnTypeParameter { get;  }
}