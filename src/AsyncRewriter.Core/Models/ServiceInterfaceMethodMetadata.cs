using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Metadata indicating that a method is declared on an interface whose name ends with
/// <c>Service</c>, or that it is a concrete implementation of such an interface method.
/// Used to seed the flooding roots so that all methods on service interfaces are
/// transformed to async.
/// </summary>
public class ServiceInterfaceMethodMetadata : IGraphMetadata<ServiceInterfaceMethodMetadata>
{
    public static readonly ServiceInterfaceMethodMetadata None = new() { IsServiceInterfaceMethod = false, InterfaceName = null };

    public bool IsServiceInterfaceMethod { get; init; }

    /// <summary>
    /// The name of the <c>*Service</c> interface that declared (or that is implemented by)
    /// this method.
    /// </summary>
    public string? InterfaceName { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        ["IsServiceInterfaceMethod"] = IsServiceInterfaceMethod.ToString(),
        ["InterfaceName"] = InterfaceName ?? "",
    };

    public static ServiceInterfaceMethodMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary) => new()
    {
        IsServiceInterfaceMethod = dictionary.TryGetValue("IsServiceInterfaceMethod", out var v) && bool.Parse(v),
        InterfaceName = dictionary.TryGetValue("InterfaceName", out var n) && n != "" ? n : null,
    };
}
