# Async Rewriter Client

A command-line interface (CLI) client for the Async Rewriter API. This tool allows you to analyze C# codebases for async transformation from your terminal.

## Features

- Start async analysis jobs for C# projects
- Monitor job progress with real-time updates
- Check job status on demand
- Cancel running jobs
- Find sync-over-async wrapper methods
- Transform projects from sync to async
- Apply code transformations automatically
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

#### Find Sync Wrapper Methods

Find sync-over-async wrapper methods in a project:

```bash
dotnet run -- find-sync-wrappers /path/to/your/project.csproj
```

This command identifies methods that wrap async operations in a synchronous manner, which are common candidates for async transformation.

##### Find and Analyze

Automatically run async flooding analysis from the found sync wrappers:

```bash
dotnet run -- find-sync-wrappers /path/to/your/project.csproj --analyze
```

##### Find, Analyze, and Apply

Automatically find sync wrappers, analyze, and apply transformations:

```bash
dotnet run -- find-sync-wrappers /path/to/your/project.csproj --analyze --apply
```

**Warning:** The `--apply` flag will modify your source files. Make sure you have committed your changes or have a backup before using this option.

##### Interface Mappings

Specify interface mappings to replace synchronous interfaces with asynchronous versions:

```bash
dotnet run -- find-sync-wrappers /path/to/your/project.csproj --analyze --interface-mapping IRepository=IRepositoryAsync --interface-mapping IDataService=IDataServiceAsync
```

This option is useful when you want to map methods implementing synchronous interfaces to asynchronous interface equivalents during transformation. Multiple mappings can be specified using multiple `--interface-mapping` (or `-im`) flags.

#### Transform a Project

Transform a project from sync to async based on a previously analyzed call graph:

```bash
dotnet run -- transform /path/to/your/project.csproj <call-graph-id>
```

This will generate a preview of the transformations without modifying files.

##### Apply Transformations

To actually apply the transformations to your source files:

```bash
dotnet run -- transform /path/to/your/project.csproj <call-graph-id> --apply
```

**Warning:** The `--apply` flag will modify your source files. Make sure you have committed your changes or have a backup before using this option.

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
dotnet run -- cancel --help
dotnet run -- find-sync-wrappers --help
dotnet run -- transform --help
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

### Example 4: Find and Transform Sync Wrappers

```bash
$ dotnet run -- find-sync-wrappers /home/user/myproject/MyProject.csproj --analyze

Finding sync wrapper methods in project: /home/user/myproject/MyProject.csproj

Found 2 sync wrapper(s), 15 method(s) need async transformation

Sync Wrapper Methods (Root Methods for Transformation):
  - MyNamespace.DataService.GetDataSync(Func<Task<string>> fetchAsync)
    Method with Func<Task<TResult>> parameter that returns TResult

  - MyNamespace.DataService.ExecuteSync(Func<Task> action)
    Method with Func<Task> parameter that returns void

Methods requiring async transformation (15):
  - MyNamespace.DataService.GetDataSync
    Current: string -> Will become: Task<string>
  - MyNamespace.DataService.ExecuteSync
    Current: void -> Will become: Task
  ...

Call Graph ID: callgraph-abc123
Use this ID with the transform command to apply the changes.
```

### Example 5: Preview and Apply Transformations

```bash
# First, preview the transformations
$ dotnet run -- transform /home/user/myproject/MyProject.csproj callgraph-abc123

Transforming project: /home/user/myproject/MyProject.csproj
Call Graph ID: callgraph-abc123
Apply Changes: False

✓ Transformation preview generated successfully!

Files modified: 3

  /home/user/myproject/DataService.cs
    Methods transformed: 5
    Await keywords added: 8
    Transformed methods:
      - GetDataSync
      - ExecuteSync
      - HelperMethod1
      - HelperMethod2
      - ProcessData

Note: Changes have not been applied to the files.
To apply the changes, use the --apply flag.

# Then apply the changes
$ dotnet run -- transform /home/user/myproject/MyProject.csproj callgraph-abc123 --apply

Transforming project: /home/user/myproject/MyProject.csproj
Call Graph ID: callgraph-abc123
Apply Changes: True

✓ Transformation applied successfully!

Files modified: 3
...

✓ Changes have been written to 3 file(s).
```

### Example 6: One-Command Transform

```bash
# Find sync wrappers, analyze, and apply in one command
$ dotnet run -- find-sync-wrappers /home/user/myproject/MyProject.csproj --analyze --apply

Finding sync wrapper methods in project: /home/user/myproject/MyProject.csproj

Found 2 sync wrapper(s), 15 method(s) need async transformation
...

Applying transformations to 15 method(s)...

Transforming project: /home/user/myproject/MyProject.csproj
Call Graph ID: callgraph-abc123
Apply Changes: True

✓ Transformation applied successfully!
✓ Changes have been written to 3 file(s).
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
