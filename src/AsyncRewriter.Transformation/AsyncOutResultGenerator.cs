namespace AsyncRewriter.Transformation;

/// <summary>
/// Generates the AsyncOutResult&lt;T&gt; helper class source code.
/// </summary>
public static class AsyncOutResultGenerator
{
    public const string ClassName = "AsyncOutResult";
    public const string DefaultNamespace = "AsyncRewriter.Generated";

    /// <summary>
    /// Generates the AsyncOutResult&lt;T&gt; class source code.
    /// </summary>
    public static string Generate(string ns = DefaultNamespace)
    {
        return $@"namespace {ns};

/// <summary>
/// Wraps the result of an async method that originally had out parameters with a bool return (Try* pattern).
/// </summary>
public class AsyncOutResult<T>
{{
    public T Value {{ get; }}
    public bool HasValue {{ get; }}

    public AsyncOutResult(T value, bool hasValue)
    {{
        Value = value;
        HasValue = hasValue;
    }}

    public bool TryGetValue(out T value)
    {{
        value = HasValue ? Value : default!;
        return HasValue;
    }}
}}
";
    }
}
