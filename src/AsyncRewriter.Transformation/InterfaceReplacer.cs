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
    private readonly HashSet<string>? _transformedTypes;
    private readonly bool _debug;
    private bool _insideNonTransformedBaseList;
    private string? _currentTypeName;
    private readonly Dictionary<string, List<string>> _replacementsByType = new();

    public bool AnyReplaced { get; private set; }

    public InterfaceReplacer(IEnumerable<InterfaceMapping> mappings, HashSet<string>? transformedTypes = null,
        bool debug = false)
    {
        _syncToAsync = new Dictionary<string, string>();
        foreach (var m in mappings)
        {
            // Strip generic type args so VisitGenericName can match on Identifier.Text
            // e.g., "IMapInto<B, C>" → "IMapInto", "IMapIntoAsync<B, C>" → "IMapIntoAsync"
            var syncName = StripTypeArgs(m.SyncInterfaceName);
            var asyncName = StripTypeArgs(m.AsyncInterfaceName);

            _syncToAsync[syncName] = asyncName;

            // Also store simple names (last segment after '.')
            var syncSimple = GetSimpleName(syncName);
            var asyncSimple = GetSimpleName(asyncName);
            if (syncSimple != syncName)
            {
                _syncToAsync[syncSimple] = asyncSimple;
            }
        }

        _transformedTypes = transformedTypes;
        _debug = debug;
    }

    public override SyntaxNode? VisitBaseList(BaseListSyntax node)
    {
        var typeDecl = node.Parent;
        string? typeName = typeDecl switch
        {
            ClassDeclarationSyntax cls => cls.Identifier.Text,
            StructDeclarationSyntax str => str.Identifier.Text,
            _ => null
        };

        if (_transformedTypes != null && typeName != null && !_transformedTypes.Contains(typeName))
        {
            _insideNonTransformedBaseList = true;
            var result = base.VisitBaseList(node);
            _insideNonTransformedBaseList = false;
            return result;
        }

        _currentTypeName = typeName;
        var visited = base.VisitBaseList(node);
        _currentTypeName = null;
        return visited;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (!_insideNonTransformedBaseList && _syncToAsync.TryGetValue(node.Identifier.Text, out var asyncName))
        {
            AnyReplaced = true;
            RecordReplacement(node.Identifier.Text, asyncName);
            return node.WithIdentifier(Identifier(asyncName).WithTriviaFrom(node.Identifier));
        }
        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
        if (!_insideNonTransformedBaseList && _syncToAsync.TryGetValue(node.Identifier.Text, out var asyncName))
        {
            AnyReplaced = true;
            RecordReplacement(node.Identifier.Text, asyncName);
            return visited.WithIdentifier(Identifier(asyncName).WithTriviaFrom(visited.Identifier));
        }
        return visited;
    }

    private void RecordReplacement(string syncName, string asyncName)
    {
        if (!_debug || _currentTypeName == null)
        {
            return;
        }

        if (!_replacementsByType.TryGetValue(_currentTypeName, out var list))
        {
            list = new List<string>();
            _replacementsByType[_currentTypeName] = list;
        }
        var entry = $"Interface replaced: {syncName} → {asyncName} (external interface was problematic after async flooding)";
        if (!list.Contains(entry))
        {
            list.Add(entry);
        }
    }

    /// <summary>
    /// Transforms a source file, replacing sync interface references with async equivalents.
    /// Returns the transformed source, or null if no changes were made.
    /// </summary>
    public static string? Transform(string sourceCode, IEnumerable<InterfaceMapping> mappings,
        HashSet<string>? transformedTypes = null, bool debug = false)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        var rewriter = new InterfaceReplacer(mappings, transformedTypes, debug);
        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyReplaced)
        {
            return null;
        }

        // Insert debug comments above class/struct declarations that had interface replacements
        if (debug && rewriter._replacementsByType.Count > 0)
        {
            newRoot = new InterfaceDebugCommentInserter(rewriter._replacementsByType).Visit(newRoot);
        }

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

    private static string StripTypeArgs(string name)
    {
        var idx = name.IndexOf('<');
        return idx >= 0 ? name.Substring(0, idx) : name;
    }
}

/// <summary>
/// Second-pass rewriter that inserts debug comments above class/struct declarations
/// that had interface replacements.
/// </summary>
internal class InterfaceDebugCommentInserter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, List<string>> _replacementsByType;

    public InterfaceDebugCommentInserter(Dictionary<string, List<string>> replacementsByType)
    {
        _replacementsByType = replacementsByType;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        if (_replacementsByType.TryGetValue(node.Identifier.Text, out var lines))
        {
            return PrependDebugComments(visited, lines);
        }

        return visited;
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        if (_replacementsByType.TryGetValue(node.Identifier.Text, out var lines))
        {
            return PrependDebugComments(visited, lines);
        }

        return visited;
    }

    private static T PrependDebugComments<T>(T typeDecl, List<string> debugLines) where T : SyntaxNode
    {
        var existingLeading = typeDecl.GetLeadingTrivia();
        var triviaList = new List<SyntaxTrivia>();

        // Find indentation from existing leading trivia
        var indentation = "";
        for (var i = existingLeading.Count - 1; i >= 0; i--)
        {
            if (existingLeading[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                indentation = existingLeading[i].ToString();
                break;
            }
        }

        // Add all existing leading trivia except the last whitespace
        for (var i = 0; i < existingLeading.Count; i++)
        {
            if (i == existingLeading.Count - 1 && existingLeading[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            triviaList.Add(existingLeading[i]);
        }

        // Add debug comment lines
        foreach (var line in debugLines)
        {
            triviaList.Add(Whitespace(indentation));
            triviaList.Add(Comment($"// [async-rewriter] {line}"));
            triviaList.Add(LineFeed);
        }

        // Re-add the indentation
        triviaList.Add(Whitespace(indentation));

        return typeDecl.WithLeadingTrivia(TriviaList(triviaList));
    }
}
