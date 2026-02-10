using System.Threading.Tasks;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodCallExtractorFactory
{
    Task<IMethodCallExtractor> Create();
}