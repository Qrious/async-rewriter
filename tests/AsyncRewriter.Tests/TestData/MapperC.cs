using System.Threading.Tasks;

public class MapperC : IMapInto<double, string>
{
    private readonly Validator _validator;

    public MapperC(Validator validator)
    {
        _validator = validator;
    }

    public void MapInto(string destination, double source)
    {
        _validator.ValidateAsync();
    }
}
