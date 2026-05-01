namespace Shared.Models;

public class ADSettings
{
    // LDAP server address, e.g. "ldap://ad.example.com:389" or just "ad.example.com"
    public string LdapUrl { get; set; } = string.Empty;
    // Bind DN / user (e.g. "CN=Bind User,OU=Service Accounts,DC=example,DC=com")
    public string BindDn { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    // Base DN for searching users and groups, e.g. "DC=example,DC=com"
    public string SearchBase { get; set; } = string.Empty;
    // Optional container/OU where new users should be created (relative to SearchBase or full DN)
    public string UsersContainer { get; set; } = string.Empty;
}
