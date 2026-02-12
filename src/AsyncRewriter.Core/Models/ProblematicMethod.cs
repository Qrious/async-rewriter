namespace AsyncRewriter.Core.Models;

/// <summary>
/// Represents a method that is problematic because its implementing method's return type
/// changed during async flooding but the interface method is external.
/// </summary>
public record ProblematicMethod(
    string InterfaceMethodId,
    MethodNode? InterfaceMethod,
    MethodNode OriginalImpl,
    MethodNode AsyncImpl);
