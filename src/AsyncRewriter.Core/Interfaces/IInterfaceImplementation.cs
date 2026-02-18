using System;

namespace AsyncRewriter.Core.Interfaces;

public interface IInterfaceImplementation : IEquatable<IInterfaceImplementation?>
{
    /// <summary>
    /// The id of the call graph this interface implementation belongs to.
    /// </summary>
    public string CallGraphId { get; init; }

    /// <summary>
    /// Id of the method that implements the interface method.
    /// </summary>
    public string ImplementingMethodId { get; init; }

    /// <summary>
    /// Id of the interface method that is implemented.
    /// </summary>
    public string InterfaceMethodId { get; init; }
}