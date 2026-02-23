using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

public class MethodTransformInfo
{
    public required string MethodId { get; init; }
    public required string MethodName { get; init; }
    public required string ContainingType { get; init; }
    public required string OriginalReturnType { get; init; }
    public required string NewReturnType { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public List<string>? DebugLines { get; init; }
    public string? NewMethodName { get; init; }
    /// <summary>Out-parameter transformation info. Null if method has no out parameters.</summary>
    public OutParameterTransformInfo? OutParameterInfo { get; init; }
}