using System;
using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Detects external interfaces that become problematic after async flooding —
/// i.e., interfaces whose implementing methods changed return types but the interface itself is external/unchanged.
/// </summary>
public static class ProblematicInterfaceAnalyzer
{
    /// <summary>
    /// Groups problematic interface methods by their containing interface type.
    /// A method is problematic when its return type changed during flooding but it implements an external interface method.
    /// </summary>
    public static Dictionary<string, List<ProblematicMethod>> DetectProblematicInterfaces(CallGraph syncGraph, CallGraph asyncGraph)
    {
        var byInterfaceType = new Dictionary<string, List<ProblematicMethod>>();

        foreach (var impl in syncGraph.InterfaceImplementations)
        {
            if (!syncGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var originalImpl))
                continue;
            if (!asyncGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var asyncImpl))
                continue;
            if (originalImpl.ReturnType == asyncImpl.ReturnType)
                continue;

            var isExternal = !syncGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var interfaceMethod)
                || interfaceMethod.FilePath == "external";
            if (!isExternal)
                continue;

            if (interfaceMethod?.IsReturnTypeParameter == true)
                continue;

            var interfaceType = interfaceMethod?.ContainingType
                ?? impl.InterfaceMethodId.Split('.').LastOrDefault()
                ?? impl.InterfaceMethodId;

            if (!byInterfaceType.TryGetValue(interfaceType, out var list))
            {
                list = new();
                byInterfaceType[interfaceType] = list;
            }

            if (!list.Any(e => e.InterfaceMethodId == impl.InterfaceMethodId))
                list.Add(new ProblematicMethod(impl.InterfaceMethodId, interfaceMethod, originalImpl, asyncImpl));
        }

        return byInterfaceType;
    }

    /// <summary>
    /// Searches the call graph for an existing async version of the given sync interface.
    /// Looks for types named IAsyncFoo or IFooAsync that have matching method signatures.
    /// </summary>
    public static string? FindExistingAsyncInterface(CallGraph callGraph, string syncInterfaceType, List<ProblematicMethod> methods)
    {
        var genericSuffix = "";
        var baseType = syncInterfaceType;
        var angleBracketIndex = syncInterfaceType.IndexOf('<');
        if (angleBracketIndex >= 0)
        {
            genericSuffix = syncInterfaceType.Substring(angleBracketIndex);
            baseType = syncInterfaceType.Substring(0, angleBracketIndex);
        }

        var simpleName = baseType.Contains('.')
            ? baseType.Substring(baseType.LastIndexOf('.') + 1)
            : baseType;
        var prefix = baseType.Contains('.')
            ? baseType.Substring(0, baseType.LastIndexOf('.') + 1)
            : "";

        var candidateSimpleNames = new HashSet<string>(StringComparer.Ordinal);
        if (simpleName.StartsWith("I"))
            candidateSimpleNames.Add("IAsync" + simpleName.Substring(1));
        candidateSimpleNames.Add(simpleName + "Async");

        var candidateNames = new HashSet<string>(candidateSimpleNames, StringComparer.Ordinal);
        if (prefix.Length > 0)
        {
            foreach (var c in candidateSimpleNames)
                candidateNames.Add(prefix + c);
        }

        var methodsByType = callGraph.Methods.Values
            .GroupBy(m => m.ContainingType)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (typeName, candidateMethods) in methodsByType)
        {
            var typeNameBase = typeName;
            var typeAngle = typeName.IndexOf('<');
            if (typeAngle >= 0)
                typeNameBase = typeName.Substring(0, typeAngle);

            var typeSimpleName = typeNameBase.Contains('.')
                ? typeNameBase.Substring(typeNameBase.LastIndexOf('.') + 1)
                : typeNameBase;

            if (!candidateNames.Contains(typeNameBase) && !candidateSimpleNames.Contains(typeSimpleName))
                continue;

            var allMatch = true;
            foreach (var m in methods)
            {
                var name = m.InterfaceMethod?.Name ?? m.OriginalImpl.Name;
                var expectedReturnType = m.AsyncImpl.ReturnType;

                var match = candidateMethods.Any(cm =>
                    (cm.Name == name || cm.Name == name + "Async")
                    && NormalizeReturnType(cm.ReturnType) == NormalizeReturnType(expectedReturnType));

                if (!match)
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
                return typeName;
        }

        return null;
    }

    /// <summary>
    /// Gets the namespace of a type from the call graph by finding any method belonging to that type.
    /// </summary>
    public static string? GetNamespaceFromCallGraph(CallGraph callGraph, string typeName)
    {
        var method = callGraph.Methods.Values.FirstOrDefault(m => m.ContainingType == typeName);
        return method?.ContainingNamespace;
    }

    /// <summary>
    /// Strips type arguments from a generic type: "IMapper&lt;Foo, Bar&gt;" → "IMapper".
    /// Returns null for non-generic types.
    /// </summary>
    public static string? GetGenericBaseType(string interfaceType)
    {
        var angleBracketIndex = interfaceType.IndexOf('<');
        if (angleBracketIndex < 0)
            return null;
        return interfaceType.Substring(0, angleBracketIndex);
    }

    internal static string NormalizeReturnType(string returnType)
    {
        return returnType
            .Replace("System.Threading.Tasks.Task", "Task")
            .Replace("System.Threading.Tasks.ValueTask", "ValueTask");
    }
}
