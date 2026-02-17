using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Resolves method call relationships from a syntax tree using an async visitor pattern
/// </summary>
public class MethodCallExtractor : AsyncCSharpSyntaxWalker, IMethodCallExtractor
{
    private ConcurrentBag<MethodCall> _calls = new();
    private ConcurrentBag<LambdaAsyncOverload>? _lambdaAsyncOverloads;
    private SemanticModel _semanticModel = null!;
    private ISemanticModelResolver? _semanticModelResolver;
    private string _filePath = string.Empty;
    private IMethodSymbol? _currentMethodSymbol;
    private ConcurrentDictionary<string, MethodNode> _methods;
    private Guid _callGraphId;

    public Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<MethodCall> calls,
        CancellationToken cancellationToken = default)
    {
        return Extract(callGraphId, root, semanticModel, filePath, methods, calls, null!, cancellationToken);
    }

    public async Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<MethodCall> calls,
        ISemanticModelResolver semanticModelResolver,
        CancellationToken cancellationToken = default)
    {
        await Extract(callGraphId, root, semanticModel, filePath, methods, calls, semanticModelResolver, null!, cancellationToken);
    }

    public async Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<MethodCall> calls,
        ISemanticModelResolver semanticModelResolver,
        ConcurrentBag<LambdaAsyncOverload> lambdaAsyncOverloads,
        CancellationToken cancellationToken = default)
    {
        _callGraphId = callGraphId;
        _calls = calls;
        _lambdaAsyncOverloads = lambdaAsyncOverloads;
        _methods = methods;
        _semanticModel = semanticModel;
        _semanticModelResolver = semanticModelResolver;
        _filePath = filePath;
        _currentMethodSymbol = null;

        await VisitAsync(root, cancellationToken);
    }
    
    public override async Task VisitMethodDeclarationAsync(MethodDeclarationSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol == null)
            return;

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        // Visit children to find invocations within this method
        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitLocalFunctionStatementAsync(LocalFunctionStatementSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol == null)
            return;

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        // Visit children to find invocations within this local function
        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitParenthesizedLambdaExpressionAsync(ParenthesizedLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        // Expression tree lambdas (Expression<Func<T>>) are never executed,
        // so calls inside them should not create call graph edges.
        // This handles mocking frameworks (FakeItEasy A.CallTo, Moq Setup) and EF LINQ expressions.
        if (IsExpressionTreeLambda(node))
            return;

        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol == null)
            return;

        RecordLambdaCall(node, methodSymbol);

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitSimpleLambdaExpressionAsync(SimpleLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        if (IsExpressionTreeLambda(node))
            return;

        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol == null)
            return;

        RecordLambdaCall(node, methodSymbol);

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    /// <summary>
    /// Checks if a lambda is being passed as an Expression&lt;T&gt; parameter,
    /// meaning it's an expression tree that is never executed.
    /// </summary>
    private bool IsExpressionTreeLambda(LambdaExpressionSyntax node)
    {
        var typeInfo = _semanticModel.GetTypeInfo(node);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType is not INamedTypeSymbol namedType)
            return false;

        var originalDef = namedType.OriginalDefinition;
        return originalDef.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions"
            && originalDef.Name == "Expression";
    }

    private void RecordLambdaCall(SyntaxNode node, IMethodSymbol lambdaSymbol)
    {
        if (_currentMethodSymbol == null)
            return;

        var callerId = MethodExtractor.GetMethodId(_currentMethodSymbol);
        var calleeId = MethodExtractor.GetMethodId(lambdaSymbol);

        if (!_methods.ContainsKey(calleeId))
        {
            _methods.TryAdd(calleeId, CreateMethodNodeFromSymbol(lambdaSymbol, _filePath));
        }

        _calls.Add(new MethodCall
        {
            CallGraphId = _callGraphId.ToString(),
            Id = Guid.NewGuid().ToString(),
            CallerId = callerId,
            CalleeId = calleeId,
            LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            FilePath = _filePath
        });

        // Check if the lambda is an argument to an invocation that has an async overload
        if (_lambdaAsyncOverloads != null)
        {
            DetectAsyncOverloadForLambda(node, lambdaSymbol, callerId, calleeId);
        }
    }

    /// <summary>
    /// When a lambda is passed as an argument to a method, checks if the containing type
    /// has an overload where the corresponding Func parameter accepts Task-returning delegates
    /// and the method itself returns Task. If so, records a LambdaAsyncOverload.
    /// </summary>
    private void DetectAsyncOverloadForLambda(SyntaxNode lambdaNode, IMethodSymbol lambdaSymbol, string callerId, string lambdaId)
    {
        // Walk up to find the ArgumentSyntax, then the InvocationExpressionSyntax
        var argument = lambdaNode.FirstAncestorOrSelf<ArgumentSyntax>();
        if (argument == null) return;

        var invocation = argument.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation == null) return;

        var invokedSymbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (invokedSymbol == null) return;

        // Find which parameter index this lambda corresponds to
        var argList = invocation.ArgumentList;
        var argIndex = -1;
        for (int i = 0; i < argList.Arguments.Count; i++)
        {
            if (argList.Arguments[i] == argument)
            {
                argIndex = i;
                break;
            }
        }
        if (argIndex < 0 || argIndex >= invokedSymbol.Parameters.Length) return;

        var param = invokedSymbol.Parameters[argIndex];
        if (!IsFuncType(param.Type)) return;

        // Look for an async overload in the same type
        var containingType = invokedSymbol.ContainingType;
        if (containingType == null) return;

        foreach (var member in containingType.GetMembers(invokedSymbol.Name).OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(member, invokedSymbol))
                continue;
            if (member.Parameters.Length != invokedSymbol.Parameters.Length)
                continue;

            // Check if this overload has an async Func at the same parameter position
            // and returns Task/Task<T>
            if (!IsAsyncFuncCounterpart(invokedSymbol.Parameters[argIndex].Type, member.Parameters[argIndex].Type))
                continue;
            if (!IsTaskReturning(member.ReturnType))
                continue;

            // All other parameters should match
            var allMatch = true;
            for (int i = 0; i < member.Parameters.Length; i++)
            {
                if (i == argIndex) continue;
                if (!SymbolEqualityComparer.Default.Equals(
                    invokedSymbol.Parameters[i].Type, member.Parameters[i].Type))
                {
                    allMatch = false;
                    break;
                }
            }
            if (!allMatch) continue;

            var asyncOverloadId = MethodExtractor.GetMethodId(member);
            if (!_methods.ContainsKey(asyncOverloadId))
            {
                _methods.TryAdd(asyncOverloadId, CreateMethodNodeFromSymbol(member, "external"));
            }

            _lambdaAsyncOverloads!.Add(new LambdaAsyncOverload
            {
                LambdaMethodId = lambdaId,
                CallerMethodId = callerId,
                ParentCalleeMethodId = MethodExtractor.GetMethodId(invokedSymbol),
                AsyncOverloadMethodId = asyncOverloadId,
                ParentCallLineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                FilePath = _filePath
            });
            break; // Found one async overload, that's enough
        }
    }

    private static bool IsFuncType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        return named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System"
            && named.OriginalDefinition.Name == "Func";
    }

    /// <summary>
    /// Checks if asyncType is the async counterpart of syncType.
    /// E.g. Func&lt;T, TResult&gt; → Func&lt;T, Task&lt;TResult&gt;&gt;
    /// or Func&lt;T&gt; → Func&lt;T, Task&gt; (void-returning)
    /// </summary>
    private static bool IsAsyncFuncCounterpart(ITypeSymbol syncType, ITypeSymbol asyncType)
    {
        if (syncType is not INamedTypeSymbol syncFunc || asyncType is not INamedTypeSymbol asyncFunc)
            return false;

        if (!IsFuncType(syncType) || !IsFuncType(asyncType))
            return false;

        var syncArgs = syncFunc.TypeArguments;
        var asyncArgs = asyncFunc.TypeArguments;

        // The async Func should have the same number of type args
        if (syncArgs.Length != asyncArgs.Length) return false;

        // All args except the last (return type) should match
        for (int i = 0; i < syncArgs.Length - 1; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(syncArgs[i], asyncArgs[i]))
                return false;
        }

        // The last type arg of the async version should be Task<syncReturnType>
        var syncReturn = syncArgs[syncArgs.Length - 1];
        var asyncReturn = asyncArgs[asyncArgs.Length - 1];

        if (asyncReturn is INamedTypeSymbol asyncReturnNamed && IsTaskReturning(asyncReturnNamed))
        {
            if (asyncReturnNamed.TypeArguments.Length == 1)
                return SymbolEqualityComparer.Default.Equals(syncReturn, asyncReturnNamed.TypeArguments[0]);
            // Task (no type arg) — would need syncReturn to be void-like, but Func doesn't have void
        }

        return false;
    }

    private static bool IsTaskReturning(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return false;
        var ns = named.ContainingNamespace?.ToDisplayString();
        return ns == "System.Threading.Tasks" && (named.Name == "Task" || named.Name == "ValueTask");
    }

    public override async Task VisitObjectCreationExpressionAsync(ObjectCreationExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        if (_currentMethodSymbol != null)
        {
            var constructorSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (constructorSymbol != null)
            {
                ResolveLambdaArgsThroughConstructor(node.ArgumentList, constructorSymbol);
            }
        }

        await DefaultVisitAsync(node, cancellationToken);
    }

    /// <summary>
    /// When a lambda is passed as a constructor argument, traces it through
    /// parameter → field assignment → field invocation to create call edges
    /// from the invoking methods to the original lambda.
    /// </summary>
    private void ResolveLambdaArgsThroughConstructor(BaseArgumentListSyntax? argumentList, IMethodSymbol constructorSymbol)
    {
        var args = argumentList?.Arguments;
        if (args == null) return;

        for (int i = 0; i < args.Value.Count; i++)
        {
            var argExpr = args.Value[i].Expression;
            if (argExpr is not (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
                continue;

            var lambdaSymbol = _semanticModel.GetSymbolInfo(argExpr).Symbol as IMethodSymbol;
            if (lambdaSymbol == null || i >= constructorSymbol.Parameters.Length)
                continue;

            // Use OriginalDefinition so the parameter matches the unsubstituted constructor body
            var originalConstructor = constructorSymbol.OriginalDefinition;
            var param = originalConstructor.Parameters[i];

            // Find fields assigned from this parameter in the constructor body
            var fields = FindFieldsAssignedFromParameter(originalConstructor, param);

            // For each field, find methods in the type that invoke it as a delegate
            foreach (var field in fields)
            {
                LinkDelegateFieldInvocationsToLambda(field, lambdaSymbol);
            }
        }
    }

    private SemanticModel? ResolveSemanticModel(SyntaxTree syntaxTree)
    {
        if (_semanticModel.Compilation.ContainsSyntaxTree(syntaxTree))
            return _semanticModel.Compilation.GetSemanticModel(syntaxTree);

        return _semanticModelResolver?.Resolve(syntaxTree);
    }

    private List<IFieldSymbol> FindFieldsAssignedFromParameter(IMethodSymbol constructorSymbol, IParameterSymbol param)
    {
        var fields = new List<IFieldSymbol>();

        var constructorSyntax = constructorSymbol.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax();
        if (constructorSyntax == null) return fields;

        var constructorModel = ResolveSemanticModel(constructorSyntax.SyntaxTree);
        if (constructorModel == null) return fields;

        foreach (var assignment in constructorSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var rightSymbol = constructorModel.GetSymbolInfo(assignment.Right).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(rightSymbol, param))
                continue;

            var leftSymbol = constructorModel.GetSymbolInfo(assignment.Left).Symbol as IFieldSymbol;
            if (leftSymbol != null)
                fields.Add(leftSymbol);
        }

        return fields;
    }

    private void LinkDelegateFieldInvocationsToLambda(IFieldSymbol field, IMethodSymbol lambdaSymbol)
    {
        var containingType = field.ContainingType;
        if (containingType == null) return;

        var lambdaId = MethodExtractor.GetMethodId(lambdaSymbol);

        foreach (var member in containingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind == MethodKind.Constructor)
                continue;

            var memberSyntax = member.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax();
            if (memberSyntax == null) continue;

            var memberModel = ResolveSemanticModel(memberSyntax.SyntaxTree);
            if (memberModel == null) continue;

            foreach (var invocation in memberSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // Check if the invocation target is the delegate field
                var exprSymbol = memberModel.GetSymbolInfo(invocation.Expression).Symbol;
                if (exprSymbol is not IFieldSymbol invokedField)
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(invokedField, field))
                    continue;

                var callerId = MethodExtractor.GetMethodId(member);

                if (!_methods.ContainsKey(lambdaId))
                {
                    _methods.TryAdd(lambdaId, CreateMethodNodeFromSymbol(lambdaSymbol, _filePath));
                }

                _calls.Add(new MethodCall
                {
                    CallGraphId = _callGraphId.ToString(),
                    Id = Guid.NewGuid().ToString(),
                    CallerId = callerId,
                    CalleeId = lambdaId,
                    LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    FilePath = memberSyntax.SyntaxTree.FilePath
                });
            }
        }
    }

    public override async Task VisitInvocationExpressionAsync(InvocationExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        if (_currentMethodSymbol != null)
        {
            var invokedSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (invokedSymbol != null)
            {
                var callerId = MethodExtractor.GetMethodId(_currentMethodSymbol);
                var calleeId = GetCalleeId(invokedSymbol);

                // Create a method node for the callee if it doesn't exist in known methods
                if (!_methods.ContainsKey(calleeId))
                {
                    _methods.TryAdd(calleeId, CreateMethodNodeFromSymbol(invokedSymbol, "external"));
                }

                var methodCall = new MethodCall
                {
                    CallGraphId = _callGraphId.ToString(),
                    Id = Guid.NewGuid().ToString(),
                    CallerId = callerId,
                    CalleeId = calleeId,
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    FilePath = _filePath
                };

                _calls.Add(methodCall);
            }
        }

        // Continue walking into children to find nested invocations (e.g., inside lambdas)
        await DefaultVisitAsync(node, cancellationToken);
    }

    /// <summary>
    /// Gets the callee ID for an invoked method. If the method is on an instantiated generic interface,
    /// uses the instantiated ID so the call flows through the instantiated node.
    /// </summary>
    private static string GetCalleeId(IMethodSymbol invokedSymbol)
    {
        if (invokedSymbol.ContainingType is INamedTypeSymbol containingType
            && containingType.TypeKind == TypeKind.Interface
            && containingType.IsGenericType
            && !SymbolEqualityComparer.Default.Equals(containingType, containingType.OriginalDefinition))
        {
            return MethodExtractor.GetInstantiatedMethodId(invokedSymbol);
        }

        return MethodExtractor.GetMethodId(invokedSymbol);
    }

    private MethodNode CreateMethodNodeFromSymbol(IMethodSymbol methodSymbol, string filePath)
    {
        var original = methodSymbol.OriginalDefinition;
        return new MethodNode
        {
            CallGraphId = _callGraphId.ToString(),
            Id = MethodExtractor.GetMethodId(methodSymbol),
            Name = original.Name,
            ContainingType = original.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = original.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = original.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = original.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            StartLine = methodSymbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            EndLine = methodSymbol.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
        };
    }
}
