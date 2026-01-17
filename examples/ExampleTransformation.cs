// Example: Before async transformation

namespace Example;

public class UserService
{
    private readonly DatabaseContext _db;

    public UserService(DatabaseContext db)
    {
        _db = db;
    }

    // This method should be async
    public User GetUser(int id)
    {
        var user = _db.Users.Find(id);
        return user;
    }

    // This method will need to be async due to flooding
    public UserProfile GetUserProfile(int id)
    {
        var user = GetUser(id);
        return new UserProfile
        {
            Name = user.Name,
            Email = user.Email
        };
    }

    // This method will also need to be async
    public List<UserProfile> GetAllUserProfiles()
    {
        var profiles = new List<UserProfile>();
        var userIds = _db.Users.Select(u => u.Id).ToList();

        foreach (var id in userIds)
        {
            profiles.Add(GetUserProfile(id));
        }

        return profiles;
    }
}

// After async transformation, the code would look like:

/*
using System.Threading.Tasks;

namespace Example;

public class UserService
{
    private readonly DatabaseContext _db;

    public UserService(DatabaseContext db)
    {
        _db = db;
    }

    // Root async method
    public async Task<User> GetUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        return user;
    }

    // Flooded - needs to be async because it calls GetUser
    public async Task<UserProfile> GetUserProfile(int id)
    {
        var user = await GetUser(id);
        return new UserProfile
        {
            Name = user.Name,
            Email = user.Email
        };
    }

    // Flooded - needs to be async because it calls GetUserProfile
    public async Task<List<UserProfile>> GetAllUserProfiles()
    {
        var profiles = new List<UserProfile>();
        var userIds = await _db.Users.Select(u => u.Id).ToListAsync();

        foreach (var id in userIds)
        {
            profiles.Add(await GetUserProfile(id));
        }

        return profiles;
    }
}
*/
