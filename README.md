# Async Rewriter

A C# Roslyn-based server for efficiently transforming synchronous methods to async by analyzing the call graph and determining the "flooding" of async/await changes needed throughout the codebase.

## Features

- **Call Graph Analysis**: Uses Roslyn to build a comprehensive method call graph
- **Neo4j Storage**: Stores the call graph in Neo4j for efficient querying and visualization
- **Async Flooding Analysis**: Determines which methods need to be async based on call dependencies
- **Automated Transformation**: Automatically rewrites code to add async/await keywords and transform return types
- **REST API**: Provides a complete API for analysis and transformation
- **Batch Processing**: Outputs all modified files at once for review before applying

## Architecture

The solution is divided into several projects:

- **AsyncRewriter.Core**: Core models and interfaces
- **AsyncRewriter.Analyzer**: Roslyn-based call graph analyzer and flooding analyzer
- **AsyncRewriter.Neo4j**: Neo4j repository for call graph storage
- **AsyncRewriter.Transformation**: Roslyn syntax rewriter for code transformation
- **AsyncRewriter.Server**: ASP.NET Core Web API

## Prerequisites

- .NET 8.0 SDK
- Neo4j 5.15.0 or later
- Docker and Docker Compose (optional, for running Neo4j)

## Getting Started

### 1. Start Neo4j

Using Docker Compose:

```bash
docker-compose up neo4j -d
```

Or install Neo4j locally and ensure it's running on `bolt://localhost:7687`.

Access Neo4j Browser at http://localhost:7474
- Username: neo4j
- Password: password

### 2. Configure the Server

Edit `src/AsyncRewriter.Server/appsettings.json` to configure Neo4j connection:

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "password",
    "Database": "neo4j"
  }
}
```

### 3. Build and Run

```bash
dotnet build
dotnet run --project src/AsyncRewriter.Server
```

The API will be available at `http://localhost:5000`

Swagger documentation: `http://localhost:5000/swagger`

### 4. Using Docker

To run the entire stack (API + Neo4j):

```bash
docker-compose up --build
```

## API Usage

### 1. Analyze a Project

Analyzes a C# project and builds a call graph:

```bash
POST /api/asynctransformation/analyze/project
Content-Type: application/json

{
  "projectPath": "/path/to/your/project.csproj"
}
```

Response includes the complete call graph with a unique `id`.

### 2. Analyze Source Code

Analyzes C# source code directly:

```bash
POST /api/asynctransformation/analyze/source
Content-Type: application/json

{
  "sourceCode": "public class MyClass { public void MyMethod() { } }",
  "fileName": "MyClass.cs"
}
```

### 3. Analyze Async Flooding

Determines which methods need to be async based on root methods:

```bash
POST /api/asynctransformation/analyze/flooding
Content-Type: application/json

{
  "callGraphId": "abc-123-def",
  "rootMethodIds": [
    "MyNamespace.MyClass.MyAsyncMethod(string)",
    "MyNamespace.MyClass.AnotherAsyncMethod(int)"
  ]
}
```

The response includes:
- Updated call graph with `floodedMethods` (methods that need to be async)
- `requiresAsyncTransformation` flag set on affected methods
- `requiresAwait` flag set on call relationships

### 4. Get Transformation Details

Get detailed information about what changes are needed:

```bash
GET /api/asynctransformation/callgraph/{id}/transformations
```

Returns:
- Methods to transform
- Original and new return types
- Call sites that need await

### 5. Transform Project

Transform the entire project:

```bash
POST /api/asynctransformation/transform/project
Content-Type: application/json

{
  "projectPath": "/path/to/your/project.csproj",
  "callGraphId": "abc-123-def",
  "applyChanges": false
}
```

Set `applyChanges: true` to automatically write the transformed code to files.

Response includes:
- All modified files with original and transformed content
- Total methods transformed
- Total call sites transformed
- Success/error status

### 6. Transform Source Code

Transform source code directly:

```bash
POST /api/asynctransformation/transform/source
Content-Type: application/json

{
  "sourceCode": "public class MyClass { public void MyMethod() { } }",
  "methodsToTransform": ["MyClass.MyMethod()"]
}
```

### 7. Query Call Graph

Find all callers of a method:

