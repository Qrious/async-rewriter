using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;

namespace AsyncRewriter.Analyzer;

public class MethodExtractorFactory : IMethodExtractorFactory
{
    public Task<IMethodExtractor> Create()
    {
        return Task.FromResult<IMethodExtractor>(new MethodExtractor());
    }
}