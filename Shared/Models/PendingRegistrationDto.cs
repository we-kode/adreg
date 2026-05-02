namespace Shared.Models;

public class PendingRegistrationDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string GroupsJson { get; set; } = "[]";
}
