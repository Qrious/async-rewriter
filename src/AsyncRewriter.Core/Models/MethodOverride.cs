namespace AsyncRewriter.Core.Models;

public record MethodOverride
{
    public required string CallGraphId { get; init; }
    public required string OverridingMethodId { get; init; }
    public required string BaseMethodId { get; init; }
}
