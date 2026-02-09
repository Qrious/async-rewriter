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

# Run the console tool
dotnet run --project src/AsyncRewriter.Console -- <command>

# Example: Analyze a project
dotnet run --project src/AsyncRewriter.Console -- analyze MyProject.csproj

# Example: Find sync wrappers and transform
dotnet run --project src/AsyncRewriter.Console -- find-sync-wrappers MyProject.csproj --analyze --apply
```

## Architecture

This is a Roslyn-based tool for transforming synchronous C# methods to async by analyzing call graphs and determining "flooding" - which methods need async/await based on call dependencies.

### Projects

- **AsyncRewriter.Core**: Domain models (`CallGraph`, `MethodNode`, `MethodCall`) and interfaces (`ICallGraphAnalyzer`, `IAsyncFloodingAnalyzer`, `IAsyncTransformer`)
- **AsyncRewriter.Analyzer**: Roslyn-based implementation. `CallGraphAnalyzer` builds method call graphs from C# projects. `AsyncFloodingAnalyzer` uses BFS to determine which methods need async transformation starting from root methods.
- **AsyncRewriter.Transformation**: `AsyncTransformer` and `AsyncMethodRewriter` (CSharpSyntaxRewriter) handle code transformation - adding async keywords, transforming return types (T → Task<T>, void → Task), and inserting await
- **AsyncRewriter.Console**: Command-line interface that directly uses the analyzer and transformation services

### Data Flow

1. **Analysis**: `CallGraphAnalyzer` parses a .csproj, extracts method declarations and invocations using Roslyn
2. **Storage**: Call graph stored as JSON file for persistence between commands
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
