namespace Shared.Models;

public class PasswordPolicy
{
    public bool IsConfigured { get; set; }
    public int MinLength { get; set; }
    public bool RequireComplexity { get; set; }
}
