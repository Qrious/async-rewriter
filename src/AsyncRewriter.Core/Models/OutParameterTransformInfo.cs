using System.Collections.Generic;

namespace AsyncRewriter.Core.Models;

public class OutParameterTransformInfo
{
    public required bool IsTryPattern { get; init; }
    public required List<int> OutParameterIndices { get; init; }
    public required List<string> OutParameterTypes { get; init; }
    public required List<string> OutParameterNames { get; init; }
    public required string NewAsyncReturnType { get; init; }
}