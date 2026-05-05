using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

public class PasswordResetLink
{
    [Key]
    public Guid Id { get; set; }

    public string UserDn { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public DateTime ValidUntil { get; set; }

    public bool IsUsed { get; set; }
}
