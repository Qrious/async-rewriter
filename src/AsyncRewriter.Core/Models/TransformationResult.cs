using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Result of the async transformation process
/// </summary>
public class TransformationResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<FileTransformation> ModifiedFiles { get; init; } = new();
    public int TotalMethodsTransformed { get; set; }
    public int TotalCallSitesTransformed { get; set; }
    public CallGraph? CallGraph { get; init; }
}

/// <summary>
/// Represents a transformed file
/// </summary>
public class FileTransformation
{
    public string FilePath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public string TransformedContent { get; init; } = string.Empty;
    public List<MethodTransformation> MethodTransformations { get; init; } = new();
}

/// <summary>
/// Represents a single method transformation
/// </summary>
public class MethodTransformation
{
    public string MethodName { get; init; } = string.Empty;
    public string MethodSignature { get; init; } = string.Empty;
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public string OriginalReturnType { get; init; } = string.Empty;
    public string NewReturnType { get; init; } = string.Empty;

    public required bool IsReturnTypeParameter { get; init; }
    public List<int> AwaitAddedAtLines { get; init; } = new();
}
