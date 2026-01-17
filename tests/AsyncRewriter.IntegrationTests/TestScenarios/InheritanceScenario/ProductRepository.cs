using System;

namespace InheritanceScenario;

public class ProductRepository : BaseRepository
{
    public override void Save(string data)
    {
        Console.WriteLine("Saving product...");
        base.Save(data);
    }

    public string GetProductsByCategory(string category)
    {
        Console.WriteLine($"Getting products in category {category}...");
        return GetById(100);
    }
}
