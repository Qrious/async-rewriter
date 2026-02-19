using System;
using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodOverride : IEquatable<IMethodOverride?>, IIdentifiable
{
    /// <summary>
    /// The unique identifier of the call graph.
    /// </summary>
    public string CallGraphId { get; }

    /// <summary>
    /// The unique identifier of the overriding method.
    /// </summary>
    public string OverridingMethodId { get; }

    /// <summary>
    /// The unique identifier of the base method being overridden.
    /// </summary>
    public string BaseMethodId { get; }

    /// <summary>
    /// Converts the method override to a dictionary representation.
    /// </summary>
    /// <returns></returns>
    IDictionary<string, string> ToDictionary();
}