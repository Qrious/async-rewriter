using System.Threading.Tasks;

public class MapperA : IMapInto<int, string>
{
    private readonly IMapInto<bool, string> _mapperB;

    public MapperA(IMapInto<bool, string> mapperB)
    {
        _mapperB = mapperB;
    }

    public void MapInto(string destination, int source)
    {
        _mapperB.MapInto(destination, source > 0);
    }
}
