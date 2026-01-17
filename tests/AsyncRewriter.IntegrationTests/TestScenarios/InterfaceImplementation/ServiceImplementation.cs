using System;

namespace InterfaceImplementation;

public class ServiceImplementation : IService
{
    public string GetData()
    {
        return FetchFromDatabase();
    }

    public void ProcessData(string data)
    {
        SaveToDatabase(data);
    }

    private string FetchFromDatabase()
    {
        // This would be an async call in real scenario
        Console.WriteLine("Fetching from database...");
        return "data";
    }

    private void SaveToDatabase(string data)
    {
        // This would be an async call in real scenario
        Console.WriteLine($"Saving {data} to database...");
    }
}
