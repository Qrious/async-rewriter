using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Result of the async transformation process
/// </summary>
public class TransformationResult
{
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<FileTransformation> ModifiedFiles { get; set; } = new();
    public int TotalMethodsTransformed { get; set; }
    public int TotalCallSitesTransformed { get; set; }
    public CallGraph? CallGraph { get; set; }
}

/// <summary>
/// Represents a transformed file
/// </summary>
public class FileTransformation
{
    public string FilePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string TransformedContent { get; set; } = string.Empty;
    public List<MethodTransformation> MethodTransformations { get; set; } = new();
}

/// <summary>
/// Represents a single method transformation
/// </summary>
public class MethodTransformation
{
    public string MethodName { get; set; } = string.Empty;
    public string MethodSignature { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string OriginalReturnType { get; set; } = string.Empty;
    public string NewReturnType { get; set; } = string.Empty;
    public List<int> AwaitAddedAtLines { get; set; } = new();
}
