using System;
using System.Threading;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Server.DTOs;

namespace AsyncRewriter.Server.Models;

public class AnalysisJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public int ProgressPercentage { get; set; } = 0;
    public string? CurrentStep { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public string ProjectPath { get; set; } = string.Empty;
    public CallGraph? CallGraph { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();

    public JobStatusResponse ToStatusResponse()
    {
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
            Result = Status == JobStatus.Completed ? CallGraph : null
        };
    }
}
