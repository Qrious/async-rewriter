using System;
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

        var queuedJobs = jobService.GetQueuedJobs().ToList();

        foreach (var job in queuedJobs)
        {
            if (stoppingToken.IsCancellationRequested || job.CancellationTokenSource.Token.IsCancellationRequested)
            {
                break;
            }

            await ProcessJobAsync(job, jobService, callGraphAnalyzer, callGraphRepository, stoppingToken);
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
            });

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Analyzing project structure";
                j.ProgressPercentage = 20;
            });

            await Task.Delay(100, combinedToken);

            var callGraph = await callGraphAnalyzer.AnalyzeProjectAsync(job.ProjectPath, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Building call graph";
                j.ProgressPercentage = 60;
            });

            await Task.Delay(100, combinedToken);

            jobService.UpdateJob(job.JobId, j =>
            {
                j.CurrentStep = "Saving to database";
                j.ProgressPercentage = 80;
            });

            await callGraphRepository.StoreCallGraphAsync(callGraph, combinedToken);

            combinedToken.ThrowIfCancellationRequested();

            jobService.UpdateJob(job.JobId, j =>
            {
                j.Status = JobStatus.Completed;
                j.CompletedAt = DateTime.UtcNow;
                j.CurrentStep = "Analysis complete";
                j.ProgressPercentage = 100;
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
}
