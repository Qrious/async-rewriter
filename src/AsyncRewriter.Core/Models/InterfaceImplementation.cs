namespace AsyncRewriter.Core.Models;

public record InterfaceImplementation
{
    public required string CallGraphId { get; init; }
    public required string ImplementingMethodId { get; init; }
    public required string InterfaceMethodId { get; init; }
}
