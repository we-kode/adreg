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

    private string ResolveContainerDn(string containerSetting)
    {
        var baseDn = _settings.SearchBase ?? string.Empty;
        if (string.IsNullOrWhiteSpace(containerSetting))
            return baseDn;

        // If the container setting already looks like a full DN (contains =), return as-is
        if (containerSetting.Contains('=') && containerSetting.Contains(','))
            return containerSetting;

        // If it already ends with the base dn, return as-is
        if (!string.IsNullOrWhiteSpace(baseDn) && (containerSetting.Equals(baseDn, StringComparison.OrdinalIgnoreCase)
                                                    || containerSetting.EndsWith($"," + baseDn, StringComparison.OrdinalIgnoreCase)))
            return containerSetting;

        // Otherwise treat it as relative and append the configured SearchBase
        if (string.IsNullOrWhiteSpace(baseDn))
            return containerSetting;

        return containerSetting + "," + baseDn;
    }

    private string BuildObjectClassFilter(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return "";
        var parts = csv.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (parts.Length == 0) return "";
        if (parts.Length == 1) return $"(objectClass={EscapeFilter(parts[0])})";
        return "(|" + string.Join(string.Empty, parts.Select(p => $"(objectClass={EscapeFilter(p)})")) + ")";
    }

    public async Task<bool> CreateGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name must be provided", nameof(groupName));

        return await Task.Run(() =>
        {
            using var conn = CreateConnection();

            var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
            var groupClassFilter = BuildObjectClassFilter(_settings.GroupsObjectClasses);

            // Check if a group with that CN already exists
            var searchFilter = string.IsNullOrWhiteSpace(groupClassFilter)
                ? $"(cn={EscapeFilter(groupName)})"
                : $"(&{groupClassFilter}(cn={EscapeFilter(groupName)}))";

            var search = new SearchRequest(groupsBase, searchFilter, SearchScope.Subtree, null);
            var resp = (SearchResponse)conn.SendRequest(search);
            if (resp.Entries.Count > 0)
            {
                return false; // already exists
            }

            // Create the group under the resolved groups container
            var dn = $"CN={EscapeRdn(groupName)},{groupsBase}";

            var add = new AddRequest(dn,
                 new DirectoryAttribute("objectClass", "group"),
                 new DirectoryAttribute("cn", groupName),
                 new DirectoryAttribute("sAMAccountName", groupName)
             );

            try
            {
                conn.SendRequest(add);
                return true;
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogError(ex, "Failed to create AD group {GroupDn}", dn);
                throw;
            }
        });
    }

    private LdapConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_settings.LdapUrl))
            throw new InvalidOperationException("LDAP URL is not configured in ADSettings.LdapUrl");

        // parse possible formats: ldap://host:port, ldaps://host:port, host:port or just host
        string host = _settings.LdapUrl;
        int port = -1;
        bool useSsl = false;

        if (Uri.TryCreate(_settings.LdapUrl, UriKind.Absolute, out var uri))
        {
            host = uri.Host;
            port = uri.Port;
            useSsl = uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // try host:port
            var idx = _settings.LdapUrl.IndexOf(':');
            if (idx > 0)
            {
                host = _settings.LdapUrl.Substring(0, idx);
                if (int.TryParse(_settings.LdapUrl.Substring(idx + 1), out var p))
                    port = p;
            }
        }

        if (port == -1)
            port = useSsl ? 636 : 389;

        var identifier = new LdapDirectoryIdentifier(host, port, false, false);
        var conn = new LdapConnection(identifier)
        {
            AuthType = AuthType.Basic
        };

        // ProtocolVersion must be set for many servers
        conn.SessionOptions.ProtocolVersion = 3;

        if (useSsl)
        {
            conn.SessionOptions.SecureSocketLayer = true;

            if (_settings.AllowInvalidCertificate)
            {
                // Accept any server certificate (only for testing)
                conn.SessionOptions.VerifyServerCertificate += (c, cert) => true;
            }
        }

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

            // Determine users/groups container DNs (supports full DN or OU relative to SearchBase)
            var usersContainerDn = ResolveContainerDn(_settings.UsersContainer);
            var groupsContainerDn = ResolveContainerDn(_settings.GroupsContainer);

            // Create a CN-safe name and build DN
            var cn = name;
            var userDn = $"CN={EscapeRdn(cn)},{usersContainerDn}";

            string sam = email.Contains('@') ? email.Split('@')[0] : email;
            string upn;
            if (email.Contains('@'))
            {
                upn = email;
            }
            else
            {
                var domain = DomainFromSearchBase(_settings.SearchBase) ?? "local";
                upn = sam + "@" + domain;
            }

            // The `mail` attribute should be the invitation sender's email when configured.
            // Otherwise fall back to the provided email (if it contains '@') or the computed UPN.
            var mail = !string.IsNullOrWhiteSpace(_settings.InvitationSenderEmail)
                ? _settings.InvitationSenderEmail
                : (email.Contains('@') ? email : upn);

            var attrs = new DirectoryAttributeCollection
            {
                new DirectoryAttribute("objectClass", new[] { "top", "person", "organizationalPerson", "user" }),
                new DirectoryAttribute("cn", cn),
                new DirectoryAttribute("displayName", name),
                new DirectoryAttribute("sn", name),
                new DirectoryAttribute("sAMAccountName", sam),
                new DirectoryAttribute("userPrincipalName", upn),
                new DirectoryAttribute("mail", mail)
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
                        var groupClassFilter = BuildObjectClassFilter(_settings.GroupsObjectClasses);
                        var gfilter = string.IsNullOrWhiteSpace(groupClassFilter)
                            ? $"(cn={EscapeFilter(groupDnOrIdentifier)})"
                            : $"(&{groupClassFilter}(cn={EscapeFilter(groupDnOrIdentifier)}))";

                        var search = new SearchRequest(groupsContainerDn, gfilter, SearchScope.Subtree, null);
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
            try
            {
                using var conn = CreateConnection();

                var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
                var groupClassFilter = BuildObjectClassFilter(_settings.GroupsObjectClasses);
                var filter = string.IsNullOrWhiteSpace(groupClassFilter) ? "(objectClass=group)" : groupClassFilter;

                var search = new SearchRequest(groupsBase, filter, SearchScope.Subtree, new[] { "distinguishedName", "cn" });
                var resp = (SearchResponse)conn.SendRequest(search);

                var result = new List<(string, string)>();

                foreach (SearchResultEntry entry in resp.Entries)
                {
                    var dn = entry.DistinguishedName;
                    var cn = entry.Attributes["cn"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault() ?? dn;
                    result.Add((dn, cn));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRoles failed (SearchBase={SearchBase})", _settings.SearchBase);
                return new List<(string, string)>();
            }
        });
    }

    public async Task<List<(string Dn, string Name)>> GetRoles(string? search)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();

                var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
                var groupClassFilter = BuildObjectClassFilter(_settings.GroupsObjectClasses);

                var filter = string.IsNullOrWhiteSpace(search)
                    ? (string.IsNullOrWhiteSpace(groupClassFilter) ? "(objectClass=group)" : groupClassFilter)
                    : (string.IsNullOrWhiteSpace(groupClassFilter)
                        ? $"(&(cn=*{EscapeFilter(search)}*))"
                        : $"(&{groupClassFilter}(cn=*{EscapeFilter(search)}*))");

                var searchReq = new SearchRequest(groupsBase, filter, SearchScope.Subtree, new[] { "distinguishedName", "cn" });
                var resp = (SearchResponse)conn.SendRequest(searchReq);

                var result = new List<(string, string)>();
                foreach (SearchResultEntry entry in resp.Entries)
                {
                    var dn = entry.DistinguishedName;
                    var cn = entry.Attributes["cn"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault() ?? dn;
                    result.Add((dn, cn));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetRoles(search={Search}) failed (SearchBase={SearchBase})", search, _settings.SearchBase);
                return new List<(string, string)>();
            }
        });
    }

    public async Task<List<(string Dn, string Name)>> GetUsers(string? search)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();

                var usersBase = ResolveContainerDn(_settings.UsersContainer);
                var userClassFilter = BuildObjectClassFilter(_settings.UsersObjectClasses);

                var filter = string.IsNullOrWhiteSpace(search)
                    ? (string.IsNullOrWhiteSpace(userClassFilter) ? "(objectClass=user)" : userClassFilter)
                    : (string.IsNullOrWhiteSpace(userClassFilter)
                        ? $"(&(|(displayName=*{EscapeFilter(search)}*)(sAMAccountName=*{EscapeFilter(search)}*)(mail=*{EscapeFilter(search)}*)))"
                        : $"(&{userClassFilter}(|(displayName=*{EscapeFilter(search)}*)(sAMAccountName=*{EscapeFilter(search)}*)(mail=*{EscapeFilter(search)}*)))");

                var searchReq = new SearchRequest(usersBase, filter, SearchScope.Subtree, new[] { "distinguishedName", "displayName", "sAMAccountName", "mail" });
                var resp = (SearchResponse)conn.SendRequest(searchReq);

                var result = new List<(string, string)>();
                foreach (SearchResultEntry entry in resp.Entries)
                {
                    var dn = entry.DistinguishedName;
                    var name = entry.Attributes["displayName"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? entry.Attributes["sAMAccountName"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? entry.Attributes["mail"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? dn;
                    result.Add((dn, name));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUsers(search={Search}) failed (SearchBase={SearchBase})", search, _settings.SearchBase);
                return new List<(string, string)>();
            }
        });
    }

    public async Task<List<(string Dn, string Name)>> GetUsersInGroup(string groupDn)
    {
        if (string.IsNullOrWhiteSpace(groupDn)) return new List<(string, string)>();

        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();

                // Search for users that are memberOf the provided group DN
                var usersBase = ResolveContainerDn(_settings.UsersContainer);
                var userClassFilter = BuildObjectClassFilter(_settings.UsersObjectClasses);
                var filter = string.IsNullOrWhiteSpace(userClassFilter)
                    ? $"(&(memberOf={EscapeFilter(groupDn)})(objectClass=user))"
                    : $"(&{userClassFilter}(memberOf={EscapeFilter(groupDn)}))";

                var searchReq = new SearchRequest(usersBase, filter, SearchScope.Subtree, new[] { "distinguishedName", "displayName", "sAMAccountName", "mail" });
                var resp = (SearchResponse)conn.SendRequest(searchReq);

                var result = new List<(string, string)>();
                foreach (SearchResultEntry entry in resp.Entries)
                {
                    var dn = entry.DistinguishedName;
                    var name = entry.Attributes["displayName"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? entry.Attributes["sAMAccountName"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? entry.Attributes["mail"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault()
                               ?? dn;
                    result.Add((dn, name));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetUsersInGroup(groupDn={GroupDn}) failed (SearchBase={SearchBase})", groupDn, _settings.SearchBase);
                return new List<(string, string)>();
            }
        });
    }

    public async Task<List<(string Dn, string Name)>> GetGroupsForUser(string userDn)
    {
        if (string.IsNullOrWhiteSpace(userDn)) return new List<(string, string)>();

        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();

                // Search for groups where member attribute contains the user DN
                var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
                var groupClassFilter = BuildObjectClassFilter(_settings.GroupsObjectClasses);
                var filter = string.IsNullOrWhiteSpace(groupClassFilter)
                    ? $"(&(member={EscapeFilter(userDn)})(objectClass=group))"
                    : $"(&{groupClassFilter}(member={EscapeFilter(userDn)}))";

                var searchReq = new SearchRequest(groupsBase, filter, SearchScope.Subtree, new[] { "distinguishedName", "cn" });
                var resp = (SearchResponse)conn.SendRequest(searchReq);

                var result = new List<(string, string)>();
                foreach (SearchResultEntry entry in resp.Entries)
                {
                    var dn = entry.DistinguishedName;
                    var cn = entry.Attributes["cn"]?.GetValues(typeof(string))?.Cast<string>().FirstOrDefault() ?? dn;
                    result.Add((dn, cn));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetGroupsForUser(userDn={UserDn}) failed (SearchBase={SearchBase})", userDn, _settings.SearchBase);
                return new List<(string, string)>();
            }
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

    private static string? DomainFromSearchBase(string searchBase)
    {
        if (string.IsNullOrWhiteSpace(searchBase)) return null;
        // parse DC=example,DC=com -> example.com
        try
        {
            var parts = searchBase.Split(',')
                .Select(p => p.Trim())
                .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Substring(3))
                .ToArray();
            if (parts.Length == 0) return null;
            return string.Join('.', parts);
        }
        catch
        {
            return null;
        }
    }
}
