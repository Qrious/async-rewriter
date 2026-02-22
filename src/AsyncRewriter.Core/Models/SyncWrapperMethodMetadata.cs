using System.Collections.Generic;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Core.Models;

/// <summary>
/// Metadata indicating that a method is a sync wrapper — it accepts a
/// <c>Func&lt;Task&gt;</c> or <c>Func&lt;Task&lt;T&gt;&gt;</c> parameter and returns
/// <c>void</c> or <c>T</c> respectively.
/// The transformer uses this to skip adding <c>async</c>/<c>await</c> and only change
/// the return type, letting the existing lambda body do the async work.
/// </summary>
public class SyncWrapperMethodMetadata : IGraphMetadata<SyncWrapperMethodMetadata>
{
    public static readonly SyncWrapperMethodMetadata None = new() { IsSyncWrapper = false, Reason = null };

    public bool IsSyncWrapper { get; init; }
    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, string> ToDictionary() => new Dictionary<string, string>
    {
        ["IsSyncWrapper"] = IsSyncWrapper.ToString(),
        ["Reason"] = Reason ?? "",
    };

    public static SyncWrapperMethodMetadata FromDictionary(IReadOnlyDictionary<string, string> dictionary) => new()
    {
        IsSyncWrapper = dictionary.TryGetValue("IsSyncWrapper", out var v) && bool.Parse(v),
        Reason = dictionary.TryGetValue("Reason", out var r) && r != "" ? r : null,
    };
}
