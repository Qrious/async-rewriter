using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Server.DTOs;
using AsyncRewriter.Server.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Server.Services;

public class AnalysisBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AnalysisBackgroundService> _logger;

    public AnalysisBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AnalysisBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Analysis Background Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueuedJobsAsync(stoppingToken);
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Analysis Background Service is stopping");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in background service");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("Analysis Background Service stopped");
    }

    private async Task ProcessQueuedJobsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var jobService = scope.ServiceProvider.GetRequiredService<IJobService>();
        var callGraphAnalyzer = scope.ServiceProvider.GetRequiredService<ICallGraphAnalyzer>();
        var callGraphRepository = scope.ServiceProvider.GetRequiredService<ICallGraphRepository>();
        var floodingAnalyzer = scope.ServiceProvider.GetRequiredService<IAsyncFloodingAnalyzer>();

        var queuedJobs = jobService.GetQueuedJobs().ToList();

        foreach (var job in queuedJobs)
        {
            if (stoppingToken.IsCancellationRequested || job.CancellationTokenSource.Token.IsCancellationRequested)
            {
                break;
            }

            if (job.JobType == JobType.SyncWrapperAnalysis)
            {
                await ProcessSyncWrapperJobAsync(job, jobService, callGraphAnalyzer, callGraphRepository, floodingAnalyzer, stoppingToken);
            }
            else
            {
                await ProcessJobAsync(job, jobService, callGraphAnalyzer, callGraphRepository, stoppingToken);
            }
        }
    }

    private async Task ProcessJobAsync(
        AnalysisJob job,
        IJobService jobService,
        ICallGraphAnalyzer callGraphAnalyzer,
        ICallGraphRepository callGraphRepository,
        CancellationToken stoppingToken)
    {
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            job.CancellationTokenSource.Token).Token;

        try
        {
            _logger.LogInformation("Processing job {JobId} for project {ProjectPath}", job.JobId, job.ProjectPath);

            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Processing;
                j.StartedAt = DateTime.UtcNow;
                j.CurrentStep = "Starting analysis";
                j.ProgressPercentage = 0;
                j.MethodsProcessed = 0;
                j.PendingWorkSummary = "Preparing analysis";
            });

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Analyzing project structure";
                j.ProgressPercentage = 20;
                j.MethodsProcessed = 0;
                j.PendingWorkSummary = "Scanning project for method declarations";
            });

            await Task.Delay(100, combinedToken);

            var callGraph = await callGraphAnalyzer.AnalyzeProjectAsync(job.ProjectPath, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Building call graph";
                j.ProgressPercentage = 60;
                j.MethodsProcessed = callGraph.Methods.Count;
                j.MethodCount = callGraph.Methods.Count;
                j.PendingWorkSummary = "Resolving call graph edges";
            });

            await Task.Delay(100, combinedToken);

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Saving to database";
                j.ProgressPercentage = 80;
                j.PendingWorkSummary = "Writing call graph to storage";
            });

            await callGraphRepository.StoreCallGraphAsync(callGraph, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Completed;
                j.CompletedAt = DateTime.UtcNow;
                j.CurrentStep = "Analysis complete";
                j.ProgressPercentage = 100;
                j.CallGraphId = callGraph.Id;
                j.MethodsProcessed = callGraph.Methods.Count;
                j.MethodCount = callGraph.Methods.Count;
                j.PendingWorkSummary = "Completed";
                j.CallGraph = callGraph;
            });

            _logger.LogInformation("Job {JobId} completed successfully. CallGraph ID: {CallGraphId}",
                job.JobId, callGraph.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job {JobId} was cancelled", job.JobId);
            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Cancelled;
                j.CompletedAt = DateTime.UtcNow;
                j.ErrorMessage = "Job was cancelled";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed", job.JobId);
            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Failed;
                j.CompletedAt = DateTime.UtcNow;
                j.ErrorMessage = ex.Message;
            });
        }
    }

    private async Task ProcessSyncWrapperJobAsync(
        AnalysisJob job,
        IJobService jobService,
        ICallGraphAnalyzer callGraphAnalyzer,
        ICallGraphRepository callGraphRepository,
        IAsyncFloodingAnalyzer floodingAnalyzer,
        CancellationToken stoppingToken)
    {
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            job.CancellationTokenSource.Token).Token;

        try
        {
            _logger.LogInformation("Processing sync wrapper job {JobId} for project {ProjectPath}", job.JobId, job.ProjectPath);

            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Processing;
                j.StartedAt = DateTime.UtcNow;
                j.CurrentStep = "Finding sync wrapper methods";
                j.ProgressPercentage = 0;
                j.MethodsProcessed = 0;
                j.PendingWorkSummary = "Scanning for sync wrapper patterns";
            });

            combinedToken.ThrowIfCancellationRequested();

            var syncWrappers = await callGraphAnalyzer.FindSyncWrapperMethodsAsync(job.ProjectPath, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            if (syncWrappers.Count == 0)
            {
                jobService.UpdateJob(job.JobId, j =>
                {
                    j.Status = JobStatus.Completed;
                    j.CompletedAt = DateTime.UtcNow;
                    j.CurrentStep = "No sync wrapper methods found";
                    j.ProgressPercentage = 100;
                    j.SyncWrappers = syncWrappers;
                    j.SyncWrapperCount = 0;
                    j.PendingWorkSummary = "Completed";
                });
                return;
            }

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Analyzing project structure";
                j.ProgressPercentage = 30;
                j.SyncWrappers = syncWrappers;
                j.SyncWrapperCount = syncWrappers.Count;
                j.MethodsProcessed = 0;
                j.PendingWorkSummary = "Preparing call graph analysis";
            });

            await Task.Delay(100, combinedToken);

            var callGraph = await callGraphAnalyzer.AnalyzeProjectAsync(job.ProjectPath, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            var rootMethodIds = new HashSet<string>(syncWrappers.Select(wrapper => wrapper.MethodId));
            callGraph.SyncWrapperMethods = new HashSet<string>(rootMethodIds);

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Running flooding analysis";
                j.ProgressPercentage = 70;
                j.MethodCount = callGraph.Methods.Count;
                j.MethodsProcessed = callGraph.Methods.Count;
                j.PendingWorkSummary = "Traversing call graph for async flooding";
            });

            await Task.Delay(100, combinedToken);

            var updatedCallGraph = await floodingAnalyzer.AnalyzeFloodingAsync(
                callGraph,
                rootMethodIds,
                combinedToken);

            await callGraphRepository.StoreCallGraphAsync(updatedCallGraph, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Completed;
                j.CompletedAt = DateTime.UtcNow;
                j.CurrentStep = "Sync wrapper analysis complete";
                j.ProgressPercentage = 100;
                j.CallGraphId = updatedCallGraph.Id;
                j.MethodCount = updatedCallGraph.Methods.Count;
                j.MethodsProcessed = updatedCallGraph.Methods.Count;
                j.FloodedMethodCount = updatedCallGraph.FloodedMethods.Count;
                j.PendingWorkSummary = "Completed";
                j.CallGraph = updatedCallGraph;
            });

            _logger.LogInformation("Sync wrapper job {JobId} completed successfully. CallGraph ID: {CallGraphId}",
                job.JobId, updatedCallGraph.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Sync wrapper job {JobId} was cancelled", job.JobId);
            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Cancelled;
                j.CompletedAt = DateTime.UtcNow;
                j.ErrorMessage = "Job was cancelled";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync wrapper job {JobId} failed", job.JobId);
            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Failed;
                j.CompletedAt = DateTime.UtcNow;
                j.ErrorMessage = ex.Message;
            });
        }
    }
}
