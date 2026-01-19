using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Server.Models;
using AsyncRewriter.Server.DTOs;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Server.Services;

public interface IJobService
{
    string CreateJob(
        string projectPath,
        JobType jobType = JobType.Analysis,
        string? callGraphId = null,
        bool applyChanges = false,
        List<string>? externalSyncWrapperMethods = null);
    AnalysisJob? GetJob(string jobId);
    void UpdateJob(string jobId, Action<AnalysisJob> updateAction);
    IEnumerable<AnalysisJob> GetQueuedJobs();
    bool CancelJob(string jobId);
}

public class JobService : IJobService
{
    private readonly ConcurrentDictionary<string, AnalysisJob> _jobs = new();
    private readonly ConcurrentQueue<string> _jobQueue = new();
    private readonly ILogger<JobService> _logger;

    public JobService(ILogger<JobService> logger)
    {
        _logger = logger;
    }

    public string CreateJob(
        string projectPath,
        JobType jobType = JobType.Analysis,
        string? callGraphId = null,
        bool applyChanges = false,
        List<string>? externalSyncWrapperMethods = null)
    {
        var job = new AnalysisJob
        {
            ProjectPath = projectPath,
            JobType = jobType,
            Status = JobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            CallGraphId = callGraphId,
            ApplyChanges = applyChanges,
            ExternalSyncWrapperMethods = externalSyncWrapperMethods ?? new List<string>()
        };

        _jobs[job.JobId] = job;
        _jobQueue.Enqueue(job.JobId);

        _logger.LogInformation("Created job {JobId} for project {ProjectPath}", job.JobId, projectPath);

        return job.JobId;
    }

    public AnalysisJob? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    public void UpdateJob(string jobId, Action<AnalysisJob> updateAction)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            updateAction(job);
            _logger.LogDebug("Updated job {JobId}: Status={Status}, Progress={Progress}%",
                jobId, job.Status, job.ProgressPercentage);
        }
    }

    public IEnumerable<AnalysisJob> GetQueuedJobs()
    {
        var queuedJobIds = new List<string>();

        while (_jobQueue.TryDequeue(out var jobId))
        {
            queuedJobIds.Add(jobId);
        }

        return queuedJobIds
            .Select(id => _jobs.TryGetValue(id, out var job) ? job : null)
            .Where(job => job != null)
            .Cast<AnalysisJob>();
    }

    public bool CancelJob(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Status == JobStatus.Queued || job.Status == JobStatus.Processing)
            {
                job.CancellationTokenSource.Cancel();
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation("Cancelled job {JobId}", jobId);
                return true;
            }
        }
        return false;
    }
}
