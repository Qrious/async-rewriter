using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core;

/// <summary>
/// Generates deterministic method IDs from Roslyn method symbols.
/// The generated IDs are consistent with those stored in the call graph.
/// </summary>
public static class MethodIdFactory
{
    public static string GetMethodId(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;

        if (originalMethod.MethodKind == MethodKind.LocalFunction
            || originalMethod.MethodKind == MethodKind.AnonymousFunction)
        {
            var parts = new List<string>();
            var current = originalMethod;
            while (current != null
                   && (current.MethodKind == MethodKind.LocalFunction
                       || current.MethodKind == MethodKind.AnonymousFunction))
            {
                if (current.MethodKind == MethodKind.AnonymousFunction)
                {
                    var containingName = (current.ContainingSymbol as IMethodSymbol)?.Name ?? "";
                    var location = current.Locations.FirstOrDefault();
                    var line = location?.GetLineSpan().StartLinePosition.Line ?? 0;
                    var lambdaName = $"<{containingName}>b__{line}";
                    var parameters = string.Join(", ", current.Parameters.Select(
                        p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    parts.Add($"{lambdaName}({parameters})");
                }
                else
                {
                    parts.Add(GetMethodSignature(current));
                }

                current = current.ContainingSymbol as IMethodSymbol;
            }

            if (current != null)
            {
                parts.Add(GetMethodSignature(current));
            }

            parts.Reverse();
            var containingType = originalMethod.ContainingType?.ToDisplayString() ?? "";
            return $"{containingType}.{string.Join(".", parts)}";
        }

        return $"{originalMethod.ContainingType?.ToDisplayString()}.{GetMethodSignature(originalMethod)}";
    }

    public static string GetMethodSignature(IMethodSymbol methodSymbol)
    {
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(
            p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        return $"{originalMethod.Name}({parameters})";
    }
}
