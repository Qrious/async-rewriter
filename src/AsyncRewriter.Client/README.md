# Async Rewriter Client

A command-line interface (CLI) client for the Async Rewriter API. This tool allows you to analyze C# codebases for async transformation from your terminal.

## Features

- Start async analysis jobs for C# projects
- Monitor job progress with real-time updates
- Check job status on demand
- Cancel running jobs
- Configurable API endpoint

## Prerequisites

- .NET 8.0 SDK or later
- Async Rewriter API server running (default: http://localhost:5000)

## Installation

```bash
cd src/AsyncRewriter.Client
dotnet build
```

## Usage

### Basic Commands

#### Analyze a Project

Start an analysis job for a C# project:

```bash
dotnet run -- analyze /path/to/your/project.csproj
```

This will:
1. Submit the analysis job to the API
2. Display the job ID
3. Wait for completion and show progress

#### Analyze Without Waiting

Start an analysis but return immediately:

```bash
dotnet run -- analyze /path/to/your/project.csproj --wait false
```

#### Custom API Server URL

If your API server is running on a different address:

```bash
dotnet run -- --base-url http://your-server:port analyze /path/to/project.csproj
```

#### Check Job Status

Check the status of a specific job:

```bash
dotnet run -- status <job-id>
```

Example:
```bash
dotnet run -- status abc123-def456-ghi789
```

#### Cancel a Job

Cancel a running or queued job:

```bash
dotnet run -- cancel <job-id>
```

### Advanced Options

#### Custom Polling Interval

Change how often the client checks for updates (in milliseconds):

```bash
dotnet run -- analyze /path/to/project.csproj --poll-interval 1000
```

### Help

Display help information:

```bash
dotnet run -- --help
dotnet run -- analyze --help
dotnet run -- status --help
```

## Examples

### Example 1: Analyze with Progress Display

```bash
$ dotnet run -- analyze /home/user/myproject/MyProject.csproj

Starting analysis for project: /home/user/myproject/MyProject.csproj

✓ Job created successfully!
Job ID: 7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d
Status: Queued
Message: Analysis job has been queued and will be processed in the background

Waiting for analysis to complete...

⠋ Processing - 60% - Building call graph
```

### Example 2: Check Status Later

```bash
$ dotnet run -- analyze /path/to/project.csproj --wait false

✓ Job created successfully!
Job ID: 7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d
Status: Queued
Use 'status 7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d' to check the progress

$ dotnet run -- status 7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d

Job Status:
  Job ID: 7a8b9c0d-1e2f-3a4b-5c6d-7e8f9a0b1c2d
  Status: Completed
  Progress: 100%
  Current Step: Analysis complete
  Created At: 2024-01-15 10:30:45 UTC
  Started At: 2024-01-15 10:30:46 UTC
  Completed At: 2024-01-15 10:32:15 UTC

Result:
{
  "id": "callgraph-123",
  "projectName": "MyProject",
  "methods": {...}
}
```

### Example 3: Using with Different Server

```bash
dotnet run -- --base-url https://api.example.com analyze /path/to/project.csproj
```

## Building for Production

Create a standalone executable:

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

Then run:

```bash
./bin/Release/net8.0/linux-x64/publish/AsyncRewriter.Client analyze /path/to/project.csproj
```

## Troubleshooting

### Connection Refused

If you see "Error connecting to API", make sure:
1. The API server is running
2. The URL is correct (use `--base-url` to specify)
3. No firewall is blocking the connection

### Job Not Found

If a job is not found:
- Double-check the job ID
- The server may have restarted (jobs are stored in memory)

## Integration with CI/CD

You can use this client in your CI/CD pipelines:

```bash
#!/bin/bash

# Start analysis
OUTPUT=$(dotnet run --project AsyncRewriter.Client -- analyze ./MyProject.csproj --wait false)
JOB_ID=$(echo "$OUTPUT" | grep "Job ID:" | awk '{print $3}')

# Poll for completion
while true; do
  STATUS=$(dotnet run --project AsyncRewriter.Client -- status $JOB_ID)
  if echo "$STATUS" | grep -q "Status: Completed"; then
    echo "Analysis complete!"
    break
  elif echo "$STATUS" | grep -q "Status: Failed"; then
    echo "Analysis failed!"
    exit 1
  fi
  sleep 5
done
```
