using System.Threading.Tasks;

public interface IMapInto<TSource, TTarget>
{
    void MapInto(TTarget destination, TSource source);
}

public interface IMapIntoAsync<TSource, TTarget>
{
    Task MapInto(TTarget destination, TSource source);
}

public class DbContext
{
    public Task SaveAsync() => Task.CompletedTask;
}

public class Validator
{
    public Task ValidateAsync() => Task.CompletedTask;
}
