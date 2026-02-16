using System.Threading.Tasks;

public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<bool, string> _mapperB;
    private readonly IMapInto<double, string> _mapperC;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(
        IMapInto<int, string> mapperA,
        IMapInto<bool, string> mapperB,
        IMapInto<double, string> mapperC,
        IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperB = mapperB;
        _mapperC = mapperC;
        _mapperD = mapperD;
    }

    public void HandleRequest()
    {
        _mapperA.MapInto("hello", 42);
        _mapperB.MapInto("world", true);
        _mapperC.MapInto("foo", 3.14);
        _mapperD.MapInto("bar", 100L);
    }
}
