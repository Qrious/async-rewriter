using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Replaces specific method declarations in a syntax tree with pre-refactored text.
/// Methods are identified by their 0-based start line so that callers do not need
/// to worry about ordering — Roslyn rewrites by node identity, not position.
/// </summary>
public sealed class CopilotMethodReplacementRewriter : CSharpSyntaxRewriter
{
    /// <summary>0-based start line → replacement source text for that method.</summary>
    private readonly IReadOnlyDictionary<int, string> _replacementsByStartLine;

    public CopilotMethodReplacementRewriter(IReadOnlyDictionary<int, string> replacementsByStartLine)
    {
        _replacementsByStartLine = replacementsByStartLine;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var startLine = node.GetLocation().GetLineSpan().StartLinePosition.Line;

        if (!_replacementsByStartLine.TryGetValue(startLine, out var replacementText))
            return base.VisitMethodDeclaration(node);

        // Parse the replacement as a class member so we get the full method node.
        var wrapper = SyntaxFactory.ParseCompilationUnit(
            $"class __W__ {{ {replacementText} }}");

        var replacement = wrapper
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (replacement == null)
            return base.VisitMethodDeclaration(node);

        // Preserve the original leading/trailing trivia (whitespace, comments) so
        // the file's formatting is not disturbed.
        return replacement
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());
    }
}
