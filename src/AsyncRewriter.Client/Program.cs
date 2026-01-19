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
            getDefaultValue: () => 1000);

        var externalSyncWrapperOption = new Option<string[]>(
            aliases: new[] { "--external-sync-wrapper", "-esw" },
            description: "Fully qualified method IDs to treat as sync wrappers (e.g. MyLib.AsyncHelper.RunTaskSynchronously(Func<Task<TResult>>))",
            getDefaultValue: Array.Empty<string>);

        var transformPollIntervalOption = new Option<int>(
            aliases: new[] { "--transform-poll-interval", "-tp" },
            description: "Polling interval in milliseconds when waiting for transformation",
            getDefaultValue: () => 1000);

        analyzeCommand.AddArgument(projectPathArgument);
        analyzeCommand.AddOption(waitOption);
        analyzeCommand.AddOption(pollIntervalOption);
        analyzeCommand.AddOption(externalSyncWrapperOption);

        analyzeCommand.SetHandler(async (baseUrl, projectPath, wait, pollInterval, externalSyncWrappers) =>
        {
            _baseUrl = baseUrl;
            await AnalyzeProjectAsync(projectPath, wait, pollInterval, externalSyncWrappers);
        }, baseUrlOption, projectPathArgument, waitOption, pollIntervalOption, externalSyncWrapperOption);

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
            getDefaultValue: () => 1000);

        var syncWrapperTransformPollIntervalOption = new Option<int>(
            aliases: new[] { "--transform-poll-interval", "-tp" },
            description: "Polling interval in milliseconds when waiting for transformation",
            getDefaultValue: () => 1000);

        findSyncWrappersCommand.AddArgument(syncWrapperProjectPath);
        findSyncWrappersCommand.AddOption(analyzeFromWrappersOption);
        findSyncWrappersCommand.AddOption(applyChangesOption);
        findSyncWrappersCommand.AddOption(syncWrapperPollIntervalOption);
        findSyncWrappersCommand.AddOption(syncWrapperTransformPollIntervalOption);
        findSyncWrappersCommand.AddOption(externalSyncWrapperOption);

        findSyncWrappersCommand.SetHandler(async (baseUrl, projectPath, analyzeFromWrappers, applyChanges, pollInterval, transformPollInterval, externalSyncWrappers) =>
        {
            _baseUrl = baseUrl;
            await FindSyncWrappersAsync(projectPath, analyzeFromWrappers, applyChanges, pollInterval, transformPollInterval, externalSyncWrappers);
        }, baseUrlOption, syncWrapperProjectPath, analyzeFromWrappersOption, applyChangesOption, syncWrapperPollIntervalOption, syncWrapperTransformPollIntervalOption, externalSyncWrapperOption);

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
        transformCommand.AddOption(transformPollIntervalOption);
        transformCommand.AddOption(externalSyncWrapperOption);

        transformCommand.SetHandler(async (baseUrl, projectPath, callGraphId, applyChanges, pollInterval, externalSyncWrappers) =>
        {
            _baseUrl = baseUrl;
            await TransformProjectAsync(projectPath, callGraphId, applyChanges, pollInterval, externalSyncWrappers);
        }, baseUrlOption, transformProjectPath, transformCallGraphId, transformApplyOption, transformPollIntervalOption, externalSyncWrapperOption);

        // Search command - search for methods in a call graph
        var searchCommand = new Command("search", "Search for methods in a call graph");
        var searchCallGraphIdArgument = new Argument<string>("call-graph-id", "The ID of the call graph to search");
        var searchQueryArgument = new Argument<string>("query", "The search query (matches method name, type, or ID)");
        var floodedOnlyOption = new Option<bool>(
            aliases: new[] { "--flooded-only", "-f" },
            description: "Only show methods that require async transformation",
            getDefaultValue: () => false);

        searchCommand.AddArgument(searchCallGraphIdArgument);
        searchCommand.AddArgument(searchQueryArgument);
        searchCommand.AddOption(floodedOnlyOption);

        searchCommand.SetHandler(async (baseUrl, callGraphId, query, floodedOnly) =>
        {
            _baseUrl = baseUrl;
            await SearchMethodsAsync(callGraphId, query, floodedOnly);
        }, baseUrlOption, searchCallGraphIdArgument, searchQueryArgument, floodedOnlyOption);

        // Explain command - explain why a method became async
        var explainCommand = new Command("explain", "Explain why a method requires async transformation");
        var explainCallGraphIdArgument = new Argument<string>("call-graph-id", "The ID of the call graph");
        var explainMethodIdArgument = new Argument<string>("method-id", "The ID of the method to explain");

        explainCommand.AddArgument(explainCallGraphIdArgument);
        explainCommand.AddArgument(explainMethodIdArgument);

        explainCommand.SetHandler(async (baseUrl, callGraphId, methodId) =>
        {
            _baseUrl = baseUrl;
            await ExplainMethodAsync(callGraphId, methodId);
        }, baseUrlOption, explainCallGraphIdArgument, explainMethodIdArgument);

        rootCommand.AddCommand(analyzeCommand);
        rootCommand.AddCommand(statusCommand);
        rootCommand.AddCommand(cancelCommand);
        rootCommand.AddCommand(findSyncWrappersCommand);
        rootCommand.AddCommand(transformCommand);
        rootCommand.AddCommand(searchCommand);
        rootCommand.AddCommand(explainCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task AnalyzeProjectAsync(string projectPath, bool wait, int pollInterval, string[] externalSyncWrappers)
    {
        try
        {
            Console.WriteLine($"Starting analysis for project: {projectPath}");
            Console.WriteLine();

            var request = new { projectPath, externalSyncWrapperMethods = externalSyncWrappers };
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

                // Always update the spinner and show current progress details
                var currentFileLabel = !string.IsNullOrWhiteSpace(status.CurrentFile)
                    ? $" - {System.IO.Path.GetFileName(status.CurrentFile)}"
                    : string.Empty;

                var currentMethodLabel = !string.IsNullOrWhiteSpace(status.CurrentMethod)
                    ? $" - {status.CurrentMethod}"
                    : string.Empty;

                var fileProgress = status.MethodCount.HasValue && status.MethodCount > 0
                    ? $" ({status.MethodsProcessed ?? 0}/{status.MethodCount})"
                    : string.Empty;

                Console.Write("\r" + new string(' ', 140) + "\r");
                Console.Write($"{spinner[spinnerIndex]} {status.Status} - {status.ProgressPercentage}%{fileProgress} - {status.CurrentStep}{currentFileLabel}{currentMethodLabel}");
                lastProgress = status.ProgressPercentage;
                spinnerIndex = (spinnerIndex + 1) % spinner.Length;

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

    static async Task WaitForTransformationCompletionAsync(string jobId, int pollInterval, bool appliedChanges)
    {
        var lastProgress = -1;
        var spinner = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        var spinnerIndex = 0;

        while (true)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/asynctransformation/transform/project/{jobId}/status");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error retrieving transformation status");
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
                    var currentFileLabel = string.IsNullOrWhiteSpace(status.CurrentFile)
                        ? ""
                        : $" - {System.IO.Path.GetFileName(status.CurrentFile)}";

                    var fileProgress = status.TotalFileCount.HasValue
                        ? $" ({status.TransformedFileCount ?? 0}/{status.TotalFileCount})"
                        : string.Empty;

                    Console.Write("\r" + new string(' ', 120) + "\r");
                    Console.Write($"{spinner[spinnerIndex]} {status.Status} - {status.ProgressPercentage}%{fileProgress} - {status.CurrentStep}{currentFileLabel}");
                    lastProgress = status.ProgressPercentage;
                    spinnerIndex = (spinnerIndex + 1) % spinner.Length;
                }

                if (status.Status == JobStatus.Completed)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✓ Transformation {(appliedChanges ? "applied" : "preview")} completed successfully!");
                    Console.ResetColor();
                    Console.WriteLine();
                    PrintTransformationJobStatus(status, appliedChanges);
                    return;
                }
                else if (status.Status == JobStatus.Failed)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ Transformation failed");
                    Console.ResetColor();
                    Console.WriteLine($"Error: {status.ErrorMessage}");
                    return;
                }
                else if (status.Status == JobStatus.Cancelled)
                {
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Transformation was cancelled");
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

    static async Task FindSyncWrappersAsync(
        string projectPath,
        bool analyzeFromWrappers,
        bool applyChanges,
        int pollInterval,
        int transformPollInterval,
        string[] externalSyncWrappers)
    {
        try
        {
            Console.WriteLine($"Finding sync wrapper methods in project: {projectPath}");
            Console.WriteLine();

            var request = new { projectPath, externalSyncWrapperMethods = externalSyncWrappers };

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

                        await TransformProjectAsync(projectPath, status.CallGraphId!, true, transformPollInterval, externalSyncWrappers);
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

    static async Task TransformProjectAsync(
        string projectPath,
        string callGraphId,
        bool applyChanges,
        int pollInterval,
        string[] externalSyncWrappers)
    {
        try
        {
            Console.WriteLine($"Transforming project: {projectPath}");
            Console.WriteLine($"Call Graph ID: {callGraphId}");
            Console.WriteLine($"Apply Changes: {applyChanges}");
            Console.WriteLine();

            var request = new { projectPath, callGraphId, applyChanges, externalSyncWrapperMethods = externalSyncWrappers };
            var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/asynctransformation/transform/project", request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error ({response.StatusCode}): {(string.IsNullOrEmpty(error) ? "No error details provided" : error)}");
                Console.ResetColor();
                return;
            }

            var jobResponse = await response.Content.ReadFromJsonAsync<TransformationJobResponse>();
            if (jobResponse == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: Failed to deserialize transformation job response");
                Console.ResetColor();
                return;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✓ Transformation job queued successfully!");
            Console.ResetColor();
            Console.WriteLine($"Job ID: {jobResponse.JobId}");
            Console.WriteLine($"Status: {jobResponse.Status}");
            Console.WriteLine($"Message: {jobResponse.Message}");
            Console.WriteLine();

            await WaitForTransformationCompletionAsync(jobResponse.JobId, pollInterval, applyChanges);
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

    static async Task SearchMethodsAsync(string callGraphId, string query, bool floodedOnly)
    {
        try
        {
            Console.WriteLine($"Searching for '{query}' in call graph {callGraphId}...");

            var url = $"{_baseUrl}/api/asynctransformation/callgraph/{Uri.EscapeDataString(callGraphId)}/search?query={Uri.EscapeDataString(query)}&floodedOnly={floodedOnly}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return;
            }

            var results = await response.Content.ReadFromJsonAsync<List<MethodSearchResult>>();

            if (results == null || results.Count == 0)
            {
                Console.WriteLine("No methods found matching the query.");
                return;
            }

            Console.WriteLine($"\nFound {results.Count} method(s):\n");

            foreach (var method in results)
            {
                var asyncMarker = method.RequiresAsyncTransformation ? "[NEEDS ASYNC]" :
                                  method.IsAsync ? "[ASYNC]" :
                                  method.IsSyncWrapper ? "[SYNC WRAPPER]" : "";

                if (!string.IsNullOrEmpty(asyncMarker))
                {
                    Console.ForegroundColor = method.RequiresAsyncTransformation ? ConsoleColor.Yellow :
                                              method.IsSyncWrapper ? ConsoleColor.Magenta : ConsoleColor.Green;
                    Console.Write($"  {asyncMarker} ");
                    Console.ResetColor();
                }
                else
                {
                    Console.Write("  ");
                }

                Console.WriteLine($"{method.ContainingType}.{method.MethodName}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"    ID: {method.MethodId}");
                Console.WriteLine($"    {method.FilePath}:{method.StartLine}");
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.WriteLine("Use 'explain <call-graph-id> <method-id>' to see why a method needs async.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error searching methods: {ex.Message}");
            Console.ResetColor();
        }
    }

    static async Task ExplainMethodAsync(string callGraphId, string methodId)
    {
        try
        {
            Console.WriteLine($"Explaining async requirement for method...\n");

            var url = $"{_baseUrl}/api/asynctransformation/callgraph/{Uri.EscapeDataString(callGraphId)}/explain/{Uri.EscapeDataString(methodId)}";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {error}");
                Console.ResetColor();
                return;
            }

            var explanation = await response.Content.ReadFromJsonAsync<AsyncExplanationResponse>();

            if (explanation == null)
            {
                Console.WriteLine("No explanation available.");
                return;
            }

            // Print method info
            Console.WriteLine($"Method: {explanation.ContainingType}.{explanation.MethodName}");
            Console.WriteLine($"ID: {explanation.MethodId}");
            Console.WriteLine();

            // Print async status
            if (explanation.RequiresAsync)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Status: REQUIRES ASYNC TRANSFORMATION");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Status: Does not require async");
                Console.ResetColor();
            }
            Console.WriteLine();

            // Print reason
            if (!string.IsNullOrEmpty(explanation.Reason))
            {
                Console.WriteLine($"Reason: {explanation.Reason}");
                Console.WriteLine();
            }

            // Print call chain
            if (explanation.CallChain.Count > 0)
            {
                var rootLabel = explanation.RootSyncWrapper != null ? "sync wrapper" : "async root";
                Console.WriteLine($"Call Chain (from this method to the {rootLabel}):");
                Console.WriteLine();

                for (int i = 0; i < explanation.CallChain.Count; i++)
                {
                    var step = explanation.CallChain[i];
                    var indent = new string(' ', i * 2);

                    Console.Write($"{indent}");
                    if (i == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write("[THIS METHOD] ");
                        Console.ResetColor();
                    }

                    Console.WriteLine($"{step.ContainingType}.{step.MethodName}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}  {step.FilePath}:{step.LineNumber}");
                    Console.ResetColor();

                    if (i < explanation.CallChain.Count - 1)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkYellow;
                        Console.WriteLine($"{indent}  └── calls ──▶");
                        Console.ResetColor();
                    }
                }

                // Print the sync wrapper root
                if (explanation.RootSyncWrapper != null)
                {
                    var indent = new string(' ', explanation.CallChain.Count * 2);
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"{new string(' ', (explanation.CallChain.Count - 1) * 2)}  └── calls ──▶");
                    Console.ResetColor();

                    Console.Write($"{indent}");
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write("[SYNC WRAPPER ROOT] ");
                    Console.ResetColor();
                    Console.WriteLine($"{explanation.RootSyncWrapper.ContainingType}.{explanation.RootSyncWrapper.MethodName}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}  {explanation.RootSyncWrapper.FilePath}:{explanation.RootSyncWrapper.LineNumber}");
                    if (!string.IsNullOrEmpty(explanation.RootSyncWrapper.PatternDescription))
                    {
                        Console.WriteLine($"{indent}  {explanation.RootSyncWrapper.PatternDescription}");
                    }
                    Console.ResetColor();
                }

                if (explanation.RootAsyncMethod != null)
                {
                    var indent = new string(' ', explanation.CallChain.Count * 2);
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"{new string(' ', (explanation.CallChain.Count - 1) * 2)}  └── calls ──▶");
                    Console.ResetColor();

                    Console.Write($"{indent}");
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Console.Write("[ASYNC ROOT] ");
                    Console.ResetColor();
                    Console.WriteLine($"{explanation.RootAsyncMethod.ContainingType}.{explanation.RootAsyncMethod.MethodName}");
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"{indent}  {explanation.RootAsyncMethod.FilePath}:{explanation.RootAsyncMethod.LineNumber}");
                    Console.ResetColor();
                }
            }
            else if (explanation.RootSyncWrapper != null)
            {
                Console.WriteLine("This method directly uses a sync wrapper:");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"  {explanation.RootSyncWrapper.ContainingType}.{explanation.RootSyncWrapper.MethodName}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {explanation.RootSyncWrapper.FilePath}:{explanation.RootSyncWrapper.LineNumber}");
                Console.ResetColor();
            }
            else if (explanation.RootAsyncMethod != null)
            {
                Console.WriteLine("This method directly calls an async root:");
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine($"  {explanation.RootAsyncMethod.ContainingType}.{explanation.RootAsyncMethod.MethodName}");
                Console.ResetColor();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {explanation.RootAsyncMethod.FilePath}:{explanation.RootAsyncMethod.LineNumber}");
                Console.ResetColor();
            }

            if (explanation.InterfacePropagation.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Interface Propagation:");

                foreach (var interfaceInfo in explanation.InterfacePropagation)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  Interface: {interfaceInfo.InterfaceMethod.ContainingType}.{interfaceInfo.InterfaceMethod.MethodName}");
                    Console.ResetColor();

                    if (!string.IsNullOrEmpty(interfaceInfo.Reason))
                    {
                        Console.WriteLine($"    Reason: {interfaceInfo.Reason}");
                    }

                    Console.WriteLine("    Implementations:");
                    foreach (var implementation in interfaceInfo.Implementations)
                    {
                        Console.WriteLine($"      - {implementation.ContainingType}.{implementation.MethodName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error explaining method: {ex.Message}");
            Console.ResetColor();
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

    static void PrintTransformationJobStatus(JobStatusResponse status, bool appliedChanges)
    {
        Console.WriteLine("Transformation Status:");
        Console.WriteLine($"  Job ID: {status.JobId}");
        Console.WriteLine($"  Status: {status.Status}");
        Console.WriteLine($"  Progress: {status.ProgressPercentage}%");
        Console.WriteLine($"  Current Step: {status.CurrentStep}");

        if (status.TotalFileCount.HasValue)
        {
            Console.WriteLine($"  Files: {status.TransformedFileCount ?? 0} / {status.TotalFileCount}");
        }

        if (!string.IsNullOrWhiteSpace(status.CurrentFile))
        {
            Console.WriteLine($"  Current File: {status.CurrentFile}");
        }

        if (status.CompletedAt.HasValue)
        {
            Console.WriteLine($"  Completed At: {status.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC");
        }

        Console.ForegroundColor = appliedChanges ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(appliedChanges
            ? "  Changes were applied to disk"
            : "  Preview only (no files written)");
        Console.ResetColor();
    }
}

// DTOs matching the server
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

public class AsyncExplanationStep
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string Relationship { get; set; } = string.Empty;
}

public class SyncWrapperInfo
{
    public string MethodId { get; set; } = string.Empty;
    public string MethodName { get; set; } = string.Empty;
    public string ContainingType { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? LineNumber { get; set; }
    public string? PatternDescription { get; set; }
}
