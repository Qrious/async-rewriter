using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsyncRewriter.Client;

class Program
{
    private static readonly HttpClient _httpClient = new();
    private static string _baseUrl = "http://localhost:5000";

    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Async Rewriter Client - Analyze C# codebases for async transformation");

        // Base URL option
        var baseUrlOption = new Option<string>(
            aliases: new[] { "--base-url", "-u" },
            description: "The base URL of the Async Rewriter API server",
            getDefaultValue: () => "http://localhost:5000");

        rootCommand.AddGlobalOption(baseUrlOption);

        // Analyze command
        var analyzeCommand = new Command("analyze", "Start an async analysis of a C# project");
        var projectPathArgument = new Argument<string>("project-path", "The path to the C# project to analyze");
        var waitOption = new Option<bool>(
            aliases: new[] { "--wait", "-w" },
            description: "Wait for the analysis to complete and show progress",
            getDefaultValue: () => true);
        var pollIntervalOption = new Option<int>(
            aliases: new[] { "--poll-interval", "-p" },
            description: "Polling interval in milliseconds when waiting for completion",
            getDefaultValue: () => 2000);

        analyzeCommand.AddArgument(projectPathArgument);
        analyzeCommand.AddOption(waitOption);
        analyzeCommand.AddOption(pollIntervalOption);

        analyzeCommand.SetHandler(async (baseUrl, projectPath, wait, pollInterval) =>
        {
            _baseUrl = baseUrl;
            await AnalyzeProjectAsync(projectPath, wait, pollInterval);
        }, baseUrlOption, projectPathArgument, waitOption, pollIntervalOption);

        // Status command
        var statusCommand = new Command("status", "Check the status of an analysis job");
        var jobIdArgument = new Argument<string>("job-id", "The ID of the job to check");
        statusCommand.AddArgument(jobIdArgument);

        statusCommand.SetHandler(async (baseUrl, jobId) =>
        {
            _baseUrl = baseUrl;
            await GetJobStatusAsync(jobId);
        }, baseUrlOption, jobIdArgument);

        // Cancel command
        var cancelCommand = new Command("cancel", "Cancel a running analysis job");
        var cancelJobIdArgument = new Argument<string>("job-id", "The ID of the job to cancel");
        cancelCommand.AddArgument(cancelJobIdArgument);

        cancelCommand.SetHandler(async (baseUrl, jobId) =>
        {
            _baseUrl = baseUrl;
            await CancelJobAsync(jobId);
        }, baseUrlOption, cancelJobIdArgument);

        // Find sync wrappers command
        var findSyncWrappersCommand = new Command("find-sync-wrappers", "Find sync-over-async wrapper methods in a project");
        var syncWrapperProjectPath = new Argument<string>("project-path", "The path to the C# project to analyze");
        var analyzeFromWrappersOption = new Option<bool>(
            aliases: new[] { "--analyze", "-a" },
            description: "Automatically run async flooding analysis from the found sync wrappers",
            getDefaultValue: () => false);

        var applyChangesOption = new Option<bool>(
            aliases: new[] { "--apply", "-y" },
            description: "Automatically apply changes without prompting (use with caution)",
            getDefaultValue: () => false);

        var syncWrapperPollIntervalOption = new Option<int>(
            aliases: new[] { "--poll-interval", "-p" },
            description: "Polling interval in milliseconds when waiting for completion",
            getDefaultValue: () => 2000);

        findSyncWrappersCommand.AddArgument(syncWrapperProjectPath);
        findSyncWrappersCommand.AddOption(analyzeFromWrappersOption);
        findSyncWrappersCommand.AddOption(applyChangesOption);
        findSyncWrappersCommand.AddOption(syncWrapperPollIntervalOption);

        findSyncWrappersCommand.SetHandler(async (baseUrl, projectPath, analyzeFromWrappers, applyChanges, pollInterval) =>
        {
            _baseUrl = baseUrl;
            await FindSyncWrappersAsync(projectPath, analyzeFromWrappers, applyChanges, pollInterval);
        }, baseUrlOption, syncWrapperProjectPath, analyzeFromWrappersOption, applyChangesOption, syncWrapperPollIntervalOption);

        // Transform command
        var transformCommand = new Command("transform", "Transform a C# project from sync to async based on a call graph");
        var transformProjectPath = new Argument<string>("project-path", "The path to the C# project to transform");
        var transformCallGraphId = new Argument<string>("call-graph-id", "The ID of the call graph to use for transformation");
        var transformApplyOption = new Option<bool>(
            aliases: new[] { "--apply", "-y" },
            description: "Apply the changes to the files (default is preview only)",
            getDefaultValue: () => false);

        transformCommand.AddArgument(transformProjectPath);
        transformCommand.AddArgument(transformCallGraphId);
        transformCommand.AddOption(transformApplyOption);

