namespace AdminApp.Models;

public class RegistrationLinkDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateTime? ValidUntil { get; set; }
    public bool IsSingleUse { get; set; }
    public bool IsUsed { get; set; }
    public string GroupsJson { get; set; } = "[]";

    // Whether the link's validity window has elapsed. Links without a
    // ValidUntil never expire.
    public bool IsExpired => ValidUntil.HasValue && ValidUntil.Value < DateTime.UtcNow;

    // A single-use link that has already been consumed can no longer be used.
    public bool IsConsumed => IsSingleUse && IsUsed;

    // "Active" means the invitation can still be used to register.
    public bool IsActive => !IsExpired && !IsConsumed;

    public IReadOnlyList<string> GroupNames
    {
        get
        {
            try
            {
                var dns = System.Text.Json.JsonSerializer.Deserialize<List<string>>(GroupsJson) ?? new List<string>();
                return dns.Select(ExtractCn).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    private static string ExtractCn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
        var firstRdn = dn.Split(',', 2)[0].Trim();
        var eq = firstRdn.IndexOf('=');
        return eq >= 0 ? firstRdn[(eq + 1)..].Trim() : firstRdn;
    }
}
