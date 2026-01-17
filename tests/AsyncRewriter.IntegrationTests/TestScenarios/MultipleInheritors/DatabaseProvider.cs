using System;

namespace MultipleInheritors;

public class DatabaseProvider : IDataProvider
{
    public string FetchData()
    {
        return QueryDatabase();
    }

    private string QueryDatabase()
    {
        Console.WriteLine("Querying database...");
        return "database-data";
    }
}
