using System;
using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface IInterfaceImplementation : IEquatable<IInterfaceImplementation?>, IIdentifiable
{
    /// <summary>
    /// The id of the call graph this interface implementation belongs to.
    /// </summary>
    public string CallGraphId { get; }

    /// <summary>
    /// Id of the method that implements the interface method.
    /// </summary>
    public string ImplementingMethodId { get;  }

    /// <summary>
    /// Id of the interface method that is implemented.
    /// </summary>
    public string InterfaceMethodId { get;  }

    public IDictionary<string, string> ToDictionary();
}