using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Server.DTOs;
using AsyncRewriter.Server.Models;
using AsyncRewriter.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AsyncTransformationController : ControllerBase
{
    private readonly ICallGraphAnalyzer _callGraphAnalyzer;
    private readonly ICallGraphRepository _callGraphRepository;
    private readonly IAsyncFloodingAnalyzer _floodingAnalyzer;
    private readonly IAsyncTransformer _asyncTransformer;
    private readonly IJobService _jobService;
    private readonly ILogger<AsyncTransformationController> _logger;

    public AsyncTransformationController(
        ICallGraphAnalyzer callGraphAnalyzer,
        ICallGraphRepository callGraphRepository,
        IAsyncFloodingAnalyzer floodingAnalyzer,
        IAsyncTransformer asyncTransformer,
        IJobService jobService,
        ILogger<AsyncTransformationController> logger)
    {
        _callGraphAnalyzer = callGraphAnalyzer;
        _callGraphRepository = callGraphRepository;
        _floodingAnalyzer = floodingAnalyzer;
        _asyncTransformer = asyncTransformer;
        _jobService = jobService;
        _logger = logger;
    }

    /// <summary>
    /// Starts an async analysis job for a C# project (returns immediately with job ID)
    /// </summary>
    [HttpPost("analyze/project/async")]
    public ActionResult<AnalysisJobResponse> StartAnalysisJob([FromBody] AnalyzeProjectRequest request)
    {
        try
        {
            _logger.LogInformation("Starting async analysis job for project: {ProjectPath}", request.ProjectPath);

            var jobId = _jobService.CreateJob(request.ProjectPath, JobType.Analysis);

            return Ok(new AnalysisJobResponse
            {
                JobId = jobId,
                Status = JobStatus.Queued,
                Message = "Analysis job has been queued and will be processed in the background"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start analysis job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the status and progress of an analysis job
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    public ActionResult<JobStatusResponse> GetJobStatus(string jobId)
    {
        try
        {
            var job = _jobService.GetJob(jobId);

            if (job == null || job.JobType == JobType.Transformation)
                return NotFound(new { error = "Job not found" });

            return Ok(job.ToStatusResponse());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get job status");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancels a running or queued analysis job
    /// </summary>
    [HttpPost("jobs/{jobId}/cancel")]
    public ActionResult CancelJob(string jobId)
    {
        try
        {
            var cancelled = _jobService.CancelJob(jobId);

            if (!cancelled)
                return BadRequest(new { error = "Job cannot be cancelled (not found or already completed)" });

            return Ok(new { message = "Job cancelled successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Analyzes a C# project and builds a call graph (synchronous - may timeout on large projects)
    /// </summary>
    [HttpPost("analyze/project")]
    public async Task<ActionResult<CallGraph>> AnalyzeProject(
        [FromBody] AnalyzeProjectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Analyzing project: {ProjectPath}", request.ProjectPath);

            var callGraph = await _callGraphAnalyzer.AnalyzeProjectAsync(
                request.ProjectPath,
                cancellationToken);

            // Store in Neo4j
            await _callGraphRepository.StoreCallGraphAsync(callGraph, cancellationToken);

            _logger.LogInformation(
                "Analysis complete. Found {MethodCount} methods and {CallCount} calls",
                callGraph.Methods.Count,
                callGraph.Calls.Count);

            return Ok(callGraph);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze project");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Analyzes C# source code and builds a call graph
    /// </summary>
    [HttpPost("analyze/source")]
    public async Task<ActionResult<CallGraph>> AnalyzeSource(
        [FromBody] AnalyzeSourceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Analyzing source code");

            var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(
                request.SourceCode,
                request.FileName,
                cancellationToken);

            // Store in Neo4j
            await _callGraphRepository.StoreCallGraphAsync(callGraph, cancellationToken);

            return Ok(callGraph);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze source");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets a call graph by ID
    /// </summary>
    [HttpGet("callgraph/{id}")]
    public async Task<ActionResult<CallGraph>> GetCallGraph(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var callGraph = await _callGraphRepository.GetCallGraphAsync(id, cancellationToken);

            if (callGraph == null)
                return NotFound(new { error = "Call graph not found" });

            return Ok(callGraph);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get call graph");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Analyzes async flooding to determine which methods need to be async
    /// </summary>
    [HttpPost("analyze/flooding")]
    public async Task<ActionResult<CallGraph>> AnalyzeFlooding(
        [FromBody] AnalyzeFloodingRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Analyzing async flooding for call graph: {CallGraphId}", request.CallGraphId);

            // Get the call graph
            var callGraph = await _callGraphRepository.GetCallGraphAsync(
                request.CallGraphId,
                cancellationToken);

            if (callGraph == null)
                return NotFound(new { error = "Call graph not found" });

            // Analyze flooding
            var rootMethodIds = new HashSet<string>(request.RootMethodIds);
            var updatedCallGraph = await _floodingAnalyzer.AnalyzeFloodingAsync(
                callGraph,
                rootMethodIds,
                cancellationToken);

            // Update in Neo4j
            await _callGraphRepository.StoreCallGraphAsync(updatedCallGraph, cancellationToken);

            _logger.LogInformation(
                "Flooding analysis complete. {FloodedCount} methods need async transformation",
                updatedCallGraph.FloodedMethods.Count);

            return Ok(updatedCallGraph);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze flooding");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets transformation info for methods that need to be async
    /// </summary>
    [HttpGet("callgraph/{id}/transformations")]
    public async Task<ActionResult<List<AsyncTransformationInfo>>> GetTransformations(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var callGraph = await _callGraphRepository.GetCallGraphAsync(id, cancellationToken);

            if (callGraph == null)
                return NotFound(new { error = "Call graph not found" });

            var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(
                callGraph,
                cancellationToken);

            return Ok(transformations);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get transformations");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Transforms a project from sync to async (async job)
    /// </summary>
    [HttpPost("transform/project")]
    public ActionResult<TransformationJobResponse> TransformProject([FromBody] TransformRequest request)
    {
        try
        {
            _logger.LogInformation("Queueing transformation for project: {ProjectPath}", request.ProjectPath);

            var jobId = _jobService.CreateJob(
                request.ProjectPath,
                JobType.Transformation,
                request.CallGraphId,
                request.ApplyChanges);

            return Ok(new TransformationJobResponse
            {
                JobId = jobId,
                Status = JobStatus.Queued,
                Message = "Transformation job has been queued and will be processed in the background"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue transformation job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the status and progress of a transformation job
    /// </summary>
    [HttpGet("transform/project/{jobId}/status")]
    public ActionResult<JobStatusResponse> GetTransformationJobStatus(string jobId)
    {
        try
        {
            var job = _jobService.GetJob(jobId);

            if (job == null || job.JobType != JobType.Transformation)
                return NotFound(new { error = "Transformation job not found" });

            return Ok(job.ToStatusResponse());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get transformation job status");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Transforms source code from sync to async
    /// </summary>
    [HttpPost("transform/source")]
    public async Task<ActionResult<string>> TransformSource(
        [FromBody] TransformSourceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Transforming source code");

            // First analyze the source to get the call graph
            var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(
                request.SourceCode,
                "source.cs",
                cancellationToken);

            // Analyze flooding
            var rootMethodIds = new HashSet<string>(request.MethodsToTransform);
            var updatedCallGraph = await _floodingAnalyzer.AnalyzeFloodingAsync(
                callGraph,
                rootMethodIds,
                cancellationToken);

            // Get transformations
            var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(
                updatedCallGraph,
                cancellationToken);

            // Transform
            var transformedSource = await _asyncTransformer.TransformSourceAsync(
                request.SourceCode,
                transformations,
                null,
                null,
                cancellationToken);

            return Ok(new { transformedSource, callGraph = updatedCallGraph });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transform source");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finds all methods that call a specific method
    /// </summary>
    [HttpGet("callgraph/{id}/callers/{methodId}")]
    public async Task<ActionResult<List<MethodNode>>> FindCallers(
        string id,
        string methodId,
        [FromQuery] int depth = -1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var callers = await _callGraphRepository.FindCallersAsync(methodId, depth, cancellationToken);
            return Ok(callers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find callers");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finds all methods called by a specific method
    /// </summary>
    [HttpGet("callgraph/{id}/callees/{methodId}")]
    public async Task<ActionResult<List<MethodNode>>> FindCallees(
        string id,
        string methodId,
        [FromQuery] int depth = -1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var callees = await _callGraphRepository.FindCalleesAsync(methodId, depth, cancellationToken);
            return Ok(callees);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find callees");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finds sync wrapper methods in a project - methods with Func&lt;Task&gt; or Func&lt;Task&lt;TResult&gt;&gt;
    /// parameters that return void or TResult. These are candidates for async transformation.
    /// </summary>
    [HttpPost("find-sync-wrappers/project")]
    public async Task<ActionResult<List<SyncWrapperMethod>>> FindSyncWrappers(
        [FromBody] AnalyzeProjectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Finding sync wrapper methods in project: {ProjectPath}", request.ProjectPath);

            var syncWrappers = await _callGraphAnalyzer.FindSyncWrapperMethodsAsync(
                request.ProjectPath,
                cancellationToken);

            _logger.LogInformation("Found {Count} sync wrapper methods", syncWrappers.Count);

            return Ok(syncWrappers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find sync wrapper methods");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finds sync wrapper methods in source code
    /// </summary>
    [HttpPost("find-sync-wrappers/source")]
    public async Task<ActionResult<List<SyncWrapperMethod>>> FindSyncWrappersInSource(
        [FromBody] AnalyzeSourceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Finding sync wrapper methods in source code");

            var syncWrappers = await _callGraphAnalyzer.FindSyncWrapperMethodsInSourceAsync(
                request.SourceCode,
                request.FileName,
                cancellationToken);

            _logger.LogInformation("Found {Count} sync wrapper methods", syncWrappers.Count);

            return Ok(syncWrappers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find sync wrapper methods");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Starts an async analysis job for sync wrapper-based flooding (returns immediately with job ID)
    /// </summary>
    [HttpPost("analyze/from-sync-wrappers/async")]
    public ActionResult<AnalysisJobResponse> StartSyncWrapperAnalysisJob([FromBody] AnalyzeProjectRequest request)
    {
        try
        {
            _logger.LogInformation("Starting async sync wrapper analysis job for project: {ProjectPath}", request.ProjectPath);

            var jobId = _jobService.CreateJob(request.ProjectPath, JobType.SyncWrapperAnalysis);

            return Ok(new AnalysisJobResponse
            {
                JobId = jobId,
                Status = JobStatus.Queued,
                Message = "Sync wrapper analysis job has been queued and will be processed in the background"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start sync wrapper analysis job");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Finds sync wrapper methods and automatically runs async flooding analysis from them
    /// </summary>
    [HttpPost("analyze/from-sync-wrappers")]
    public async Task<ActionResult<SyncWrapperAnalysisResult>> AnalyzeFromSyncWrappers(
        [FromBody] AnalyzeProjectRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Analyzing project from sync wrappers: {ProjectPath}", request.ProjectPath);

            // Step 1: Find sync wrapper methods
            var syncWrappers = await _callGraphAnalyzer.FindSyncWrapperMethodsAsync(
                request.ProjectPath,
                cancellationToken);

            if (syncWrappers.Count == 0)
            {
                return Ok(new SyncWrapperAnalysisResult
                {
                    SyncWrappers = syncWrappers,
                    CallGraph = null,
                    Message = "No sync wrapper methods found in the project"
                });
            }

            // Step 2: Build call graph
            var callGraph = await _callGraphAnalyzer.AnalyzeProjectAsync(
                request.ProjectPath,
                cancellationToken);

            // Step 3: Use sync wrapper method IDs as root methods for flooding
            var rootMethodIds = new HashSet<string>(syncWrappers.Select(sw => sw.MethodId));

            // Track sync wrapper methods for unwrapping during transformation
            callGraph.SyncWrapperMethods = new HashSet<string>(rootMethodIds);

            // Step 4: Analyze flooding
            var updatedCallGraph = await _floodingAnalyzer.AnalyzeFloodingAsync(
                callGraph,
                rootMethodIds,
                cancellationToken);

            // Store in Neo4j
            await _callGraphRepository.StoreCallGraphAsync(updatedCallGraph, cancellationToken);

            _logger.LogInformation(
                "Analysis complete. Found {WrapperCount} sync wrappers, {FloodedCount} methods need async transformation",
                syncWrappers.Count,
                updatedCallGraph.FloodedMethods.Count);

            return Ok(new SyncWrapperAnalysisResult
            {
                SyncWrappers = syncWrappers,
                CallGraph = updatedCallGraph,
                Message = $"Found {syncWrappers.Count} sync wrapper(s), {updatedCallGraph.FloodedMethods.Count} method(s) need async transformation"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze from sync wrappers");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

/// <summary>
/// Result of analyzing a project starting from sync wrapper methods
/// </summary>
public class SyncWrapperAnalysisResult
{
    public List<SyncWrapperMethod> SyncWrappers { get; set; } = new();
    public CallGraph? CallGraph { get; set; }
    public string Message { get; set; } = string.Empty;
}
