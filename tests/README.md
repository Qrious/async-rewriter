# AsyncRewriter Tests

This directory contains comprehensive unit tests and integration tests for the AsyncRewriter project.

## Test Projects

### AsyncRewriter.Tests (Unit Tests)

Unit tests for individual components of the async-rewriter system.

**Test Classes:**
- `CallGraphAnalyzerTests.cs` - Tests for the Roslyn-based call graph analysis
- `AsyncFloodingAnalyzerTests.cs` - Tests for the BFS async flooding algorithm
- `AsyncTransformerTests.cs` - Tests for the code transformation logic
- `JobServiceTests.cs` - Tests for the job queue and management service

**Key Test Scenarios:**
- Simple method detection and analysis
- Method parameter and return type capture
- Async method identification
- Call graph construction
- Chained method calls
- External method calls
- Flooding with different return types (void → Task, T → Task<T>)
- Diamond dependencies
- Job creation, cancellation, and status updates

### AsyncRewriter.IntegrationTests (Integration Tests)

End-to-end integration tests using realistic code scenarios.

**Test Classes:**
- `InterfaceImplementationTests.cs` - Tests for interface implementation scenarios
- `InheritanceScenarioTests.cs` - Tests for inheritance hierarchies
- `MultipleInheritorsTests.cs` - Tests for interfaces with multiple implementations
- `EndToEndTests.cs` - Complete workflow tests from analysis to transformation

## Test Scenarios

### 1. Interface Implementation Scenario

Tests the transformation of methods that implement interface contracts:
- Method in interface implementation that calls async operations
- Controller calling interface methods
- Multiple interface methods with independent async paths

**Example:**
```csharp
public interface IService
{
    string GetData();
}

public class ServiceImplementation : IService
{
    public string GetData() => FetchFromDatabase(); // Should become async
    private string FetchFromDatabase() { } // Root async method
}
```

### 2. Inheritance Scenario

Tests transformation through inheritance hierarchies:
- Base class methods calling async operations
- Derived classes overriding and calling base methods
- Multiple inheritors with different override patterns
- Chained base calls across multiple levels

**Example:**
```csharp
public abstract class BaseRepository
{
    public virtual string GetById(int id) => LoadFromStorage(id);
    protected string LoadFromStorage(int id) { } // Root async
}

public class UserRepository : BaseRepository
{
    public override string GetById(int id) => base.GetById(id); // Should become async
}
```

### 3. Multiple Inheritors Scenario

Tests scenarios where multiple classes implement the same interface:
- Interface with 3+ implementations
- Aggregator pattern calling all implementations
- Selective flooding (only some implementations have async roots)
- Complex hierarchies with both interface and inheritance

**Example:**
```csharp
public interface IDataProvider
{
    string FetchData();
}

public class DatabaseProvider : IDataProvider
{
    public string FetchData() => QueryDatabase(); // Async root
}

public class ApiProvider : IDataProvider
{
    public string FetchData() => CallExternalApi(); // Async root
}

public class FileProvider : IDataProvider
{
    public string FetchData() => ReadFromFile(); // Async root
}
```

### 4. Complex Hierarchy Scenario

Tests complex inheritance and interface combinations:
- Interface + abstract base class + concrete implementations
- Abstract methods in base class
- Shared helper methods
- Multiple inheritance levels

**Example:**
```csharp
public interface IMessageHandler
{
    void HandleMessage(string message);
}

public abstract class BaseMessageHandler : IMessageHandler
{
    public virtual void HandleMessage(string message)
    {
        ValidateMessage(message);
        ProcessMessage(message);
        LogMessage(message);
    }

    protected abstract void ProcessMessage(string message);
}

public class EmailMessageHandler : BaseMessageHandler
{
    protected override void ProcessMessage(string message) => SendEmail(message);
}
```

### 5. Deep Call Chain Scenario

Tests flooding through deep call chains:
- 8+ levels of method calls
- Each level calling the next
- Root async method at the deepest level
- All levels should be flooded

## Running the Tests

### Build the test projects:
```bash
dotnet build tests/AsyncRewriter.Tests/AsyncRewriter.Tests.csproj
dotnet build tests/AsyncRewriter.IntegrationTests/AsyncRewriter.IntegrationTests.csproj
```

### Run unit tests:
```bash
dotnet test tests/AsyncRewriter.Tests/AsyncRewriter.Tests.csproj
```

### Run integration tests:
```bash
dotnet test tests/AsyncRewriter.IntegrationTests/AsyncRewriter.IntegrationTests.csproj
```

### Run all tests:
```bash
dotnet test
```

### Run with coverage:
```bash
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

## Test Coverage

The test suite covers:
- ✅ Call graph analysis from source code
- ✅ Method declaration parsing
- ✅ Method invocation detection
- ✅ Return type transformation (void → Task, T → Task<T>)
- ✅ Async flooding with BFS algorithm
- ✅ Code transformation with Roslyn
- ✅ Interface implementation scenarios
- ✅ Inheritance hierarchies
- ✅ Multiple inheritors
- ✅ Complex hierarchies
- ✅ Deep call chains
- ✅ Job queue management
- ✅ Job cancellation
- ✅ End-to-end workflows

## Notes

- All unit tests use mocking (Moq) for dependencies
- Integration tests use real analyzer and transformer instances
- Test scenarios are based on common real-world patterns
- Tests verify both the call graph structure and the transformed output
