using System.ComponentModel.DataAnnotations;

namespace Shared.Models;
public class PendingRegistration
{
    [Key]
    public int Id { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string Email { get; set; }
    public string GroupsJson { get; set; } = "[]";
    public required Guid LinkId { get; set; }
}
