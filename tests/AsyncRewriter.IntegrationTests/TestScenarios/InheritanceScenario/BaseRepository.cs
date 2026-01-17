using System;

namespace InheritanceScenario;

public abstract class BaseRepository
{
    public virtual string GetById(int id)
    {
        return LoadFromStorage(id);
    }

    public virtual void Save(string data)
    {
        WriteToStorage(data);
    }

    protected string LoadFromStorage(int id)
    {
        // Would be async in real scenario
        Console.WriteLine($"Loading item {id}...");
        return $"item-{id}";
    }

    protected void WriteToStorage(string data)
    {
        // Would be async in real scenario
        Console.WriteLine($"Writing {data}...");
    }
}
