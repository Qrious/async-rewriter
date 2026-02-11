using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Roslyn syntax rewriter that replaces references to synchronous interface names
/// with their async equivalents (e.g. IRepository → IRepositoryAsync).
/// </summary>
public class InterfaceReplacer : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, string> _syncToAsync;

    public bool AnyReplaced { get; private set; }

    public InterfaceReplacer(IEnumerable<InterfaceMapping> mappings)
    {
        _syncToAsync = new Dictionary<string, string>();
        foreach (var m in mappings)
        {
            // Store both full name and simple name mappings
            _syncToAsync[m.SyncInterfaceName] = m.AsyncInterfaceName;

            // Also store simple names (last segment after '.')
            var syncSimple = GetSimpleName(m.SyncInterfaceName);
            var asyncSimple = GetSimpleName(m.AsyncInterfaceName);
            if (syncSimple != m.SyncInterfaceName)
                _syncToAsync[syncSimple] = asyncSimple;
        }
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_syncToAsync.TryGetValue(node.Identifier.Text, out var asyncName))
        {
            AnyReplaced = true;
            return node.WithIdentifier(Identifier(asyncName).WithTriviaFrom(node.Identifier));
        }
        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
        if (_syncToAsync.TryGetValue(node.Identifier.Text, out var asyncName))
        {
            AnyReplaced = true;
            return visited.WithIdentifier(Identifier(asyncName).WithTriviaFrom(visited.Identifier));
        }
        return visited;
    }

    /// <summary>
    /// Transforms a source file, replacing sync interface references with async equivalents.
    /// Returns the transformed source, or null if no changes were made.
    /// </summary>
    public static string? Transform(string sourceCode, IEnumerable<InterfaceMapping> mappings)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        var rewriter = new InterfaceReplacer(mappings);
        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyReplaced)
            return null;

        // Add any required using directives
        if (newRoot is CompilationUnitSyntax compilationUnit)
        {
            foreach (var mapping in mappings)
            {
                foreach (var ns in mapping.RequiredNamespaces)
                {
                    var hasUsing = compilationUnit.Usings.Any(u => u.Name?.ToString() == ns);
                    if (!hasUsing)
                    {
                        var usingDirective = UsingDirective(ParseName(ns).WithLeadingTrivia(Space))
                            .WithTrailingTrivia(LineFeed);
                        compilationUnit = compilationUnit.AddUsings(usingDirective);
                    }
                }
            }
            newRoot = compilationUnit;
        }

        return newRoot.ToFullString();
    }

    private static string GetSimpleName(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
    }
}
