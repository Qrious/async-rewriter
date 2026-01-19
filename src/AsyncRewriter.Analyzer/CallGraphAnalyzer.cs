using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Analyzes C# code using Roslyn to build a method call graph
/// </summary>
public class CallGraphAnalyzer : ICallGraphAnalyzer
{
    public async Task<CallGraph> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var sourceCode = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await AnalyzeSourceAsync(sourceCode, filePath, cancellationToken);
    }

    public Task<CallGraph> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return AnalyzeProjectAsync(projectPath, null, cancellationToken);
    }

    public async Task<CallGraph> AnalyzeProjectAsync(
        string projectPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default)
    {
        // If a solution file is provided, delegate to AnalyzeSolutionAsync
        if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return await AnalyzeSolutionAsync(projectPath, externalSyncWrapperMethods, cancellationToken);
        }

        var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = project.Name
        };

        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            throw new InvalidOperationException("Failed to get compilation");
        }

        // Process syntax trees in parallel
        await Parallel.ForEachAsync(
            compilation.SyntaxTrees,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            async (syntaxTree, ct) =>
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(ct);

                AnalyzeSyntaxTree(root, semanticModel, syntaxTree.FilePath, callGraph);
            });

        return callGraph;
    }

    public Task<CallGraph> AnalyzeSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return AnalyzeSolutionAsync(solutionPath, null, cancellationToken);
    }

    public async Task<CallGraph> AnalyzeSolutionAsync(
        string solutionPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default)
    {
        var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = Path.GetFileNameWithoutExtension(solutionPath)
        };

        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);

        // Process all projects in the solution
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue; // Skip projects that fail to compile
            }

            // Process syntax trees in parallel
            await Parallel.ForEachAsync(
                compilation.SyntaxTrees,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                },
                async (syntaxTree, ct) =>
                {
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync(ct);

                    AnalyzeSyntaxTree(root, semanticModel, syntaxTree.FilePath, callGraph);
                });
        }

        return callGraph;
    }

    public async Task<CallGraph> AnalyzeSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: fileName, cancellationToken: cancellationToken);

        // Get references from runtime directory to resolve BCL types
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location), // System.Linq
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll"))
        };

        var compilation = CSharpCompilation.Create("Analysis")
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = "InlineAnalysis"
        };

        AnalyzeSyntaxTree(root, semanticModel, fileName, callGraph);

        return callGraph;
    }

    public async Task<CallGraph> AnalyzeSourceAsync(
        string sourceCode,
        IEnumerable<string>? externalSyncWrapperMethods,
        string fileName = "source.cs",
        CancellationToken cancellationToken = default)
    {
        var callGraph = await AnalyzeSourceAsync(sourceCode, fileName, cancellationToken);
        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);
        return callGraph;
    }

    private void ApplyExternalSyncWrapperMethods(CallGraph callGraph, IEnumerable<string>? externalSyncWrapperMethods)
    {
        if (externalSyncWrapperMethods == null)
        {
            return;
        }

        foreach (var method in externalSyncWrapperMethods)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            var methodId = method.Trim();
            callGraph.SyncWrapperMethods.Add(methodId);

            if (callGraph.Methods.TryGetValue(methodId, out var methodNode))
            {
                methodNode.IsSyncWrapper = true;
            }
        }
    }


    private void AnalyzeSyntaxTree(SyntaxNode root, SemanticModel semanticModel, string filePath, CallGraph callGraph)
    {
        // First pass: collect all method declarations
        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        // Process method declarations in parallel
        Parallel.ForEach(
            methodDeclarations,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            methodDecl =>
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol == null) return;

                var methodNode = CreateMethodNode(methodDecl, methodSymbol, filePath);
                callGraph.AddMethod(methodNode);
            });

        // Collect interface method declarations
        var interfaceDeclarations = root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .ToList();

        foreach (var interfaceDecl in interfaceDeclarations)
        {
            var interfaceSymbol = semanticModel.GetDeclaredSymbol(interfaceDecl);
            if (interfaceSymbol == null) continue;

            foreach (var member in interfaceDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(member);
                if (methodSymbol == null) continue;

                var methodNode = CreateInterfaceMethodNode(member, methodSymbol, filePath);
                callGraph.AddMethod(methodNode);
            }
        }

        // Second pass: analyze method calls in parallel
        Parallel.ForEach(
            methodDeclarations,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            methodDecl =>
            {
                var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (methodSymbol == null) return;

                var callerId = GetMethodId(methodSymbol);

                // Find all invocation expressions in this method
                var invocations = methodDecl.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .ToList();

                foreach (var invocation in invocations)
                {
                    var invokedSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (invokedSymbol == null) continue;

                    var calleeId = GetMethodId(invokedSymbol);

                    // Create a method node for the callee if it doesn't exist
                    // (this handles external method calls)
                    if (!callGraph.Methods.ContainsKey(calleeId))
                    {
                        var calleeNode = CreateMethodNodeFromSymbol(invokedSymbol, "external");
                        callGraph.AddMethod(calleeNode);
                    }

                    if (callGraph.SyncWrapperMethods.Contains(calleeId) &&
                        callGraph.Methods.TryGetValue(calleeId, out var wrapperMethod))
                    {
                        wrapperMethod.IsSyncWrapper = true;
                    }

                    var methodCall = new MethodCall
                    {
                        CallerId = callerId,
                        CalleeId = calleeId,
                        CallerSignature = GetMethodSignature(methodSymbol),
                        CalleeSignature = GetMethodSignature(invokedSymbol),
                        LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        FilePath = filePath
                    };

                    callGraph.AddCall(methodCall);
                }
            });
    }

    private MethodNode CreateMethodNode(MethodDeclarationSyntax methodDecl, IMethodSymbol methodSymbol, string filePath)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();

        // Find interface methods that this method implements
        var implementedInterfaces = new List<string>();

        // Add explicit interface implementations
        foreach (var explicitImpl in methodSymbol.ExplicitInterfaceImplementations)
        {
            implementedInterfaces.Add(GetMethodId(explicitImpl));
        }

        // Check for implicit interface implementations
        var containingType = methodSymbol.ContainingType;
        if (containingType != null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var interfaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                {
                    var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                    if (SymbolEqualityComparer.Default.Equals(implementation, methodSymbol))
                    {
                        implementedInterfaces.Add(GetMethodId(interfaceMember));
                    }
                }
            }
        }

        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsAsync = methodSymbol.IsAsync,
            Signature = GetMethodSignature(methodSymbol),
            SourceCode = methodDecl.ToFullString(),
            ImplementsInterfaceMethods = implementedInterfaces
        };
    }

    private MethodNode CreateMethodNodeFromSymbol(IMethodSymbol methodSymbol, string filePath)
    {
        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            IsAsync = methodSymbol.IsAsync,
            Signature = GetMethodSignature(methodSymbol)
        };
    }

    private MethodNode CreateInterfaceMethodNode(MethodDeclarationSyntax methodDecl, IMethodSymbol methodSymbol, string filePath)
    {
        var lineSpan = methodDecl.GetLocation().GetLineSpan();

        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsAsync = false, // Interface methods cannot have async modifier
            IsInterfaceMethod = true,
            Signature = GetMethodSignature(methodSymbol),
            SourceCode = methodDecl.ToFullString()
        };
    }

    private string GetMethodId(IMethodSymbol methodSymbol)
    {
        // Use OriginalDefinition to get the uninstantiated generic method
        // This ensures Query<User> and Query<T> have the same ID
        var originalMethod = methodSymbol.OriginalDefinition;
        return $"{originalMethod.ContainingType?.ToDisplayString()}.{GetMethodSignature(originalMethod)}";
    }

    private string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        // Use OriginalDefinition to get uninstantiated parameter types
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{originalMethod.Name}({parameters})";
    }

    public async Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var results = new List<SyncWrapperMethod>();
        var workspace = MSBuildWorkspace.Create();

        // Handle solution files
        if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await workspace.OpenSolutionAsync(projectPath, cancellationToken: cancellationToken);

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync(cancellationToken);

                    var syncWrappers = FindSyncWrappersInSyntaxTree(root, semanticModel, syntaxTree.FilePath);
                    results.AddRange(syncWrappers);
                }
            }

            return results;
        }

        // Handle single project files
        var proj = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);

        var comp = await proj.GetCompilationAsync(cancellationToken);
        if (comp == null)
        {
            throw new InvalidOperationException("Failed to get compilation");
        }

        foreach (var syntaxTree in comp.SyntaxTrees)
        {
            var semanticModel = comp.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            var syncWrappers = FindSyncWrappersInSyntaxTree(root, semanticModel, syntaxTree.FilePath);
            results.AddRange(syncWrappers);
        }

        return results;
    }

    public async Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsInSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: fileName, cancellationToken: cancellationToken);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
        };

        var compilation = CSharpCompilation.Create("TempAssembly")
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        return FindSyncWrappersInSyntaxTree(root, semanticModel, fileName);
    }

    private List<SyncWrapperMethod> FindSyncWrappersInSyntaxTree(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var results = new List<SyncWrapperMethod>();

        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        foreach (var methodDecl in methodDeclarations)
        {
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
            if (methodSymbol == null) continue;

            var syncWrapperInfo = AnalyzeForSyncWrapperPattern(methodSymbol);
            if (syncWrapperInfo != null)
            {
                var lineSpan = methodDecl.GetLocation().GetLineSpan();
                results.Add(new SyncWrapperMethod
                {
                    MethodId = GetMethodId(methodSymbol),
                    Name = methodSymbol.Name,
                    ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Signature = GetMethodSignature(methodSymbol),
                    PatternDescription = syncWrapperInfo
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes a method to determine if it follows a sync-over-async wrapper pattern.
    /// Returns a description of the pattern if found, null otherwise.
    /// </summary>
    private string? AnalyzeForSyncWrapperPattern(IMethodSymbol methodSymbol)
    {
        // Look for parameters that are Func<Task> or Func<Task<TResult>>
        foreach (var parameter in methodSymbol.Parameters)
        {
            var paramType = parameter.Type;

            // Check if parameter is a Func type
            if (paramType is not INamedTypeSymbol namedType)
                continue;

            if (!namedType.Name.StartsWith("Func") || !namedType.IsGenericType)
                continue;

            var typeArgs = namedType.TypeArguments;
            if (typeArgs.Length == 0)
                continue;

            // Get the last type argument (return type of the Func)
            var funcReturnType = typeArgs[typeArgs.Length - 1];

            // Check if the Func returns Task or Task<T>
            if (!IsTaskType(funcReturnType, out var taskResultType))
                continue;

            // Now check if the method's return type matches the pattern
            var methodReturnType = methodSymbol.ReturnType;

            // Pattern 1: Func<Task> parameter with void return
            if (taskResultType == null && methodReturnType.SpecialType == SpecialType.System_Void)
            {
                return $"Method has Func<Task> parameter '{parameter.Name}' and returns void - sync wrapper pattern";
            }

            // Pattern 2: Func<Task<TResult>> parameter with TResult return
            if (taskResultType != null)
            {
                // Check if the return type matches the Task's result type
                if (SymbolEqualityComparer.Default.Equals(methodReturnType, taskResultType))
                {
                    return $"Method has Func<Task<{taskResultType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>> parameter '{parameter.Name}' and returns {methodReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} - sync wrapper pattern";
                }

                // Also check for type parameter match (generic methods like Execute<TResult>(Func<Task<TResult>>))
                if (methodReturnType is ITypeParameterSymbol returnTypeParam &&
                    taskResultType is ITypeParameterSymbol taskTypeParam &&
                    returnTypeParam.Name == taskTypeParam.Name)
                {
                    return $"Method has Func<Task<{taskResultType.Name}>> parameter '{parameter.Name}' and returns {methodReturnType.Name} - generic sync wrapper pattern";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a type is Task or Task&lt;T&gt; and extracts the result type if applicable
    /// </summary>
    private bool IsTaskType(ITypeSymbol type, out ITypeSymbol? resultType)
    {
        resultType = null;

        if (type is not INamedTypeSymbol namedType)
            return false;

        var fullName = namedType.ToDisplayString();

        // Check for Task<T>
        if (fullName.StartsWith("System.Threading.Tasks.Task<") ||
            (namedType.Name == "Task" && namedType.TypeArguments.Length == 1))
        {
            if (namedType.TypeArguments.Length == 1)
            {
                resultType = namedType.TypeArguments[0];
                return true;
            }
        }

        // Check for Task (non-generic)
        if (fullName == "System.Threading.Tasks.Task" ||
            (namedType.Name == "Task" && namedType.TypeArguments.Length == 0))
        {
            return true;
        }

        return false;
    }
}
