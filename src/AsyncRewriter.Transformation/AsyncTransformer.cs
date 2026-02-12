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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

public class AsyncTransformer : IAsyncTransformer
{
    public Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        CancellationToken cancellationToken = default)
        => TransformProjectAsync(projectPath, callGraph, null, false, cancellationToken);

    public Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
        => TransformProjectAsync(projectPath, callGraph, progressCallback, false, cancellationToken);

    public async Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        Action<string, int, int>? progressCallback,
        bool debugComments,
        CancellationToken cancellationToken = default)
    {
        var result = new TransformationResult { CallGraph = callGraph };

        try
        {
            // Build transformation info from the flooded call graph
            var transformationsByFile = BuildTransformationsByFile(callGraph);

            var fileCount = 0;
            var totalFiles = transformationsByFile.Count;

            foreach (var (filePath, transformations) in transformationsByFile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileCount++;
                progressCallback?.Invoke(filePath, fileCount, totalFiles);

                if (!File.Exists(filePath))
                {
                    result.Warnings.Add($"File not found: {filePath}");
                    continue;
                }

                var sourceCode = await File.ReadAllTextAsync(filePath, cancellationToken);
                var fileTransformation = await TransformFileInternalAsync(
                    filePath, sourceCode, transformations, callGraph, debugComments, cancellationToken);

                if (fileTransformation != null)
                {
                    result.ModifiedFiles.Add(fileTransformation);
                    result.TotalMethodsTransformed += fileTransformation.MethodTransformations.Count;
                    result.TotalCallSitesTransformed += fileTransformation.MethodTransformations
                        .Sum(m => m.AwaitAddedAtLines.Count);
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
        }

        return result;
    }

    public async Task<FileTransformation> TransformFileAsync(
        string filePath,
        List<AsyncTransformationInfo> transformations,
        HashSet<string>? syncWrapperMethodIds = null,
        HashSet<string>? allAsyncMethodIds = null,
        CancellationToken cancellationToken = default)
    {
        var sourceCode = await File.ReadAllTextAsync(filePath, cancellationToken);
        var transformed = await TransformSourceAsync(
            sourceCode, transformations, syncWrapperMethodIds, allAsyncMethodIds, cancellationToken);

        return new FileTransformation
        {
            FilePath = filePath,
            OriginalContent = sourceCode,
            TransformedContent = transformed,
            MethodTransformations = new List<MethodTransformation>()
        };
    }

    public Task<string> TransformSourceAsync(
        string sourceCode,
        List<AsyncTransformationInfo> transformations,
        HashSet<string>? syncWrapperMethodIds = null,
        HashSet<string>? allAsyncMethodIds = null,
        CancellationToken cancellationToken = default)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot(cancellationToken);

        // Build lookup tables from AsyncTransformationInfo
        var methodsByStartLine = new Dictionary<int, MethodTransformInfo>();
        var callSitesByLine = new Dictionary<int, CallSiteInfo>();

        foreach (var info in transformations)
        {
            // For TransformSourceAsync, we get start line info from the transformation info itself
            // The caller is responsible for providing correct line-based info
            foreach (var callSite in info.CallSitesToTransform)
            {
                if (!callSitesByLine.ContainsKey(callSite.LineNumber))
                {
                    callSitesByLine[callSite.LineNumber] = new CallSiteInfo
                    {
                        CalleeMethodId = info.MethodId,
                        LineNumber = callSite.LineNumber
                    };
                }
            }
        }

        // Walk the syntax tree to find method declarations that match transformation info
        // Match by method name and return type from the transformations
        var methodDeclarations = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        foreach (var methodDecl in methodDeclarations)
        {
            var startLine = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var endLine = methodDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

            var matchingTransform = transformations.FirstOrDefault(t =>
            {
                // Match by looking at call sites within this method's range
                return t.CallSitesToTransform.Any(cs => cs.LineNumber >= startLine && cs.LineNumber <= endLine)
                    || (t.OriginalReturnType != t.NewReturnType && MatchesMethodByContext(methodDecl, t));
            });

            if (matchingTransform != null)
            {
                methodsByStartLine[startLine] = new MethodTransformInfo
                {
                    MethodId = matchingTransform.MethodId,
                    MethodName = methodDecl.Identifier.Text,
                    ContainingType = GetContainingTypeName(methodDecl),
                    OriginalReturnType = matchingTransform.OriginalReturnType,
                    NewReturnType = matchingTransform.NewReturnType,
                    StartLine = startLine,
                    EndLine = endLine
                };
            }
        }

        // Also scan local function declarations
        var localFunctions = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>();
        foreach (var localFunc in localFunctions)
        {
            var startLine = localFunc.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var endLine = localFunc.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

            var matchingTransform = transformations.FirstOrDefault(t =>
            {
                return t.CallSitesToTransform.Any(cs => cs.LineNumber >= startLine && cs.LineNumber <= endLine)
                    || (t.OriginalReturnType != t.NewReturnType && MatchesLocalFunctionByContext(localFunc, t));
            });

            if (matchingTransform != null)
            {
                methodsByStartLine[startLine] = new MethodTransformInfo
                {
                    MethodId = matchingTransform.MethodId,
                    MethodName = localFunc.Identifier.Text,
                    ContainingType = GetContainingTypeName(localFunc),
                    OriginalReturnType = matchingTransform.OriginalReturnType,
                    NewReturnType = matchingTransform.NewReturnType,
                    StartLine = startLine,
                    EndLine = endLine
                };
            }
        }

        var rewriter = new AsyncMethodRewriter(
            methodsByStartLine, callSitesByLine, syncWrapperMethodIds, allAsyncMethodIds);

        var newRoot = rewriter.Visit(root);

        // Add using System.Threading.Tasks if needed and any method was transformed
        if (rewriter.AnyMethodTransformed)
        {
            newRoot = EnsureUsingDirective(newRoot, "System.Threading.Tasks");
        }

        return Task.FromResult(newRoot.ToFullString());
    }

    private Dictionary<string, List<(MethodNode Method, string OriginalReturnType, List<MethodCall> CallsToAwait)>>
        BuildTransformationsByFile(CallGraph callGraph)
    {
        var result = new Dictionary<string, List<(MethodNode, string, List<MethodCall>)>>();

        // Find all methods that need transformation (return type is Task-based)
        var floodedMethodIds = new HashSet<string>();
        foreach (var (id, method) in callGraph.Methods)
        {
            if (IsTaskReturnType(method.ReturnType))
                floodedMethodIds.Add(id);
        }

        foreach (var methodId in floodedMethodIds)
        {
            var method = callGraph.Methods[methodId];
            if (string.IsNullOrEmpty(method.FilePath) || method.FilePath == "external")
                continue;

            // Find calls from this method to other flooded methods that need await
            var callsToAwait = new List<MethodCall>();
            foreach (var call in callGraph.Calls)
            {
                if (call.CallerId == methodId && floodedMethodIds.Contains(call.CalleeId))
                    callsToAwait.Add(call);
            }

            // Determine original return type (reverse the Task transformation)
            var originalReturnType = ReverseTaskReturnType(method.ReturnType);

            if (!result.TryGetValue(method.FilePath, out var list))
            {
                list = new();
                result[method.FilePath] = list;
            }
            list.Add((method, originalReturnType, callsToAwait));
        }

        return result;
    }

    private async Task<FileTransformation?> TransformFileInternalAsync(
        string filePath,
        string sourceCode,
        List<(MethodNode Method, string OriginalReturnType, List<MethodCall> CallsToAwait)> methodInfos,
        CallGraph callGraph,
        bool debugComments,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot(cancellationToken);

        var methodsByStartLine = new Dictionary<int, MethodTransformInfo>();
        var callSitesByLine = new Dictionary<int, CallSiteInfo>();

        // Find flooded method IDs for sync wrapper detection
        var floodedMethodIds = new HashSet<string>();
        foreach (var (id, method) in callGraph.Methods)
        {
            if (IsTaskReturnType(method.ReturnType))
                floodedMethodIds.Add(id);
        }

        foreach (var (method, originalReturnType, callsToAwait) in methodInfos)
        {
            string? floodingReason = null;
            if (debugComments)
                floodingReason = DeriveFloodingReason(method, callsToAwait, callGraph, floodedMethodIds);

            methodsByStartLine[method.StartLine] = new MethodTransformInfo
            {
                MethodId = method.Id,
                MethodName = method.Name,
                ContainingType = method.ContainingType,
                OriginalReturnType = originalReturnType,
                NewReturnType = method.ReturnType,
                StartLine = method.StartLine,
                EndLine = method.EndLine,
                FloodingReason = floodingReason
            };

            foreach (var call in callsToAwait)
            {
                if (!callSitesByLine.ContainsKey(call.LineNumber))
                {
                    callSitesByLine[call.LineNumber] = new CallSiteInfo
                    {
                        CalleeMethodId = call.CalleeId,
                        LineNumber = call.LineNumber
                    };
                }
            }
        }

        var rewriter = new AsyncMethodRewriter(methodsByStartLine, callSitesByLine, debugComments: debugComments);
        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyMethodTransformed)
            return null;

        newRoot = EnsureUsingDirective(newRoot, "System.Threading.Tasks");

        var transformedSource = newRoot.ToFullString();

        // Apply interface replacements if mappings are available
        if (callGraph.InterfaceMappings.Count > 0)
        {
            transformedSource = InterfaceReplacer.Transform(transformedSource, callGraph.InterfaceMappings)
                ?? transformedSource;
        }

        return new FileTransformation
        {
            FilePath = filePath,
            OriginalContent = sourceCode,
            TransformedContent = transformedSource,
            MethodTransformations = rewriter.Transformations.ToList()
        };
    }

    private static string DeriveFloodingReason(
        MethodNode method,
        List<MethodCall> callsToAwait,
        CallGraph callGraph,
        HashSet<string> floodedMethodIds)
    {
        // Check if it calls flooded methods
        if (callsToAwait.Count > 0)
        {
            var calleeName = callsToAwait[0].CalleeId;
            if (callGraph.Methods.TryGetValue(calleeName, out var calleeMethod))
                calleeName = $"{calleeMethod.ContainingType}.{calleeMethod.Name}";
            return callsToAwait.Count == 1
                ? $"Calls async method {calleeName}"
                : $"Calls async methods {calleeName} (+{callsToAwait.Count - 1} more)";
        }

        // Check interface implementations
        foreach (var impl in callGraph.InterfaceImplementations)
        {
            if (impl.ImplementingMethodId == method.Id && floodedMethodIds.Contains(impl.InterfaceMethodId))
            {
                var ifaceName = impl.InterfaceMethodId;
                if (callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var ifaceMethod))
                    ifaceName = $"{ifaceMethod.ContainingType}.{ifaceMethod.Name}";

                // For external interfaces, explain why the interface method was flooded
                var isExternal = ifaceMethod != null
                    && (string.IsNullOrEmpty(ifaceMethod.FilePath) || ifaceMethod.FilePath == "external");
                if (isExternal)
                {
                    var ifaceReason = DeriveFloodingReasonForInterfaceMethod(
                        impl.InterfaceMethodId, callGraph, floodedMethodIds);
                    if (ifaceReason != null)
                        return $"Implements {ifaceName} (flooded because: {ifaceReason})";
                }

                return $"Implements {ifaceName}";
            }
        }

        // Check method overrides
        foreach (var ovr in callGraph.MethodOverrides)
        {
            if (ovr.OverridingMethodId == method.Id && floodedMethodIds.Contains(ovr.BaseMethodId))
            {
                var baseName = ovr.BaseMethodId;
                if (callGraph.Methods.TryGetValue(ovr.BaseMethodId, out var baseMethod))
                    baseName = $"{baseMethod.ContainingType}.{baseMethod.Name}";

                // For external base methods, explain why it was flooded
                var isExternal = baseMethod != null
                    && (string.IsNullOrEmpty(baseMethod.FilePath) || baseMethod.FilePath == "external");
                if (isExternal)
                {
                    var baseReason = DeriveFloodingReasonForExternalMethod(
                        ovr.BaseMethodId, callGraph, floodedMethodIds);
                    if (baseReason != null)
                        return $"Overrides {baseName} (flooded because: {baseReason})";
                }

                return $"Overrides {baseName}";
            }
        }

        return "Flooded via async call graph";
    }

    /// <summary>
    /// Determines why an external interface method was flooded by finding a flooded
    /// implementation and explaining its reason.
    /// </summary>
    private static string? DeriveFloodingReasonForInterfaceMethod(
        string interfaceMethodId,
        CallGraph callGraph,
        HashSet<string> floodedMethodIds)
    {
        // An interface method is flooded because one of its implementations was flooded.
        // Find a flooded implementation and report its reason.
        foreach (var impl in callGraph.InterfaceImplementations)
        {
            if (impl.InterfaceMethodId == interfaceMethodId
                && floodedMethodIds.Contains(impl.ImplementingMethodId)
                && callGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var implMethod))
            {
                // Find why this implementation was flooded (its outgoing calls)
                var implCalls = callGraph.Calls
                    .Where(c => c.CallerId == impl.ImplementingMethodId && floodedMethodIds.Contains(c.CalleeId))
                    .ToList();

                if (implCalls.Count > 0)
                {
                    var calleeName = implCalls[0].CalleeId;
                    if (callGraph.Methods.TryGetValue(calleeName, out var calleeMethod))
                        calleeName = $"{calleeMethod.ContainingType}.{calleeMethod.Name}";
                    return $"{implMethod.ContainingType}.{implMethod.Name} calls async method {calleeName}";
                }

                return $"{implMethod.ContainingType}.{implMethod.Name} is flooded";
            }
        }

        return null;
    }

    /// <summary>
    /// Determines why an external base method was flooded by checking its callers
    /// or implementations.
    /// </summary>
    private static string? DeriveFloodingReasonForExternalMethod(
        string methodId,
        CallGraph callGraph,
        HashSet<string> floodedMethodIds)
    {
        // Check if the base method has flooded callers with async calls
        var outgoingCalls = callGraph.Calls
            .Where(c => c.CallerId == methodId && floodedMethodIds.Contains(c.CalleeId))
            .ToList();

        if (outgoingCalls.Count > 0)
        {
            var calleeName = outgoingCalls[0].CalleeId;
            if (callGraph.Methods.TryGetValue(calleeName, out var calleeMethod))
                calleeName = $"{calleeMethod.ContainingType}.{calleeMethod.Name}";
            return $"calls async method {calleeName}";
        }

        // Check overrides of this base method
        foreach (var ovr in callGraph.MethodOverrides)
        {
            if (ovr.BaseMethodId == methodId
                && floodedMethodIds.Contains(ovr.OverridingMethodId)
                && callGraph.Methods.TryGetValue(ovr.OverridingMethodId, out var overrideMethod))
            {
                return $"override {overrideMethod.ContainingType}.{overrideMethod.Name} is flooded";
            }
        }

        return null;
    }

    private static SyntaxNode EnsureUsingDirective(SyntaxNode root, string namespaceName)
    {
        if (root is not CompilationUnitSyntax compilationUnit)
            return root;

        // Check if using already exists
        var hasUsing = compilationUnit.Usings.Any(u =>
            u.Name?.ToString() == namespaceName);

        if (hasUsing)
            return root;

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return compilationUnit.AddUsings(usingDirective);
    }

    private static bool IsTaskReturnType(string returnType)
    {
        return returnType == "Task"
            || returnType.StartsWith("Task<")
            || returnType == "System.Threading.Tasks.Task"
            || returnType.StartsWith("System.Threading.Tasks.Task<");
    }

    private static string ReverseTaskReturnType(string taskReturnType)
    {
        if (taskReturnType == "Task")
            return "void";
        if (taskReturnType.StartsWith("Task<") && taskReturnType.EndsWith(">"))
            return taskReturnType.Substring(5, taskReturnType.Length - 6);
        return taskReturnType;
    }

    private static bool MatchesMethodByContext(MethodDeclarationSyntax methodDecl, AsyncTransformationInfo info)
    {
        // Try to match by method name from the MethodId (format: Namespace.Type.Method(params))
        var methodId = info.MethodId;
        var lastDot = methodId.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var parenIdx = methodId.IndexOf('(', lastDot);
            var methodName = parenIdx >= 0
                ? methodId.Substring(lastDot + 1, parenIdx - lastDot - 1)
                : methodId.Substring(lastDot + 1);

            if (methodDecl.Identifier.Text == methodName)
            {
                var declReturnType = methodDecl.ReturnType.ToString().Trim();
                if (declReturnType == info.OriginalReturnType)
                    return true;
            }
        }
        return false;
    }

    private static bool MatchesLocalFunctionByContext(LocalFunctionStatementSyntax localFunc, AsyncTransformationInfo info)
    {
        var methodId = info.MethodId;
        // Local function IDs end with .LocalFuncName(params)
        var lastDot = methodId.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var parenIdx = methodId.IndexOf('(', lastDot);
            var methodName = parenIdx >= 0
                ? methodId.Substring(lastDot + 1, parenIdx - lastDot - 1)
                : methodId.Substring(lastDot + 1);

            if (localFunc.Identifier.Text == methodName)
            {
                var declReturnType = localFunc.ReturnType.ToString().Trim();
                if (declReturnType == info.OriginalReturnType)
                    return true;
            }
        }
        return false;
    }

    private static string GetContainingTypeName(LocalFunctionStatementSyntax localFunc)
    {
        var parent = localFunc.Parent;
        while (parent != null)
        {
            if (parent is TypeDeclarationSyntax typeDecl)
                return typeDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return string.Empty;
    }

    private static string GetContainingTypeName(MethodDeclarationSyntax method)
    {
        var parent = method.Parent;
        while (parent != null)
        {
            if (parent is TypeDeclarationSyntax typeDecl)
                return typeDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return string.Empty;
    }
}
