using System.Collections.Generic;
using System.Linq;

namespace MultipleInheritors;

public class DataAggregator
{
    private readonly List<IDataProvider> _providers;

    public DataAggregator(List<IDataProvider> providers)
    {
        _providers = providers;
    }

    public string AggregateData()
    {
        var results = _providers.Select(p => p.FetchData()).ToList();
        return string.Join(", ", results);
    }
}
