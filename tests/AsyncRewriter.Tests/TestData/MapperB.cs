using System.Threading.Tasks;

public class MapperB : IMapInto<bool, string>
{
    private readonly DbContext _db;

    public MapperB(DbContext db)
    {
        _db = db;
    }

    public void MapInto(string destination, bool source)
    {
        _db.SaveAsync();
    }
}
