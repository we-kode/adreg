namespace Shared.Models;

public class PendingRegistrationDto
{
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string GroupsJson { get; set; } = "[]";

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
        // Take the first RDN and strip the "CN=" prefix if present.
        var firstRdn = dn.Split(',', 2)[0].Trim();
        var eq = firstRdn.IndexOf('=');
        return eq >= 0 ? firstRdn[(eq + 1)..].Trim() : firstRdn;
    }
}
