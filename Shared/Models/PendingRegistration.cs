using System.ComponentModel.DataAnnotations;

namespace Shared.Models;
public class PendingRegistration
{
    [Key]
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    // Username that will be created in AD (e.g. firstname.lastname or firstname.lastname1)
    public required string Username { get; set; }

    // Password stored temporarily as Base64 string until admin approves
    public required string PasswordBase64 { get; set; }

    public string GroupsJson { get; set; } = "[]";
    public required Guid LinkId { get; set; }
}
