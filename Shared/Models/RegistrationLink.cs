using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

public class RegistrationLink
{
    [Key]
    public Guid Id { get; set; }
    public DateTime? ValidUntil { get; set; }
    public bool IsSingleUse { get; set; }
    public bool IsUsed { get; set; }
    public string GroupsJson { get; set; } = "[]";
}
