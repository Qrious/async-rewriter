using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Abstract async syntax walker modeled after Roslyn's CSharpSyntaxWalker.
/// Provides async virtual methods for visiting key syntax nodes.
/// </summary>
public abstract class AsyncCSharpSyntaxWalker
{
    /// <summary>
    /// Entry point: dispatches to typed visit methods based on node kind
    /// </summary>
    public virtual async Task VisitAsync(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (node)
        {
            case CompilationUnitSyntax compilationUnit:
                await VisitCompilationUnitAsync(compilationUnit, cancellationToken);
                break;
            case ClassDeclarationSyntax classDecl:
                await VisitClassDeclarationAsync(classDecl, cancellationToken);
                break;
            case InterfaceDeclarationSyntax interfaceDecl:
                await VisitInterfaceDeclarationAsync(interfaceDecl, cancellationToken);
                break;
            case MethodDeclarationSyntax methodDecl:
                await VisitMethodDeclarationAsync(methodDecl, cancellationToken);
                break;
            case InvocationExpressionSyntax invocation:
                await VisitInvocationExpressionAsync(invocation, cancellationToken);
                break;
            default:
                await DefaultVisitAsync(node, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Default visit: walks all child nodes recursively
    /// </summary>
    public virtual async Task DefaultVisitAsync(SyntaxNode node, CancellationToken cancellationToken = default)
    {
        foreach (var child in node.ChildNodes())
        {
            await VisitAsync(child, cancellationToken);
        }
    }

    public virtual Task VisitCompilationUnitAsync(CompilationUnitSyntax node, CancellationToken cancellationToken = default)
        => DefaultVisitAsync(node, cancellationToken);

    public virtual Task VisitClassDeclarationAsync(ClassDeclarationSyntax node, CancellationToken cancellationToken = default)
        => DefaultVisitAsync(node, cancellationToken);

    public virtual Task VisitInterfaceDeclarationAsync(InterfaceDeclarationSyntax node, CancellationToken cancellationToken = default)
        => DefaultVisitAsync(node, cancellationToken);

    public virtual Task VisitMethodDeclarationAsync(MethodDeclarationSyntax node, CancellationToken cancellationToken = default)
        => DefaultVisitAsync(node, cancellationToken);

    public virtual Task VisitInvocationExpressionAsync(InvocationExpressionSyntax node, CancellationToken cancellationToken = default)
        => DefaultVisitAsync(node, cancellationToken);
}
