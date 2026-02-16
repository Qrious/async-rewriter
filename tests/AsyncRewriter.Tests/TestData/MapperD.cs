using System.Threading.Tasks;

public class MapperD : IMapInto<long, string>
{
    public void MapInto(string destination, long source)
    {
        destination = source.ToString();
    }
}
