namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents the complete call graph for a codebase or project
/// </summary>
public class CallGraph
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ProjectName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, MethodNode> Methods { get; set; } = new();
    public List<MethodCall> Calls { get; set; } = new();

    /// <summary>
    /// Root methods that were marked for async transformation
    /// </summary>
    public HashSet<string> RootAsyncMethods { get; set; } = new();

    /// <summary>
    /// Methods affected by async flooding (need to become async)
    /// </summary>
    public HashSet<string> FloodedMethods { get; set; } = new();

    /// <summary>
    /// Add a method to the graph
    /// </summary>
    public void AddMethod(MethodNode method)
    {
        Methods[method.Id] = method;
    }

    /// <summary>
    /// Add a call relationship
    /// </summary>
    public void AddCall(MethodCall call)
    {
        Calls.Add(call);
    }

    /// <summary>
    /// Get all methods that call the specified method
    /// </summary>
    public IEnumerable<MethodNode> GetCallers(string methodId)
    {
        var callerIds = Calls
            .Where(c => c.CalleeId == methodId)
            .Select(c => c.CallerId)
            .Distinct();

        return callerIds.Select(id => Methods[id]).Where(m => m != null);
    }

    /// <summary>
    /// Get all methods called by the specified method
    /// </summary>
    public IEnumerable<MethodNode> GetCallees(string methodId)
    {
        var calleeIds = Calls
            .Where(c => c.CallerId == methodId)
            .Select(c => c.CalleeId)
            .Distinct();

        return calleeIds.Select(id => Methods[id]).Where(m => m != null);
    }
}
