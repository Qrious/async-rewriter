using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Server.DTOs;

namespace AsyncRewriter.Server.Models;

public enum JobType
{
    Analysis,
    SyncWrapperAnalysis,
    Transformation
}

public class AnalysisJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public JobType JobType { get; set; } = JobType.Analysis;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int ProgressPercentage { get; set; } = 0;
    public string? CurrentStep { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public string ProjectPath { get; set; } = string.Empty;
    public string? CallGraphId { get; set; }
    public bool ApplyChanges { get; set; }
    public List<string> ExternalSyncWrapperMethods { get; set; } = new();
    public Dictionary<string, string> InterfaceMappings { get; set; } = new();
    public int? MethodCount { get; set; }
    public int? MethodsProcessed { get; set; }
    public int? FloodedMethodCount { get; set; }
    public int? SyncWrapperCount { get; set; }
    public string? CurrentFile { get; set; }
    public string? CurrentMethod { get; set; }
    public int? TransformedFileCount { get; set; }
    public int? TotalFileCount { get; set; }
    public string? PendingWorkSummary { get; set; }
    public CallGraph? CallGraph { get; set; }
    public List<SyncWrapperMethod>? SyncWrappers { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();

    public JobStatusResponse ToStatusResponse()
    {
        int? methodsRemaining = null;
        if (MethodCount.HasValue && MethodsProcessed.HasValue)
        {
            methodsRemaining = Math.Max(0, MethodCount.Value - MethodsProcessed.Value);
        }

        object? result = null;
        if (Status == JobStatus.Completed)
        {
            result = new AnalysisJobResultSummary
            {
                CallGraphId = CallGraphId,
                MethodCount = MethodCount,
                FloodedMethodCount = FloodedMethodCount,
                SyncWrapperCount = SyncWrapperCount
            };
        }

        return new JobStatusResponse
        {
            JobId = JobId,
            Status = Status,
            ProgressPercentage = ProgressPercentage,
            CurrentStep = CurrentStep,
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            ErrorMessage = ErrorMessage,
            CallGraphId = CallGraphId,
            MethodCount = MethodCount,
            MethodsProcessed = MethodsProcessed,
            MethodsRemaining = methodsRemaining,
            FloodedMethodCount = FloodedMethodCount,
            SyncWrapperCount = SyncWrapperCount,
            CurrentFile = CurrentFile,
            CurrentMethod = CurrentMethod,
            TransformedFileCount = TransformedFileCount,
            TotalFileCount = TotalFileCount,
            SyncWrappers = SyncWrappers?.Select(wrapper => new SyncWrapperSummary
            {
                MethodId = wrapper.MethodId,
                ContainingType = wrapper.ContainingType,
                Signature = wrapper.Signature,
                FilePath = wrapper.FilePath,
                StartLine = wrapper.StartLine,
                PatternDescription = wrapper.PatternDescription
            }).ToList(),
            PendingWorkSummary = PendingWorkSummary,
            Result = result
        };
    }
}

public class AnalysisJobResultSummary
{
    public string? CallGraphId { get; set; }
    public int? MethodCount { get; set; }
    public int? FloodedMethodCount { get; set; }
    public int? SyncWrapperCount { get; set; }
}
