using System;
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

    public async Task<CallGraph> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = project.Name
        };

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

    public async Task<CallGraph> AnalyzeSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: fileName, cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create("TempAssembly")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
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

        return new MethodNode
        {
            Id = GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList(),
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            IsAsync = methodSymbol.IsAsync,
            Signature = GetMethodSignature(methodSymbol),
            SourceCode = methodDecl.ToFullString()
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
            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList(),
            FilePath = filePath,
            IsAsync = methodSymbol.IsAsync,
            Signature = GetMethodSignature(methodSymbol)
        };
    }

    private string GetMethodId(IMethodSymbol methodSymbol)
    {
        // Create a unique ID based on the fully qualified method signature
        return $"{methodSymbol.ContainingType?.ToDisplayString()}.{GetMethodSignature(methodSymbol)}";
    }

    private string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var parameters = string.Join(", ", methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()));
        return $"{methodSymbol.Name}({parameters})";
    }
}
