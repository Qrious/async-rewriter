using System.Threading.Tasks;

namespace AsyncRewriter.Core.Interfaces;

public interface IMethodExtractorFactory
{
  Task<IMethodExtractor> Create();
}