```bash
GET /api/asynctransformation/callgraph/{id}/callers/{methodId}?depth=2
```

Find all methods called by a method:

```bash
GET /api/asynctransformation/callgraph/{id}/callees/{methodId}?depth=2
```

## Example Workflow

Here's a complete example of transforming a method to async:

```csharp
// Original code
public class DataService
{
    public string GetData()
    {
        var result = DatabaseHelper.FetchData();
        return result;
    }
}

public class DatabaseHelper
{
    public static string FetchData()
    {
        // Synchronous database call
        return Database.Query("SELECT * FROM table");
    }
}
```

**Step 1**: Analyze the project

```bash
curl -X POST http://localhost:5000/api/asynctransformation/analyze/project \
  -H "Content-Type: application/json" \
  -d '{"projectPath": "/path/to/project.csproj"}'
```

**Step 2**: Identify root methods (methods that should be async)

In this case, `DatabaseHelper.FetchData()` should call async database operations.

**Step 3**: Analyze flooding

```bash
curl -X POST http://localhost:5000/api/asynctransformation/analyze/flooding \
  -H "Content-Type: application/json" \
  -d '{
    "callGraphId": "your-callgraph-id",
    "rootMethodIds": ["DatabaseHelper.FetchData()"]
  }'
```

This determines that `DataService.GetData()` also needs to be async (flooding effect).

**Step 4**: Transform the project

```bash
curl -X POST http://localhost:5000/api/asynctransformation/transform/project \
  -H "Content-Type: application/json" \
  -d '{
    "projectPath": "/path/to/project.csproj",
    "callGraphId": "your-callgraph-id",
    "applyChanges": false
  }'
```

**Result**:

```csharp
// Transformed code
using System.Threading.Tasks;

public class DataService
{
    public async Task<string> GetData()
    {
        var result = await DatabaseHelper.FetchData();
        return result;
    }
}

public class DatabaseHelper
{
    public static async Task<string> FetchData()
    {
        // Asynchronous database call
        return await Database.QueryAsync("SELECT * FROM table");
    }
}
```

## Neo4j Graph Visualization

You can visualize the call graph in Neo4j Browser:

```cypher
// View all methods
MATCH (m:Method) RETURN m LIMIT 25

// View call relationships
MATCH (caller:Method)-[c:CALLS]->(callee:Method)
RETURN caller, c, callee LIMIT 50

// Find methods that need async transformation
MATCH (m:Method)
WHERE m.requiresAsyncTransformation = true
RETURN m

// Find all methods in the async flood chain
MATCH path = (root:Method)<-[:CALLS*]-(flooded:Method)
WHERE root.id IN ['your-root-method-id']
RETURN path
```

## How It Works

### 1. Call Graph Building

The analyzer uses Roslyn to:
- Parse all C# files in the project
- Extract method declarations
- Identify method invocations
- Build relationships between callers and callees

### 2. Async Flooding

The flooding analyzer uses BFS (Breadth-First Search) to:
- Start from root methods that need to be async
- Traverse up the call graph to find all callers
- Mark each caller as needing async transformation
- Continue until reaching entry points or boundaries

### 3. Code Transformation

The transformer uses Roslyn's `CSharpSyntaxRewriter` to:
- Add `async` keyword to method declarations
- Transform return types (`T` → `Task<T>`, `void` → `Task`)
- Add `await` keyword to async method calls
- Add `using System.Threading.Tasks;` if needed

## Configuration

### Neo4j Options

```json
{
  "Neo4j": {
    "Uri": "bolt://localhost:7687",
    "Username": "neo4j",
    "Password": "password",
    "Database": "neo4j"
  }
}
```

## Limitations

- Only analyzes methods within the same solution (external library calls are tracked but not transformed)
- Does not handle all edge cases (e.g., event handlers, LINQ expression trees)
- Requires manual review of transformations before applying
- Does not automatically transform `Main` methods or ASP.NET Core action methods (requires manual handling)

## Future Enhancements

- Support for lambda expressions and local functions
- Integration with MSBuild for automated project transformation
- CLI tool for command-line usage
- Visual Studio extension
- Support for ConfigureAwait(false)
- Deadlock detection and prevention analysis

## Contributing

Contributions are welcome! Please submit issues and pull requests.

## License

MIT License
