# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build and Test Commands

```bash
# Build entire solution
dotnet build

# Run all tests
dotnet test

# Run unit tests only
dotnet test tests/AsyncRewriter.Tests

# Run integration tests only
dotnet test tests/AsyncRewriter.IntegrationTests

# Run a single test by name
dotnet test --filter "FullyQualifiedName~TestMethodName"

# Run the server
dotnet run --project src/AsyncRewriter.Server

# Run the CLI client
dotnet run --project src/AsyncRewriter.Client -- <command>
```

## Infrastructure

Neo4j is required for the server. Start it with Docker:
```bash
docker-compose up neo4j -d
```

Neo4j Browser: http://localhost:7474 (neo4j/password)

## Architecture

This is a Roslyn-based tool for transforming synchronous C# methods to async by analyzing call graphs and determining "flooding" - which methods need async/await based on call dependencies.

### Projects

- **AsyncRewriter.Core**: Domain models (`CallGraph`, `MethodNode`, `MethodCall`) and interfaces (`ICallGraphAnalyzer`, `IAsyncFloodingAnalyzer`, `IAsyncTransformer`, `ICallGraphRepository`)
- **AsyncRewriter.Analyzer**: Roslyn-based implementation. `CallGraphAnalyzer` builds method call graphs from C# projects. `AsyncFloodingAnalyzer` uses BFS to determine which methods need async transformation starting from root methods.
- **AsyncRewriter.Neo4j**: `CallGraphRepository` stores/queries call graphs in Neo4j
- **AsyncRewriter.Transformation**: `AsyncTransformer` and `AsyncMethodRewriter` (CSharpSyntaxRewriter) handle code transformation - adding async keywords, transforming return types (T → Task<T>, void → Task), and inserting await
- **AsyncRewriter.Server**: ASP.NET Core API with background job processing via `AnalysisBackgroundService` and `JobService`
- **AsyncRewriter.Client**: CLI for interacting with the API

### Data Flow

1. **Analysis**: `CallGraphAnalyzer` parses a .csproj, extracts method declarations and invocations using Roslyn
2. **Storage**: Call graph stored in Neo4j as Method nodes with CALLS relationships
3. **Flooding**: Given root methods (methods that should be async), `AsyncFloodingAnalyzer` traverses callers via BFS, marking all upstream methods as needing transformation
4. **Transformation**: `AsyncTransformer` rewrites the syntax tree to add async/await keywords and transform return types

### Transformation Optimizations

The `AsyncMethodRewriter` applies intelligent transformations to minimize async overhead:

1. **Direct Task Return**: Methods with a single async call directly return the task instead of using async/await
   ```csharp
   // Before: void Get() { _repo.Connect(); }
   // After:  Task Get() { return _repo.Connect(); }
   ```

2. **Task.FromResult**: Methods marked for transformation but containing no async calls use `Task.FromResult<T>()` or `Task.CompletedTask`
   ```csharp
   // Before: bool IsConnected() { return true; }
   // After:  Task<bool> IsConnected() { return Task.FromResult<bool>(true); }
   ```

3. **Async/Await**: Only used when necessary (multiple statements, result used in computation)

### Key Types

- `CallGraph`: Contains `MethodNode` collection and `MethodCall` relationships
- `MethodNode`: Represents a method with name, return type, parameters, and flags like `RequiresAsyncTransformation`
- `MethodCall`: Represents a caller→callee relationship with `RequiresAwait` flag
- `SyncWrapperMethod`: Identifies sync-over-async patterns (methods with `Func<Task<T>>` parameters returning `T`)
