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

            var jobId = _jobService.CreateJob(
                request.ProjectPath,
                JobType.Analysis,
                externalSyncWrapperMethods: request.ExternalSyncWrapperMethods);

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
                request.ExternalSyncWrapperMethods,
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
                request.ExternalSyncWrapperMethods,
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
                request.ApplyChanges,
                request.ExternalSyncWrapperMethods);

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
                request.ExternalSyncWrapperMethods,
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

            if (request.ExternalSyncWrapperMethods.Count > 0)
            {
                syncWrappers.AddRange(request.ExternalSyncWrapperMethods.Select(methodId => new SyncWrapperMethod
                {
                    MethodId = methodId,
                    Name = string.Empty,
                    ContainingType = string.Empty,
                    FilePath = "external",
                    StartLine = 0,
                    ReturnType = string.Empty,
                    Signature = methodId,
                    PatternDescription = "External sync wrapper"
                }));
            }

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

            if (request.ExternalSyncWrapperMethods.Count > 0)
            {
                syncWrappers.AddRange(request.ExternalSyncWrapperMethods.Select(methodId => new SyncWrapperMethod
                {
                    MethodId = methodId,
                    Name = string.Empty,
                    ContainingType = string.Empty,
                    FilePath = "external",
                    StartLine = 0,
                    ReturnType = string.Empty,
                    Signature = methodId,
                    PatternDescription = "External sync wrapper"
                }));
            }

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

            var jobId = _jobService.CreateJob(
                request.ProjectPath,
                JobType.SyncWrapperAnalysis,
                externalSyncWrapperMethods: request.ExternalSyncWrapperMethods);

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
    /// Explains why a specific method requires async transformation by showing the call chain
    /// to the sync wrapper root that caused it
    /// </summary>
    [HttpGet("callgraph/{callGraphId}/explain/{methodId}")]
    public async Task<ActionResult<AsyncExplanationResponse>> ExplainAsyncMethod(
        string callGraphId,
        string methodId,
        CancellationToken cancellationToken)
    {
        try
        {
            var callGraph = await _callGraphRepository.GetCallGraphAsync(callGraphId, cancellationToken);

            if (callGraph == null)
                return NotFound(new { error = "Call graph not found" });

            if (!callGraph.Methods.TryGetValue(methodId, out var method))
                return NotFound(new { error = "Method not found in call graph" });

            var response = new AsyncExplanationResponse
            {
                MethodId = methodId,
                MethodName = method.Name,
                ContainingType = method.ContainingType,
                RequiresAsync = method.RequiresAsyncTransformation || method.IsAsync
            };

            if (!response.RequiresAsync)
            {
                response.Reasons.Add("This method does not require async transformation");
                return Ok(response);
            }

            if (method.IsAsync)
            {
                response.Reasons.Add("This method is already async");
                return Ok(response);
            }

            var propagationReasons = BuildPropagationReasons(callGraph, methodId);
            if (propagationReasons.Count > 0)
            {
                response.Reasons.AddRange(propagationReasons);
            }
            else
            {
                response.Reasons.Add("Async propagation reason not recorded during analysis");
            }

            var interfacePropagation = FindInterfacePropagation(callGraph, methodId, response);
            if (interfacePropagation.Handled)
            {
                var interfaceCallChain = FindCallChainToMethod(callGraph, methodId, interfacePropagation.InterfaceMethodId);
                if (interfaceCallChain.Count > 0)
                {
                    BuildCallChain(callGraph, response, interfaceCallChain);
                }

                return Ok(response);
            }

            // BFS to find the path from this method to a sync wrapper or root async method
            var queue = new Queue<(string MethodId, List<string> Path)>();
            var visited = new HashSet<string>();
            queue.Enqueue((methodId, new List<string> { methodId }));

            while (queue.Count > 0)
            {
                var (currentId, path) = queue.Dequeue();

                if (!visited.Add(currentId))
                    continue;

                if (callGraph.SyncWrapperMethods.Contains(currentId))
                {
                    if (callGraph.Methods.TryGetValue(currentId, out var rootMethod))
                    {
                        response.RootSyncWrapper = new SyncWrapperInfo
                        {
                            MethodId = currentId,
                            MethodName = rootMethod.Name,
                            ContainingType = rootMethod.ContainingType,
                            FilePath = rootMethod.FilePath,
                            LineNumber = rootMethod.StartLine,
                            PatternDescription = "Sync wrapper method (converts async to sync)"
                        };
                    }

                    BuildCallChain(callGraph, response, path);
                    response.Reasons.Add($"This method calls (directly or indirectly) the sync wrapper '{response.RootSyncWrapper?.ContainingType}.{response.RootSyncWrapper?.MethodName}', which requires async propagation up the call chain");
                    return Ok(response);
                }

                if (callGraph.RootAsyncMethods.Contains(currentId))
                {
                    if (callGraph.Methods.TryGetValue(currentId, out var rootMethod))
                    {
                        response.RootAsyncMethod = ToMethodReference(rootMethod);
                    }

                    BuildCallChain(callGraph, response, path);
                    response.Reasons.Add($"This method calls (directly or indirectly) the async root '{response.RootAsyncMethod?.ContainingType}.{response.RootAsyncMethod?.MethodName}', which requires async propagation up the call chain");
                    return Ok(response);
                }

                // Get all callees and add them to the queue
                var callees = callGraph.Calls
                    .Where(c => c.CallerId == currentId)
                    .Select(c => c.CalleeId)
                    .Distinct();

                foreach (var calleeId in callees)
                {
                    if (!visited.Contains(calleeId))
                    {
                        var newPath = new List<string>(path) { calleeId };
                        queue.Enqueue((calleeId, newPath));
                    }
                }
            }

            response.Reasons.Add("This method requires async transformation, but no call path to a root async method was found");
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to explain async method");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private (bool Handled, string? InterfaceMethodId) FindInterfacePropagation(CallGraph callGraph, string methodId, AsyncExplanationResponse response)
    {
        if (!callGraph.Methods.TryGetValue(methodId, out var method))
        {
            return (false, null);
        }

        if (method.ImplementsInterfaceMethods.Count == 0)
        {
            return (false, null);
        }

        foreach (var interfaceMethodId in method.ImplementsInterfaceMethods)
        {
            if (!callGraph.Methods.TryGetValue(interfaceMethodId, out var interfaceMethod))
            {
                continue;
            }

            if (!interfaceMethod.RequiresAsyncTransformation && !interfaceMethod.IsAsync)
            {
                continue;
            }

            var otherImplementations = callGraph.Methods.Values
                .Where(m => m.Id != methodId && m.ImplementsInterfaceMethods.Contains(interfaceMethodId))
                .ToList();

            var interfaceInfo = new InterfacePropagationInfo
            {
                InterfaceMethod = ToMethodReference(interfaceMethod),
                Reason = "This method must remain compatible with the async interface contract"
            };

            interfaceInfo.Implementations.Add(ToMethodReference(method));

            foreach (var implementation in otherImplementations)
            {
                interfaceInfo.Implementations.Add(ToMethodReference(implementation));
            }

            response.InterfacePropagation.Add(interfaceInfo);
            response.Reasons.Add($"This method implements interface method '{interfaceMethod.ContainingType}.{interfaceMethod.Name}', which is async or requires async transformation");
            return (true, interfaceMethodId);
        }

        return (false, null);
    }

    private void BuildCallChain(CallGraph callGraph, AsyncExplanationResponse response, List<string> path)
    {
        for (int i = 0; i < path.Count - 1; i++)
        {
            var callerId = path[i];
            var calleeId = path[i + 1];

            if (callGraph.Methods.TryGetValue(callerId, out var callerMethod))
            {
                var call = callGraph.Calls.FirstOrDefault(c => c.CallerId == callerId && c.CalleeId == calleeId);

                response.CallChain.Add(new AsyncExplanationStep
                {
                    MethodId = callerId,
                    MethodName = callerMethod.Name,
                    ContainingType = callerMethod.ContainingType,
                    FilePath = call?.FilePath ?? callerMethod.FilePath,
                    LineNumber = call?.LineNumber ?? callerMethod.StartLine,
                    Relationship = "calls"
                });
            }
        }
    }

    private static MethodReference ToMethodReference(MethodNode method)
    {
        return new MethodReference
        {
            MethodId = method.Id,
            MethodName = method.Name,
            ContainingType = method.ContainingType,
            FilePath = method.FilePath,
            LineNumber = method.StartLine
        };
    }

    private static List<string> BuildPropagationReasons(CallGraph callGraph, string methodId)
    {
        var reasons = new List<string>();
        var visited = new HashSet<string>();
        var currentId = methodId;

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (!callGraph.Methods.TryGetValue(currentId, out var method))
            {
                break;
            }

            if (callGraph.RootAsyncMethods.Contains(currentId))
            {
                reasons.Add($"Marked as async root '{method.ContainingType}.{method.Name}'");
                break;
            }

            if (method.ImplementsInterfaceMethods.Count > 0)
            {
                var interfaceId = method.ImplementsInterfaceMethods[0];
                if (callGraph.Methods.TryGetValue(interfaceId, out var interfaceMethod))
                {
                    reasons.Add($"Implements interface method '{interfaceMethod.ContainingType}.{interfaceMethod.Name}'");
                }
            }

            var sourceId = method.AsyncPropagationSourceMethodId;
            if (string.IsNullOrWhiteSpace(sourceId) || sourceId == currentId)
            {
                break;
            }

            if (callGraph.Methods.TryGetValue(sourceId, out var sourceMethod))
            {
                reasons.Add($"Calls '{sourceMethod.ContainingType}.{sourceMethod.Name}', which requires async propagation");
            }

            currentId = sourceId;
        }

        return reasons;
    }

    private static List<string> FindCallChainToMethod(CallGraph callGraph, string startMethodId, string? targetMethodId)
    {
        if (string.IsNullOrWhiteSpace(targetMethodId))
        {
            return new List<string>();
        }

        var queue = new Queue<(string MethodId, List<string> Path)>();
        var visited = new HashSet<string>();
        queue.Enqueue((startMethodId, new List<string> { startMethodId }));

        while (queue.Count > 0)
        {
            var (currentId, path) = queue.Dequeue();

            if (!visited.Add(currentId))
            {
                continue;
            }

            if (currentId == targetMethodId)
            {
                return path;
            }

            var callees = callGraph.Calls
                .Where(c => c.CallerId == currentId)
                .Select(c => c.CalleeId)
                .Distinct();

            foreach (var calleeId in callees)
            {
                if (!visited.Contains(calleeId))
                {
                    var newPath = new List<string>(path) { calleeId };
                    queue.Enqueue((calleeId, newPath));
                }
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// Searches for methods in a call graph by name pattern
    /// </summary>
    [HttpGet("callgraph/{callGraphId}/search")]
    public async Task<ActionResult<List<MethodSearchResult>>> SearchMethods(
        string callGraphId,
        [FromQuery] string query,
        [FromQuery] bool floodedOnly = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var callGraph = await _callGraphRepository.GetCallGraphAsync(callGraphId, cancellationToken);

            if (callGraph == null)
                return NotFound(new { error = "Call graph not found" });

            var results = callGraph.Methods.Values
                .Where(m =>
                    (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     m.ContainingType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
                    (!floodedOnly || m.RequiresAsyncTransformation))
                .Take(50)
                .Select(m => new MethodSearchResult
                {
                    MethodId = m.Id,
                    MethodName = m.Name,
                    ContainingType = m.ContainingType,
                    FilePath = m.FilePath,
                    StartLine = m.StartLine,
                    RequiresAsyncTransformation = m.RequiresAsyncTransformation,
                    IsAsync = m.IsAsync,
                    IsSyncWrapper = callGraph.SyncWrapperMethods.Contains(m.Id)
                })
                .ToList();

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search methods");
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
