using System;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodCall : IEquatable<IMethodCall?>
{
    /// <summary>
    /// The id of the call graph this method call belongs to. This is used to group method calls together that belong to the same call graph.
    /// </summary>
    public string CallGraphId { get; }

    /// <summary>
    /// The id of the method call. This is used to uniquely identify a method call in the call graph.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// The id of the caller method. This is used to identify the caller method in the call graph.
    /// </summary>
    public string CallerId { get; }

    /// <summary>
    /// The id of the callee method. This is used to identify the callee method in the call graph.
    /// </summary>
    public string CalleeId { get; }

    /// <summary>
    /// The line number of the method call.
    /// </summary>
    public int LineNumber { get; }

    /// <summary>
    /// The file path of the method call.
    /// </summary>
    public string FilePath { get; }
}