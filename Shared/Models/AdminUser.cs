using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

public class AdminUser
{
    [Key]
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
}
