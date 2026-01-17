namespace AsyncRewriter.Server.DTOs;

public class AnalyzeProjectRequest
{
    public string ProjectPath { get; set; } = string.Empty;
}

public class AnalyzeSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public string FileName { get; set; } = "source.cs";
}

public class AnalyzeFloodingRequest
{
    public string CallGraphId { get; set; } = string.Empty;
    public List<string> RootMethodIds { get; set; } = new();
}

public class TransformRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string CallGraphId { get; set; } = string.Empty;
    public bool ApplyChanges { get; set; } = false;
}

public class TransformSourceRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public List<string> MethodsToTransform { get; set; } = new();
}
