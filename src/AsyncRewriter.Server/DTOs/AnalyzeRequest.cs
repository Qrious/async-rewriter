using System;
using System.Collections.Generic;

namespace AsyncRewriter.Server.DTOs;

public class AnalyzeProjectRequest
{
    public string ProjectPath { get; set; } = string.Empty;
}

public class AnalyzeSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public string FileName { get; set; } = "source.cs";
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
}

public class TransformSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public List<string> MethodsToTransform { get; set; } = new();
}

public class AnalysisJobResponse
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
    public string? PendingWorkSummary { get; set; }
    public object? Result { get; set; }
}

public enum JobStatus
{
    Queued,
    Processing,
    Completed,
    Failed,
    Cancelled
}
