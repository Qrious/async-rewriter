using System;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodOverride : IEquatable<IMethodOverride?>
{
    /// <summary>
    /// The unique identifier of the call graph.
    /// </summary>
    string CallGraphId { get; init; }

    /// <summary>
    /// The unique identifier of the overriding method.
    /// </summary>
    string OverridingMethodId { get; init; }

    /// <summary>
    /// The unique identifier of the base method being overridden.
    /// </summary>
    string BaseMethodId { get; init; }
}