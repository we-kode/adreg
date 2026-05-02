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
    // Optional container/OU where groups live (relative to SearchBase or full DN)
    public string GroupsContainer { get; set; } = string.Empty;
    // Comma-separated object classes to consider users (e.g. "user,inetOrgPerson")
    public string UsersObjectClasses { get; set; } = "user";
    // Comma-separated object classes to consider groups (e.g. "group,groupOfNames")
    public string GroupsObjectClasses { get; set; } = "group";
    // Optional email address used as sender for invitation links. If set, this address will be
    // written to the user's `mail` attribute in AD instead of the user's own email.
    public string InvitationSenderEmail { get; set; } = string.Empty;
    // If true the LDAP server certificate will not be validated. Use only for testing.
    public bool AllowInvalidCertificate { get; set; } = false;
}
