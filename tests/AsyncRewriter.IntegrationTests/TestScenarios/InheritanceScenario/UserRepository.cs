using System;

namespace InheritanceScenario;

public class UserRepository : BaseRepository
{
    public override string GetById(int id)
    {
        Console.WriteLine("Getting user...");
        return base.GetById(id);
    }

    public string GetUserByEmail(string email)
    {
        Console.WriteLine($"Searching for user with email {email}...");
        return GetById(1);
    }
}
