using Shared.Data;
using Shared.Models;

namespace Shared.Services;

public class AuthService
{
    private readonly AppDbContext _db;

    public AuthService(AppDbContext db) => _db = db;

    public bool HasAdminUser => _db.AdminUsers.Any();

    public async Task CreateAdmin(string username, string password)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(password);

        _db.AdminUsers.Add(new AdminUser
        {
            Username = username,
            PasswordHash = hash
        });

        await _db.SaveChangesAsync();
    }

    public bool Login(string username, string password)
    {
        var user = _db.AdminUsers.FirstOrDefault(x => x.Username == username);
        return user != null && BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }
}