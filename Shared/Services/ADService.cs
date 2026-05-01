using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.DirectoryServices.Protocols;
using System.Net;

namespace Shared.Services;

public class ADService
{
    private readonly ADSettings _settings;
    private readonly ILogger<ADService> _logger;

    public ADService(IOptions<ADSettings> options, ILogger<ADService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    private LdapConnection CreateConnection()
    {
        var identifier = new LdapDirectoryIdentifier(_settings.LdapUrl, 389, false, false);
        var conn = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic,
            SessionOptions = { ProtocolVersion = 3 }
        };

        conn.Credential = new NetworkCredential(_settings.BindDn, _settings.BindPassword);
        conn.Bind();

        return conn;
    }

    public async Task CreateUser(string name, string email, string password, List<string> groups)
    {
        // Active Directory operations are synchronous in System.DirectoryServices.Protocols
        await Task.Run(() =>
        {
            using var conn = CreateConnection();

            // Determine users container DN
            var usersContainer = _settings.UsersContainer;
            var baseDn = _settings.SearchBase;

            string containerDn;
            if (string.IsNullOrEmpty(usersContainer))
                containerDn = baseDn;
            else if (usersContainer.EndsWith($",{baseDn}", StringComparison.OrdinalIgnoreCase) || usersContainer.Equals(baseDn, StringComparison.OrdinalIgnoreCase))
                containerDn = usersContainer;
            else
                containerDn = $"{usersContainer},{baseDn}";

            // Create a CN-safe name
            var cn = name;

            // Build DN
            var userDn = $"CN={EscapeRdn(cn)},{containerDn}";

            var attrs = new DirectoryAttributeCollection
            {
                new DirectoryAttribute("objectClass", new[] { "top", "person", "organizationalPerson", "user" }),
                new DirectoryAttribute("cn", cn),
                new DirectoryAttribute("displayName", name),
                new DirectoryAttribute("sAMAccountName", email.Split('@')[0]),
                new DirectoryAttribute("userPrincipalName", email),
                new DirectoryAttribute("mail", email)
            };

            var addRequest = new AddRequest(userDn);
            foreach (DirectoryAttribute a in attrs)
            {
                addRequest.Attributes.Add(a);
            }

            try
            {
                conn.SendRequest(addRequest);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogError(ex, "Failed to create AD user {UserDn}", userDn);
                throw;
            }

            // Set password and enable account. Setting password requires LDAPS and specific attribute unicodePwd.
            try
            {
                // set unicodePwd - requires secure connection (LDAPS)
                var pwd = EncodePassword(password);
                var modPwd = new DirectoryAttributeModification
                {
                    Name = "unicodePwd",
                    Operation = DirectoryAttributeOperation.Replace
                };
                modPwd.Add(pwd);

                var modifyPwd = new ModifyRequest(userDn, modPwd);
                conn.SendRequest(modifyPwd);

                // enable account: set userAccountControl to NORMAL_ACCOUNT (512)
                var modUac = new DirectoryAttributeModification
                {
                    Name = "userAccountControl",
                    Operation = DirectoryAttributeOperation.Replace
                };
                modUac.Add("512");
                var modifyUac = new ModifyRequest(userDn, modUac);
                conn.SendRequest(modifyUac);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogWarning(ex, "Could not set password or enable account for {UserDn}. This often requires LDAPS.", userDn);
            }

            // Add to groups
            foreach (var groupDnOrIdentifier in groups ?? Enumerable.Empty<string>())
            {
                try
                {
                    // if provided value looks like a DN (contains = and ,) use it directly, otherwise search by cn
                    string groupDn = groupDnOrIdentifier;
                    if (!groupDn.Contains("=", StringComparison.OrdinalIgnoreCase))
                    {
                        // search for group by cn
                        var search = new SearchRequest(_settings.SearchBase, $"(&(objectClass=group)(cn={EscapeFilter(groupDnOrIdentifier)}))", SearchScope.Subtree, null);
                        var resp = (SearchResponse)conn.SendRequest(search);
                        var entry = resp.Entries.Cast<SearchResultEntry>().FirstOrDefault();
                        if (entry == null)
                        {
                            _logger.LogWarning("Group {Group} not found in AD", groupDnOrIdentifier);
                            continue;
                        }
                        groupDn = entry.DistinguishedName;
                    }

                    var mod = new DirectoryAttributeModification
                    {
                        Name = "member",
                        Operation = DirectoryAttributeOperation.Add
                    };
                    mod.Add(userDn);
                    var modification = new ModifyRequest(groupDn, mod);
                    conn.SendRequest(modification);
                }
                catch (DirectoryOperationException ex)
                {
                    _logger.LogError(ex, "Failed to add user to group {Group} for user {User}", groupDnOrIdentifier, userDn);
                }
            }
        });
    }

    public async Task<List<(string Oid, string Name)>> GetRoles()
    {
        return await Task.Run(() =>
        {
            using var conn = CreateConnection();

            var search = new SearchRequest(_settings.SearchBase, "(objectClass=group)", SearchScope.Subtree, new[] { "distinguishedName", "cn" });
            var resp = (SearchResponse)conn.SendRequest(search);

            var result = new List<(string, string)>();

            foreach (SearchResultEntry entry in resp.Entries)
            {
                var dn = entry.DistinguishedName;
                var cn = entry.Attributes["cn"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault() ?? dn;
                result.Add((dn, cn));
            }

            return result;
        });
    }

    private static byte[] EncodePassword(string pwd)
    {
        // unicodePwd needs to be a UTF-16LE quoted string
        var quoted = "\"" + pwd + "\"";
        return System.Text.Encoding.Unicode.GetBytes(quoted);
    }

    private static string EscapeRdn(string input)
    {
        // basic escaping for DN RDNs
        return input.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");
    }

    private static string EscapeFilter(string input)
    {
        return input.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");
    }
}
