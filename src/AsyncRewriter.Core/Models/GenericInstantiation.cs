namespace AsyncRewriter.Core.Models;

public record GenericInstantiation
{
    public required string CallGraphId { get; init; }
    public required string InstantiatedMethodId { get; init; }
    public required string GenericMethodId { get; init; }
}
