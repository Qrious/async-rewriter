namespace AsyncRewriter.Core.Models;

/// <summary>
/// Records that a lambda argument to a method call has a corresponding async overload.
/// When the lambda is flooded (becomes async), the parent call should resolve to the
/// async overload and needs await.
/// </summary>
public record LambdaAsyncOverload
{
    public required string LambdaMethodId { get; init; }
    public required string CallerMethodId { get; init; }
    public required string ParentCalleeMethodId { get; init; }
    public required string AsyncOverloadMethodId { get; init; }
    public required int ParentCallLineNumber { get; init; }
    public required string FilePath { get; init; }
}
