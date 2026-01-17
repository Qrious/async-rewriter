using System;

namespace MultipleInheritors;

public class ApiProvider : IDataProvider
{
    public string FetchData()
    {
        return CallExternalApi();
    }

    private string CallExternalApi()
    {
        Console.WriteLine("Calling external API...");
        return "api-data";
    }
}
