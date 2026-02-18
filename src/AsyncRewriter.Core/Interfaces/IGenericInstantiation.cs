using System;

namespace AsyncRewriter.Core.Interfaces;

public interface IGenericInstantiation : IEquatable<IGenericInstantiation?>
{
    /// <summary>
    /// The unique identifier of the call graph that contains the generic instantiation.
    /// </summary>
    string CallGraphId { get; }

    /// <summary>
    /// The unique identifier of the instantiated method.
    /// </summary>
    string InstantiatedMethodId { get; }

    /// <summary>
    /// The unique identifier of the generic method definition that is being instantiated.
    /// </summary>
    string GenericMethodId { get; }
}