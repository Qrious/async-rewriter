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

        rootCommand.AddCommand(analyzeCommand);
        rootCommand.AddCommand(statusCommand);
        rootCommand.AddCommand(cancelCommand);

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

        if (status.Result != null)
        {
            Console.WriteLine();
            Console.WriteLine("Result:");
            var json = JsonSerializer.Serialize(status.Result, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            Console.WriteLine(json);
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
