using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Analyzer;

public class MethodCallExtractorFactory : IMethodCallExtractorFactory
{
    public Task<IMethodCallExtractor> Create()
    {
        return Task.FromResult<IMethodCallExtractor>(new MethodCallExtractor());
    }
}