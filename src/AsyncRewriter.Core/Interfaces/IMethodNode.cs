using System;
using System.Collections.Generic;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodNode : IEquatable<IMethodNode>, IIdentifiable
{
    /// <summary>
    /// The unique identifier of the call graph this method belongs to.
    /// </summary>
    public string CallGraphId { get; }

    /// <summary>
    /// The name of the method.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The name of the type that contains this method.
    /// </summary>
    public string ContainingType { get; }

    /// <summary>
    /// The namespace that contains this method.
    /// </summary>
    public string ContainingNamespace { get; }

    /// <summary>
    /// The return type of the method.
    /// </summary>
    public string ReturnType { get; }

    /// <summary>
    /// The list of parameter types for this method.
    /// </summary>
    public List<string> Parameters { get; }

    /// <summary>
    /// The file path where this method is defined.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// The starting line number of the method in the source file.
    /// </summary>
    public int StartLine { get; }

    /// <summary>
    /// The ending line number of the method in the source file.
    /// </summary>
    public int EndLine { get; }

    /// <summary>
    /// Indicates whether the return type is a type parameter (generic type parameter).
    /// </summary>
    public bool IsReturnTypeParameter { get; }

    /// <summary>
    /// Converts the method node properties to a dictionary representation.
    /// </summary>
    /// <returns>A dictionary containing the method node's properties as key-value pairs.</returns>
    public IDictionary<string, string> ToDictionary();
}