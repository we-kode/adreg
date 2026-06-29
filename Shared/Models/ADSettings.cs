namespace Shared.Models;

public enum DirectoryServerType
{
    // Microsoft Active Directory. Default and prioritized server type.
    ActiveDirectory = 0,
    // Generic LDAP server (e.g. OpenLDAP, 389 Directory Server).
    Ldap = 1
}

public class ADSettings
{
    // Server type: ActiveDirectory (default, prioritized) or Ldap (e.g. OpenLDAP).
    public DirectoryServerType ServerType { get; set; } = DirectoryServerType.ActiveDirectory;

    // LDAP server URL, e.g. "ldap://ad.example.com:389", "ldaps://dc.example.com:636" or just "ad.example.com".
    public string LdapUrl { get; set; } = string.Empty;

    // Bind DN (e.g. "CN=Bind User,OU=Service Accounts,DC=example,DC=com").
    public string BindDn { get; set; } = string.Empty;

    public string BindPassword { get; set; } = string.Empty;

    // Base DN for searching users and groups (e.g. "DC=example,DC=com").
    public string SearchBase { get; set; } = string.Empty;

    // Container/OU where new users are created (relative to SearchBase or full DN). Optional.
    public string UsersContainer { get; set; } = string.Empty;

    // Container/OU where groups live (relative to SearchBase or full DN). Optional.
    public string GroupsContainer { get; set; } = string.Empty;

    // Comma-separated object classes that identify user entries. Optional override of the per-server defaults.
    public string UsersObjectClasses { get; set; } = string.Empty;

    // Comma-separated object classes that identify group entries. Optional override of the per-server defaults.
    public string GroupsObjectClasses { get; set; } = string.Empty;

    // Optional sender address written to the user's `mail` attribute. If empty, the user's own address is used.
    public string InvitationSenderEmail { get; set; } = string.Empty;

    // If true, the LDAP server certificate is not validated. Use only for testing.
    public bool AllowInvalidCertificate { get; set; } = false;

    // Optional override for the username attribute. Defaults: sAMAccountName for AD, uid for LDAP.
    public string UsernameAttribute { get; set; } = string.Empty;

    // Fallback minimum password length if the domain controller policy cannot be read.
    public int? FallbackMinPasswordLength { get; set; }

    // Fallback complexity requirement if the domain controller policy cannot be read.
    public bool? FallbackRequirePasswordComplexity { get; set; }
}