        transformCommand.SetHandler(async (baseUrl, projectPath, callGraphId, applyChanges) =>
        {
            _baseUrl = baseUrl;
            await TransformProjectAsync(projectPath, callGraphId, applyChanges);
        }, baseUrlOption, transformProjectPath, transformCallGraphId, transformApplyOption);

        rootCommand.AddCommand(analyzeCommand);
        rootCommand.AddCommand(statusCommand);
        rootCommand.AddCommand(cancelCommand);
        rootCommand.AddCommand(findSyncWrappersCommand);
        rootCommand.AddCommand(transformCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task AnalyzeProjectAsync(string projectPath, bool wait, int pollInterval)
    {
        try
        {
            Console.WriteLine($"Starting analysis for project: {projectPath}");
            Console.WriteLine();

            var request = new { projectPath };
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/asynctransformation/analyze/project/async", request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return;
            }

            var jobResponse = await response.Content.ReadFromJsonAsync<AnalysisJobResponse>();
            if (jobResponse == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Failed to deserialize job response");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Job created successfully!");
            Console.ResetColor();
            Console.WriteLine($"Job ID: {jobResponse.JobId}");
            Console.WriteLine($"Status: {jobResponse.Status}");
            Console.WriteLine($"Message: {jobResponse.Message}");
            Console.WriteLine();

            if (wait)
            {
                Console.WriteLine("Waiting for analysis to complete...");
                Console.WriteLine();
                await WaitForCompletionAsync(jobResponse.JobId, pollInterval);
            }
            else
            {
                Console.WriteLine($"Use 'status {jobResponse.JobId}' to check the progress");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error connecting to API: {ex.Message}");
            Console.WriteLine($"Make sure the API server is running at {_baseUrl}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static async Task GetJobStatusAsync(string jobId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/api/asynctransformation/jobs/{jobId}");

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Job not found: {jobId}");
                Console.ResetColor();
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return;
            }

            var status = await response.Content.ReadFromJsonAsync<JobStatusResponse>();
            if (status == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Failed to deserialize status response");
                Console.ResetColor();
                return;
            }

            PrintJobStatus(status);
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error connecting to API: {ex.Message}");
            Console.WriteLine($"Make sure the API server is running at {_baseUrl}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static async Task CancelJobAsync(string jobId)
    {
        try
        {
            var response = await _httpClient.PostAsync($"{_baseUrl}/api/asynctransformation/jobs/{jobId}/cancel", null);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Job {jobId} cancelled successfully");
            Console.ResetColor();
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error connecting to API: {ex.Message}");
            Console.WriteLine($"Make sure the API server is running at {_baseUrl}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static async Task WaitForCompletionAsync(string jobId, int pollInterval)
    {
        var lastProgress = -1;
        var spinner = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        var spinnerIndex = 0;

        while (true)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/asynctransformation/jobs/{jobId}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error retrieving job status");
                    Console.ResetColor();
                    return;
                }

                var status = await response.Content.ReadFromJsonAsync<JobStatusResponse>();
                if (status == null)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Failed to deserialize status response");
                    Console.ResetColor();
                    return;
                }

                if (status.ProgressPercentage != lastProgress)
                {
                    Console.Write("\r" + new string(' ', 100) + "\r");
                    Console.Write($"{spinner[spinnerIndex]} {status.Status} - {status.ProgressPercentage}% - {status.CurrentStep}");
                    lastProgress = status.ProgressPercentage;
                    spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                }

                if (status.Status == JobStatus.Completed)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Analysis completed successfully!");
                    Console.ResetColor();
                    Console.WriteLine();
                    PrintJobStatus(status);
                    return;
                }
                else if (status.Status == JobStatus.Failed)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ Analysis failed");
                    Console.ResetColor();
                    Console.WriteLine($"Error: {status.ErrorMessage}");
                    return;
                }
                else if (status.Status == JobStatus.Cancelled)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Analysis was cancelled");
                    Console.ResetColor();
                    return;
                }

                await Task.Delay(pollInterval);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
                return;
            }
        }
    }

    static async Task FindSyncWrappersAsync(string projectPath, bool analyzeFromWrappers, bool applyChanges, int pollInterval)
    {
        try
        {
            Console.WriteLine($"Finding sync wrapper methods in project: {projectPath}");
            Console.WriteLine();

            var request = new { projectPath };

            if (analyzeFromWrappers)
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/asynctransformation/analyze/from-sync-wrappers/async", request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error ({response.StatusCode}): {(string.IsNullOrEmpty(error) ? "No error details provided" : error)}");
                    Console.ResetColor();
                    return;
                }

                var jobResponse = await response.Content.ReadFromJsonAsync<AnalysisJobResponse>();
                if (jobResponse == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Failed to deserialize job response");
                    Console.ResetColor();
                    return;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✓ Job created successfully!");
                Console.ResetColor();
                Console.WriteLine($"Job ID: {jobResponse.JobId}");
                Console.WriteLine($"Status: {jobResponse.Status}");
                Console.WriteLine($"Message: {jobResponse.Message}");
                Console.WriteLine();

                await WaitForCompletionAsync(jobResponse.JobId, pollInterval);

                var statusResponse = await _httpClient.GetAsync($"{_baseUrl}/api/asynctransformation/jobs/{jobResponse.JobId}");
                if (!statusResponse.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error retrieving sync wrapper analysis result");
                    Console.ResetColor();
                    return;
                }

                var status = await statusResponse.Content.ReadFromJsonAsync<JobStatusResponse>();
                if (status == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Failed to deserialize status response");
                    Console.ResetColor();
                    return;
                }

                if (status.Status == JobStatus.Completed)
                {
                    if (applyChanges && !string.IsNullOrWhiteSpace(status.CallGraphId) && (status.FloodedMethodCount ?? 0) > 0)
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Applying transformations to {status.FloodedMethodCount} method(s)...");
                        Console.ResetColor();

                        await TransformProjectAsync(projectPath, status.CallGraphId!, true);
                    }
                }
            }
            else
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/asynctransformation/find-sync-wrappers/project", request);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Error ({response.StatusCode}): {(string.IsNullOrEmpty(error) ? "No error details provided" : error)}");
                    Console.ResetColor();
                    return;
                }

                var syncWrappers = await response.Content.ReadFromJsonAsync<List<SyncWrapperMethod>>();
                if (syncWrappers == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: Failed to deserialize response");
                    Console.ResetColor();
                    return;
                }

                PrintSyncWrappers(syncWrappers);
            }
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error connecting to API: {ex.Message}");
            Console.WriteLine($"Make sure the API server is running at {_baseUrl}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static async Task TransformProjectAsync(string projectPath, string callGraphId, bool applyChanges)
    {
        try
        {
            Console.WriteLine($"Transforming project: {projectPath}");
            Console.WriteLine($"Call Graph ID: {callGraphId}");
            Console.WriteLine($"Apply Changes: {applyChanges}");
            Console.WriteLine();

            var request = new { projectPath, callGraphId, applyChanges };
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/asynctransformation/transform/project", request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error ({response.StatusCode}): {(string.IsNullOrEmpty(error) ? "No error details provided" : error)}");
                Console.ResetColor();
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<TransformationResult>();
            if (result == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Failed to deserialize transformation result");
                Console.ResetColor();
                return;
            }

            PrintTransformationResult(result, applyChanges);
        }
        catch (HttpRequestException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error connecting to API: {ex.Message}");
            Console.WriteLine($"Make sure the API server is running at {_baseUrl}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static void PrintTransformationResult(TransformationResult result, bool applied)
    {
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✓ Transformation {(applied ? "applied" : "preview generated")} successfully!");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine($"Files modified: {result.ModifiedFiles.Count}");
            Console.WriteLine();

            foreach (var file in result.ModifiedFiles)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {file.FilePath}");
                Console.ResetColor();
                Console.WriteLine($"    Methods transformed: {file.TransformedMethods.Count}");
                Console.WriteLine($"    Await keywords added: {file.AwaitLocations.Count}");

                if (file.TransformedMethods.Count > 0)
                {
                    Console.WriteLine("    Transformed methods:");
                    foreach (var method in file.TransformedMethods)
                    {
                        Console.WriteLine($"      - {method}");
                    }
                }
                Console.WriteLine();
            }

            if (!applied)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Note: Changes have not been applied to the files.");
                Console.WriteLine("To apply the changes, use the --apply flag.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"✓ Changes have been written to {result.ModifiedFiles.Count} file(s).");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("✗ Transformation failed");
            Console.ResetColor();

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Errors:");
                foreach (var error in result.Errors)
                {
                    Console.WriteLine($"  - {error}");
                }
            }
        }
    }

    static void PrintSyncWrappers(List<SyncWrapperMethod> syncWrappers)
    {
        if (syncWrappers.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No sync wrapper methods found in the project.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Found {syncWrappers.Count} sync wrapper method(s):");
        Console.ResetColor();
        Console.WriteLine();

        foreach (var wrapper in syncWrappers)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  {wrapper.ContainingType}.{wrapper.Signature}");
            Console.ResetColor();
            Console.WriteLine($"    File: {wrapper.FilePath}:{wrapper.StartLine}");
            Console.WriteLine($"    Return Type: {wrapper.ReturnType}");
            Console.WriteLine($"    Pattern: {wrapper.PatternDescription}");
            Console.WriteLine();
        }

        Console.WriteLine("These methods are candidates for async transformation.");
        Console.WriteLine("Use --analyze (-a) flag to automatically run flooding analysis from these methods.");
    }

    static void PrintSyncWrapperAnalysisResult(SyncWrapperAnalysisResult result)
    {
        Console.WriteLine(result.Message);
        Console.WriteLine();

        if (result.SyncWrappers.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Sync Wrapper Methods (Root Methods for Transformation):");
            Console.ResetColor();
            foreach (var wrapper in result.SyncWrappers)
            {
                Console.WriteLine($"  - {wrapper.ContainingType}.{wrapper.Signature}");
                Console.WriteLine($"    {wrapper.PatternDescription}");
            }
            Console.WriteLine();
        }

        if (result.CallGraph != null && result.CallGraph.FloodedMethods.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Methods requiring async transformation ({result.CallGraph.FloodedMethods.Count}):");
            Console.ResetColor();

            foreach (var methodId in result.CallGraph.FloodedMethods)
            {
                if (result.CallGraph.Methods.TryGetValue(methodId, out var method))
                {
                    Console.WriteLine($"  - {method.ContainingType}.{method.Signature}");
                    Console.WriteLine($"    Current: {method.ReturnType} -> Will become: {method.AsyncReturnType}");
                }
            }
            Console.WriteLine();

            Console.WriteLine($"Call Graph ID: {result.CallGraph.Id}");
            Console.WriteLine("Use this ID with the transform command to apply the changes.");
        }
    }

    static void PrintJobStatus(JobStatusResponse status)
    {
        Console.WriteLine("Job Status:");
        Console.WriteLine($"  Job ID: {status.JobId}");
        Console.WriteLine($"  Status: {status.Status}");
        Console.WriteLine($"  Progress: {status.ProgressPercentage}%");
        Console.WriteLine($"  Current Step: {status.CurrentStep}");
        Console.WriteLine($"  Created At: {status.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");

        if (status.StartedAt.HasValue)
            Console.WriteLine($"  Started At: {status.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");

        if (status.CompletedAt.HasValue)
            Console.WriteLine($"  Completed At: {status.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC");

        if (!string.IsNullOrEmpty(status.ErrorMessage))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Error: {status.ErrorMessage}");
            Console.ResetColor();
        }

        if (!string.IsNullOrWhiteSpace(status.PendingWorkSummary))
        {
            Console.WriteLine($"  Pending: {status.PendingWorkSummary}");
        }

        if (!string.IsNullOrWhiteSpace(status.CallGraphId))
        {
            Console.WriteLine($"  Call Graph ID: {status.CallGraphId}");
        }

        if (status.MethodCount.HasValue)
        {
            var processed = status.MethodsProcessed ?? 0;
            Console.WriteLine($"  Methods: {processed} / {status.MethodCount}");
        }

        if (status.MethodsRemaining.HasValue)
        {
            Console.WriteLine($"  Methods Remaining: {status.MethodsRemaining}");
        }

        if (status.FloodedMethodCount.HasValue)
        {
            Console.WriteLine($"  Flooded Methods: {status.FloodedMethodCount}");
        }

        if (status.SyncWrapperCount.HasValue)
        {
            Console.WriteLine($"  Sync Wrappers: {status.SyncWrapperCount}");
        }

        if (status.SyncWrappers != null && status.SyncWrappers.Count > 0)
        {
            Console.WriteLine("  Sync Wrapper Methods:");
            foreach (var wrapper in status.SyncWrappers)
            {
                Console.WriteLine($"    - {wrapper.ContainingType}.{wrapper.Signature}");
                Console.WriteLine($"      {wrapper.FilePath}:{wrapper.StartLine}");
                Console.WriteLine($"      {wrapper.PatternDescription}");
            }
        }
    }
}

// DTOs matching the server
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

public class SyncWrapperMethod
{
    public string MethodId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public string ReturnType { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    public string PatternDescription { get; set; } = string.Empty;
}

public class SyncWrapperAnalysisResult
{
    public List<SyncWrapperMethod> SyncWrappers { get; set; } = new();
    public CallGraphResult? CallGraph { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class SyncWrapperAnalysisJobResult
{
    public List<SyncWrapperMethod> SyncWrappers { get; set; } = new();
    public CallGraphResult? CallGraph { get; set; }
}

public class CallGraphResult
{
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, MethodNodeResult> Methods { get; set; } = new();
    public HashSet<string> FloodedMethods { get; set; } = new();
}

public class MethodNodeResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public string? AsyncReturnType { get; set; }
    public string Signature { get; set; } = string.Empty;
    public bool RequiresAsyncTransformation { get; set; }
}

public class TransformationResult
{
    public bool Success { get; set; }
    public List<FileTransformation> ModifiedFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class FileTransformation
{
    public string FilePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string TransformedContent { get; set; } = string.Empty;
    public List<string> TransformedMethods { get; set; } = new();
    public List<int> AwaitLocations { get; set; } = new();
}
