using System;
using System.Collections.Generic;

namespace AsyncRewriter.Server.DTOs;

public class AnalyzeProjectRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public List<string> ExternalSyncWrapperMethods { get; set; } = new();
}

public class AnalyzeSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public string FileName { get; set; } = "source.cs";
    public List<string> ExternalSyncWrapperMethods { get; set; } = new();
}

public class AnalyzeFloodingRequest
{
    public string CallGraphId { get; set; } = string.Empty;
    public List<string> RootMethodIds { get; set; } = new();
}

public class TransformRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string CallGraphId { get; set; } = string.Empty;
    public bool ApplyChanges { get; set; } = false;
    public List<string> ExternalSyncWrapperMethods { get; set; } = new();
}

public class TransformSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public List<string> MethodsToTransform { get; set; } = new();
    public List<string> ExternalSyncWrapperMethods { get; set; } = new();
}

public class AnalysisJobResponse
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TransformationJobResponse
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class JobStatusResponse
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public int ProgressPercentage { get; set; }
    public string? CurrentStep { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CallGraphId { get; set; }
    public int? MethodCount { get; set; }
    public int? MethodsProcessed { get; set; }
    public int? MethodsRemaining { get; set; }
    public int? FloodedMethodCount { get; set; }
    public int? SyncWrapperCount { get; set; }
    public string? CurrentFile { get; set; }
    public string? CurrentMethod { get; set; }
    public int? TransformedFileCount { get; set; }
    public int? TotalFileCount { get; set; }
    public List<SyncWrapperSummary>? SyncWrappers { get; set; }
    public string? PendingWorkSummary { get; set; }
    public object? Result { get; set; }
}

public class SyncWrapperSummary
{
    public string MethodId { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public string PatternDescription { get; set; } = string.Empty;
}

public enum JobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Explains why a method requires async transformation
/// </summary>
public class AsyncExplanationResponse
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public bool RequiresAsync { get; set; }
    public string? Reason { get; set; }
    public List<AsyncExplanationStep> CallChain { get; set; } = new();
    public SyncWrapperInfo? RootSyncWrapper { get; set; }
    public MethodReference? RootAsyncMethod { get; set; }
    public List<InterfacePropagationInfo> InterfacePropagation { get; set; } = new();
}

public class MethodReference
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
}

public class InterfacePropagationInfo
{
    public MethodReference InterfaceMethod { get; set; } = new();
    public List<MethodReference> Implementations { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A step in the call chain explaining async propagation
/// </summary>
public class AsyncExplanationStep
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string Relationship { get; set; } = string.Empty; // "calls" or "is called by"
}

/// <summary>
/// Information about the sync wrapper that caused the async propagation
/// </summary>
public class SyncWrapperInfo
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? PatternDescription { get; set; }
}

/// <summary>
/// Result of searching for methods in a call graph
/// </summary>
public class MethodSearchResult
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int StartLine { get; set; }
    public bool RequiresAsyncTransformation { get; set; }
    public bool IsAsync { get; set; }
    public bool IsSyncWrapper { get; set; }
}
