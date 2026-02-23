using System.Linq;
using AsyncRewriter.Core.Models;


namespace AsyncRewriter.Transformation;

/// <summary>
/// Generates source code for async interface definitions.
/// </summary>
public static class AsyncInterfaceGenerator
{
    /// <summary>
    /// Generates the source code for a new async interface with the given methods.
    /// </summary>
    public static string GenerateAsyncInterface(string asyncInterfaceName, string ns, List<ProblematicMethod> methods)
    {
        var lines = new List<string>
        {
            "using System.Threading.Tasks;",
            "",
            $"namespace {ns};",
            "",
            $"public interface {asyncInterfaceName}",
            "{"
        };

        foreach (var m in methods)
        {
            var name = m.InterfaceMethod?.Name ?? m.OriginalImpl.Name;
            var returnType = m.AsyncImpl.ReturnType;
            var parameters = m.InterfaceMethod?.Parameters ?? m.OriginalImpl.Parameters;
            var paramStr = string.Join(", ", parameters.Select(p => p.ToString()));
            lines.Add($"    {returnType} {name}({paramStr});");
        }

        lines.Add("}");
        lines.Add("");

        return string.Join(Environment.NewLine, lines);
    }
}
