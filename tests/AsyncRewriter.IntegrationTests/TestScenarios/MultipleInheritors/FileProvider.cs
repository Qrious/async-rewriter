using System;

namespace MultipleInheritors;

public class FileProvider : IDataProvider
{
    public string FetchData()
    {
        return ReadFromFile();
    }

    private string ReadFromFile()
    {
        Console.WriteLine("Reading from file...");
        return "file-data";
    }
}
