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
        bool debug,
        CancellationToken cancellationToken = default)
    {
        var result = new TransformationResult { CallGraph = callGraph };

        try
        {
            // Build transformation info from the flooded call graph
            var transformationsByFile = BuildTransformationsByFile(callGraph);
            var methodRenames = new Dictionary<string, string>();//BuildMethodRenamesByMethodId(callGraph);
            var outParamMethodsById = new Dictionary<string, OutParameterMethod>(); //BuildOutParameterMethodsById(callGraph);

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
                    filePath, sourceCode, transformations, callGraph, methodRenames, outParamMethodsById, debug, cancellationToken);

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
                    // Use CalledMethodSignature as CalleeMethodId when available (it identifies the
                    // actual callee), falling back to the parent method ID for backwards compatibility
                    var calleeId = !string.IsNullOrEmpty(callSite.CalledMethodSignature)
                        ? callSite.CalledMethodSignature
                        : info.MethodId;

                    callSitesByLine[callSite.LineNumber] = new CallSiteInfo
                    {
                        CalleeMethodId = calleeId,
                        LineNumber = callSite.LineNumber,
                        CalleeMethodName = ExtractMethodNameFromExpression(callSite.OriginalCallExpression)
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

    private Dictionary<string, List<(IMethodNode Method, string OriginalReturnType, List<IMethodCall> CallsToAwait)>>
        BuildTransformationsByFile(CallGraph callGraph)
    {
        var result = new Dictionary<string, List<(IMethodNode, string, List<IMethodCall>)>>();

        // Find all methods that need transformation (return type is Task-based)
        var floodedMethodIds = new HashSet<string>();
        foreach (var (id, method) in callGraph.Methods)
        {
            if (IsTaskReturnType(method.ReturnType))
            {
                floodedMethodIds.Add(id);
            }
        }

        foreach (var methodId in floodedMethodIds)
        {
            var method = callGraph.Methods[methodId];
            if (string.IsNullOrEmpty(method.FilePath) || method.FilePath == "external")
            {
                continue;
            }

            // Find calls from this method to other flooded methods that need await
            var callsToAwait = new List<IMethodCall>();
            foreach (var call in callGraph.Calls)
            {
                if (call.CallerId == methodId && floodedMethodIds.Contains(call.CalleeId))
                {
                    callsToAwait.Add(call);
                }
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
        List<(IMethodNode Method, string OriginalReturnType, List<IMethodCall> CallsToAwait)> methodInfos,
        CallGraph callGraph,
        Dictionary<string, string> methodRenamesByMethodId,
        Dictionary<string, OutParameterMethod> outParamMethodsById,
        bool debug,
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
            {
                floodedMethodIds.Add(id);
            }
        }

        // Build a callee-to-caller lookup for debug info
        Dictionary<string, List<string>>? callersByMethod = null;
        if (debug)
        {
            callersByMethod = new Dictionary<string, List<string>>();
            foreach (var call in callGraph.Calls)
            {
                if (!callersByMethod.TryGetValue(call.CalleeId, out var callers))
                {
                    callers = new List<string>();
                    callersByMethod[call.CalleeId] = callers;
                }
                callers.Add(call.CallerId);
            }
        }

        foreach (var (method, originalReturnType, callsToAwait) in methodInfos)
        {
            // Check if this method needs renaming due to an async interface mapping
            methodRenamesByMethodId.TryGetValue(method.Id, out var newMethodName);

            List<string>? debugLines = null;
            if (debug)
            {
                debugLines = BuildDebugLines(method, originalReturnType, callsToAwait, callGraph, floodedMethodIds, callersByMethod!, newMethodName);
            }

            OutParameterTransformInfo? outParamInfo = null;
            if (outParamMethodsById.TryGetValue(method.Id, out var outMethod))
            {
                outParamInfo = new OutParameterTransformInfo
                {
                    IsTryPattern = outMethod.TransformKind == OutParameterTransformKind.BoolTryPattern,
                    OutParameterIndices = outMethod.OutParameterIndices,
                    OutParameterTypes = outMethod.OutParameterTypes,
                    OutParameterNames = outMethod.OutParameterNames,
                    NewAsyncReturnType = outMethod.NewAsyncReturnType
                };
            }

            methodsByStartLine[method.StartLine] = new MethodTransformInfo
            {
                MethodId = method.Id,
                MethodName = method.Name,
                ContainingType = method.ContainingType,
                OriginalReturnType = originalReturnType,
                NewReturnType = method.ReturnType,
                StartLine = method.StartLine,
                EndLine = method.EndLine,
                DebugLines = debugLines,
                NewMethodName = newMethodName,
                OutParameterInfo = outParamInfo
            };

            foreach (var call in callsToAwait)
            {
                if (!callSitesByLine.ContainsKey(call.LineNumber))
                {
                    callSitesByLine[call.LineNumber] = new CallSiteInfo
                    {
                        CalleeMethodId = call.CalleeId,
                        LineNumber = call.LineNumber,
                        CalleeMethodName = ExtractMethodNameFromMethodId(call.CalleeId)
                    };
                }
            }
        }

        var syncWrapperMethodIds = DetectSyncWrapperMethodIds(callGraph);
        var rewriter = new AsyncMethodRewriter(methodsByStartLine, callSitesByLine, syncWrapperMethodIds);
        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyMethodTransformed)
        {
            return null;
        }

        newRoot = EnsureUsingDirective(newRoot, "System.Threading.Tasks");

        // Check if any out-parameter methods were transformed
        var hasOutParamMethods = methodsByStartLine.Values.Any(m => m.OutParameterInfo != null);
        if (hasOutParamMethods)
        {
            // Add using for AsyncOutResult if any BoolTryPattern methods were transformed
            var hasTryPattern = methodsByStartLine.Values.Any(m =>
                m.OutParameterInfo is { IsTryPattern: true });
            if (hasTryPattern)
            {
                var outResultNs = ResolveAsyncOutResultNamespace(callGraph, filePath);
                newRoot = EnsureUsingDirective(newRoot, outResultNs);
            }

            // Build out-parameter call site info for callers of out-param methods
            var outParamCallSites = BuildOutParameterCallSites(callGraph, outParamMethodsById, floodedMethodIds);
            if (outParamCallSites.Count > 0)
            {
                var outCallSiteRewriter = new OutParameterCallSiteRewriter(outParamCallSites);
                newRoot = outCallSiteRewriter.Visit(newRoot);
            }
        }

        var transformedSource = newRoot.ToFullString();

        // Apply interface replacements if mappings are available
        if (callGraph.InterfaceMappings.Count > 0)
        {
            var transformedTypeNames = new HashSet<string>(
                methodsByStartLine.Values.Select(m => m.ContainingType));
            transformedSource = InterfaceReplacer.Transform(transformedSource, callGraph.InterfaceMappings, transformedTypeNames, debug)
                ?? transformedSource;
        }
        // else: no InterfaceMappings on call graph — nothing to replace

        return new FileTransformation
        {
            FilePath = filePath,
            OriginalContent = sourceCode,
            TransformedContent = transformedSource,
            MethodTransformations = rewriter.Transformations.ToList()
        };
    }

    private static Dictionary<string, string> BuildMethodRenamesByMethodId(CallGraph callGraph)
    {
        var result = new Dictionary<string, string>();
        foreach (var mapping in callGraph.InterfaceMappings)
        {
            if (mapping.MethodRenames.Count == 0)
            {
                continue;
            }

            // Find implementing methods for this interface's methods
            foreach (var impl in callGraph.InterfaceImplementations)
            {
                if (!callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var ifaceMethod))
                {
                    continue;
                }

                if (ifaceMethod.ContainingType != mapping.SyncInterfaceName)
                {
                    continue;
                }

                if (!mapping.MethodRenames.TryGetValue(ifaceMethod.Name, out var newName))
                {
                    continue;
                }

                result[impl.ImplementingMethodId] = newName;
            }
        }
        return result;
    }

    private static Dictionary<int, OutParameterCallSiteInfo> BuildOutParameterCallSites(
        CallGraph callGraph,
        Dictionary<string, OutParameterMethod> outParamMethodsById,
        HashSet<string> floodedMethodIds)
    {
        var result = new Dictionary<int, OutParameterCallSiteInfo>();

        foreach (var call in callGraph.Calls)
        {
            if (!outParamMethodsById.TryGetValue(call.CalleeId, out var outMethod))
            {
                continue;
            }

            // Only transform call sites within flooded methods
            if (!floodedMethodIds.Contains(call.CallerId))
            {
                continue;
            }

            if (!result.ContainsKey(call.LineNumber))
            {
                result[call.LineNumber] = new OutParameterCallSiteInfo
                {
                    MethodName = outMethod.Method.Name,
                    IsTryPattern = outMethod.TransformKind == OutParameterTransformKind.BoolTryPattern,
                    OutParameterIndices = outMethod.OutParameterIndices,
                    OutParameterNames = outMethod.OutParameterNames,
                    LineNumber = call.LineNumber
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a lookup of out-parameter methods that need special transformation.
    /// Uses the original call graph stored alongside the async graph to detect out params.
    /// </summary>
    private static Dictionary<string, OutParameterMethod> BuildOutParameterMethodsById(CallGraph callGraph)
    {
        // We need the original graph to detect out params, but the flooded graph has Task return types.
        // The MethodNode in callGraph already has ParameterRefKinds from extraction.
        // We detect methods that: (1) have out params, (2) have Task return types (flooded).
        var result = new Dictionary<string, OutParameterMethod>();

        foreach (var (id, m) in callGraph.Methods)
        {
            var method = (MethodNode)m;
            if (!IsTaskReturnType(method.ReturnType))
            {
                continue;
            }

            if (!method.HasOutParameters)
            {
                continue;
            }

            if (string.IsNullOrEmpty(method.FilePath) || method.FilePath == "external")
            {
                continue;
            }

            var refKinds = method.ParameterRefKinds!;
            var outIndices = new List<int>();
            var outTypes = new List<string>();
            var outNames = new List<string>();

            for (int i = 0; i < refKinds.Count; i++)
            {
                if (refKinds[i] == "out")
                {
                    outIndices.Add(i);
                    var param = method.Parameters[i];
                    var spaceIdx = param.LastIndexOf(' ');
                    outTypes.Add(spaceIdx >= 0 ? param.Substring(0, spaceIdx) : param);
                    outNames.Add(spaceIdx >= 0 ? param.Substring(spaceIdx + 1) : $"out{i}");
                }
            }

            var originalReturnType = ReverseTaskReturnType(method.ReturnType);
            var isBoolReturn = originalReturnType is "bool" or "Boolean" or "System.Boolean";
            var kind = isBoolReturn ? OutParameterTransformKind.BoolTryPattern : OutParameterTransformKind.TuplePattern;

            string newAsyncReturnType;
            if (kind == OutParameterTransformKind.BoolTryPattern)
            {
                string innerType;
                if (outTypes.Count == 1)
                {
                    innerType = outTypes[0];
                }
                else
                {
                    var tupleElements = outTypes.Zip(outNames, (t, n) => $"{t} {n}");
                    innerType = $"({string.Join(", ", tupleElements)})";
                }
                newAsyncReturnType = $"Task<AsyncOutResult<{innerType}>>";
            }
            else
            {
                var elements = new List<string> { $"{originalReturnType} Result" };
                for (int i = 0; i < outTypes.Count; i++)
                {
                    elements.Add($"{outTypes[i]} {outNames[i]}");
                }

                newAsyncReturnType = $"Task<({string.Join(", ", elements)})>";
            }

            result[id] = new OutParameterMethod
            {
                MethodId = id,
                Method = method,
                OriginalReturnType = originalReturnType,
                TransformKind = kind,
                OutParameterIndices = outIndices,
                OutParameterTypes = outTypes,
                OutParameterNames = outNames,
                NewAsyncReturnType = newAsyncReturnType
            };
        }

        return result;
    }
    

    private static SyntaxNode EnsureUsingDirective(SyntaxNode root, string namespaceName)
    {
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return root;
        }

        // Check if using already exists
        var hasUsing = compilationUnit.Usings.Any(u =>
            u.Name?.ToString() == namespaceName);

        if (hasUsing)
        {
            return root;
        }

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return compilationUnit.AddUsings(usingDirective);
    }

    private static readonly System.Text.RegularExpressions.Regex SyncWrapperFuncTaskRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SyncWrapperFuncTaskOfTRegex = new(
        @"(?:System\.)?Func<(?:System\.Threading\.Tasks\.)?Task<(.+?)>>",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Detects sync wrapper methods in the call graph — methods with a Func&lt;Task&gt; or
    /// Func&lt;Task&lt;T&gt;&gt; parameter that return void or T respectively.
    /// Handles flooded call graphs where return types have been changed to Task/Task&lt;T&gt;.
    /// </summary>
    private static HashSet<string> DetectSyncWrapperMethodIds(CallGraph callGraph)
    {
        var result = new HashSet<string>();

        foreach (var method in callGraph.Methods.Values)
        {
            // Use the original return type (reverse the flooding) for pattern matching
            var originalReturnType = ReverseTaskReturnType(method.ReturnType);

            foreach (var param in method.Parameters)
            {
                if (SyncWrapperFuncTaskRegex.IsMatch(param) && originalReturnType == "void")
                {
                    result.Add(method.Id);
                    break;
                }

                var match = SyncWrapperFuncTaskOfTRegex.Match(param);
                if (match.Success && originalReturnType == match.Groups[1].Value)
                {
                    result.Add(method.Id);
                    break;
                }
            }
        }

        return result;
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
        {
            return "void";
        }

        if (taskReturnType.StartsWith("Task<") && taskReturnType.EndsWith(">"))
        {
            return taskReturnType.Substring(5, taskReturnType.Length - 6);
        }

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
                {
                    return true;
                }
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
                {
                    return true;
                }
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
            {
                return typeDecl.Identifier.Text;
            }

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
            {
                return typeDecl.Identifier.Text;
            }

            parent = parent.Parent;
        }
        return string.Empty;
    }

    /// <summary>
    /// Resolves the namespace for the AsyncOutResult class.
    /// Uses the CallGraph's explicit setting if available, otherwise looks for an existing
    /// AsyncOutResult type in the call graph's methods, then derives the namespace from
    /// TryPattern methods' ContainingNamespace, falling back to the default namespace.
    /// </summary>
    private static string ResolveAsyncOutResultNamespace(CallGraph callGraph, string filePath)
    {
        // 1. Use explicit setting from call graph if available
        if (!string.IsNullOrEmpty(callGraph.AsyncOutResultNamespace))
        {
            return callGraph.AsyncOutResultNamespace;
        }

        // 2. Look for an existing AsyncOutResult class in the call graph's methods
        var asyncOutResultMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.ContainingType == AsyncOutResultGenerator.ClassName);
        if (asyncOutResultMethod != null && !string.IsNullOrEmpty(asyncOutResultMethod.ContainingNamespace))
        {
            return asyncOutResultMethod.ContainingNamespace;
        }

        // 3. Derive namespace from TryPattern methods (bool return + out params) in the call graph
        var tryPatternMethod = callGraph.Methods.Values
            .OfType<MethodNode>()
            .FirstOrDefault(m =>
                m.HasOutParameters
                && m.ReturnType is "bool" or "Boolean" or "System.Boolean"
                && !string.IsNullOrEmpty(m.ContainingNamespace)
                && m.FilePath != "external");
        if (tryPatternMethod != null)
        {
            return tryPatternMethod.ContainingNamespace;
        }

        // 4. Fall back to default
        return AsyncOutResultGenerator.DefaultNamespace;
    }

    /// <summary>
    /// Extracts the method name from a call expression like "_builder.Configure()" → "Configure",
    /// or "LocalFunc()" → "LocalFunc".
    /// Parses backwards from the opening paren to find the method name.
    /// </summary>
    internal static string? ExtractMethodNameFromExpression(string? expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            return null;
        }

        // Find the last '(' that is the method's argument list opener.
        // We need to handle nested parens: e.g., "obj.Method(Foo(1))" → "Method"
        // Walk backwards, tracking paren depth to find the outermost opening paren of the invocation.
        var depth = 0;
        var parenIdx = -1;
        for (var i = expression.Length - 1; i >= 0; i--)
        {
            if (expression[i] == ')')
            {
                depth++;
            }
            else if (expression[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    parenIdx = i;
                    break;
                }
            }
        }

        if (parenIdx <= 0)
        {
            return null;
        }

        // Walk backwards from parenIdx to find the start of the method name
        var nameEnd = parenIdx;
        var nameStart = nameEnd - 1;
        while (nameStart >= 0 && (char.IsLetterOrDigit(expression[nameStart]) || expression[nameStart] == '_'))
        {
            nameStart--;
        }

        nameStart++;

        if (nameStart >= nameEnd)
        {
            return null;
        }

        return expression.Substring(nameStart, nameEnd - nameStart);
    }

    /// <summary>
    /// Extracts the method name from a method ID like "IBuilder.Configure()" → "Configure",
    /// or "Namespace.Type.Method(param)" → "Method".
    /// </summary>
    internal static string? ExtractMethodNameFromMethodId(string? methodId)
    {
        if (string.IsNullOrEmpty(methodId))
        {
            return null;
        }

        var parenIdx = methodId.IndexOf('(');
        if (parenIdx < 0)
        {
            parenIdx = methodId.Length;
        }

        var lastDot = methodId.LastIndexOf('.', parenIdx - 1);
        var nameStart = lastDot >= 0 ? lastDot + 1 : 0;

        if (nameStart >= parenIdx)
        {
            return null;
        }

        var name = methodId.Substring(nameStart, parenIdx - nameStart);

        // Strip generic type arguments (e.g., "RunSync<int>" → "RunSync")
        var angleIdx = name.IndexOf('<');
        if (angleIdx >= 0)
        {
            name = name.Substring(0, angleIdx);
        }

        return name;
    }
}
