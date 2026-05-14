using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Models;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace Shared.Services;

// Thrown for any directory operation failure. Wraps the underlying LDAP exception so callers
// can show a friendly message without crashing the app.
public sealed class DirectoryServiceException : Exception
{
    public DirectoryServiceException(string message, Exception? inner = null) : base(message, inner) { }
}

// Single entry point for directory operations. Supports Microsoft Active Directory and generic
// LDAP servers (e.g. OpenLDAP). AD takes priority and is the default; the LDAP profile only kicks
// in when AD__ServerType is explicitly set to "Ldap".
public class ADService
{
    private readonly ADSettings _settings;
    private readonly ILogger<ADService> _logger;
    private readonly DirectoryProfile _profile;

    public ADService(IOptions<ADSettings> options, ILogger<ADService> logger)
    {
        _settings = options.Value;
        _logger = logger;
        _profile = DirectoryProfile.From(_settings);
    }

    // ---------- Public surface ----------

    // Returns true if the group was created, false if it already existed.
    public Task<bool> CreateGroup(string groupName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name must be provided", nameof(groupName));

        return Execute(conn => CreateGroupCore(conn, groupName, description), $"CreateGroup({groupName})");
    }

    public Task CreateUser(string firstName, string lastName, string username, string password, IEnumerable<string>? groups, string email)
    {
        if (string.IsNullOrWhiteSpace(firstName)) throw new ArgumentException("First name required", nameof(firstName));
        if (string.IsNullOrWhiteSpace(lastName)) throw new ArgumentException("Last name required", nameof(lastName));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Username required", nameof(username));

        return Execute<int>(conn =>
        {
            CreateUserCore(conn, firstName, lastName, username, password, groups, email);
            return 0;
        }, $"CreateUser({username})");
    }

    public Task<List<DirectoryItem>> GetGroups(string? search = null) =>
        Execute(conn => SearchGroups(conn, search), $"GetGroups(search={search})");

    public Task<List<DirectoryItem>> GetUsers(string? search = null) =>
        Execute(conn => SearchUsers(conn, search), $"GetUsers(search={search})");

    public Task<List<DirectoryItem>> GetUsersInGroup(string groupDn)
    {
        if (string.IsNullOrWhiteSpace(groupDn)) return Task.FromResult(new List<DirectoryItem>());
        return Execute(conn => UsersInGroup(conn, groupDn), $"GetUsersInGroup({groupDn})");
    }

    public Task<List<DirectoryItem>> GetGroupsForUser(string userDn)
    {
        if (string.IsNullOrWhiteSpace(userDn)) return Task.FromResult(new List<DirectoryItem>());
        return Execute(conn => GroupsForUser(conn, userDn), $"GetGroupsForUser({userDn})");
    }

    public Task<DirectoryUser?> GetUserByDn(string userDn)
    {
        if (string.IsNullOrWhiteSpace(userDn)) return Task.FromResult<DirectoryUser?>(null);
        return Execute(conn => ReadUser(conn, userDn), $"GetUserByDn({userDn})");
    }

    public Task SetUserPassword(string userDn, string password)
    {
        if (string.IsNullOrWhiteSpace(userDn)) throw new ArgumentException("User DN required", nameof(userDn));
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Password required", nameof(password));

        return Execute<int>(conn =>
        {
            if (_profile.RequiresQuotedUtf16Password)
                ReplaceAttribute(conn, userDn, _profile.PasswordAttribute, EncodeAdPassword(password));
            else
                ReplaceAttribute(conn, userDn, _profile.PasswordAttribute, password);
            return 0;
        }, $"SetUserPassword({userDn})");
    }

    private DirectoryUser? ReadUser(LdapConnection conn, string userDn)
    {
        try
        {
            var resp = (SearchResponse)conn.SendRequest(
                new SearchRequest(userDn, "(objectClass=*)", SearchScope.Base, _profile.UserSearchAttributes));
            var entry = resp.Entries.Cast<SearchResultEntry>().FirstOrDefault();
            if (entry == null) return null;
            return new DirectoryUser(
                entry.DistinguishedName,
                FormatUserName(entry),
                GetAttribute(entry, _profile.MailAttribute),
                GetAttribute(entry, _profile.UsernameAttribute));
        }
        catch (DirectoryOperationException)
        {
            return null;
        }
    }

    // ---------- Search ----------

    private List<DirectoryItem> SearchGroups(LdapConnection conn, string? search)
    {
        var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
        var nameFilter = string.IsNullOrWhiteSpace(search)
            ? string.Empty
            : $"(cn=*{EscapeFilter(search)}*)";
        var filter = WrapClassFilter(nameFilter, _profile.GroupObjectClasses);
        return SearchSorted(conn, groupsBase, filter, _profile.GroupSearchAttributes, MapGroup);
    }

    private List<DirectoryItem> SearchUsers(LdapConnection conn, string? search)
    {
        var usersBase = ResolveContainerDn(_settings.UsersContainer);
        var filter = BuildUserSearchFilter(search);
        return SearchSorted(conn, usersBase, filter, _profile.UserSearchAttributes, MapUser);
    }

    private List<DirectoryItem> UsersInGroup(LdapConnection conn, string groupDn)
    {
        // AD: query users by `memberOf` (back-link is reliable). Plain LDAP: read the group's `member` list.
        if (_profile.SupportsMemberOf)
        {
            var usersBase = ResolveContainerDn(_settings.UsersContainer);
            var filter = WrapClassFilter($"(memberOf={EscapeFilter(groupDn)})", _profile.UserObjectClasses);
            return SearchSorted(conn, usersBase, filter, _profile.UserSearchAttributes, MapUser);
        }
        return Sort(ReadGroupMembers(conn, groupDn));
    }

    private List<DirectoryItem> GroupsForUser(LdapConnection conn, string userDn)
    {
        // Searching groups by `member` works on both AD and standard LDAP without the memberof overlay.
        var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
        var filter = WrapClassFilter($"(member={EscapeFilter(userDn)})", _profile.GroupObjectClasses);
        return SearchSorted(conn, groupsBase, filter, _profile.GroupSearchAttributes, MapGroup);
    }

    private static List<DirectoryItem> SearchSorted(
        LdapConnection conn, string baseDn, string filter, string[] attrs,
        Func<SearchResultEntry, DirectoryItem> map)
    {
        var resp = (SearchResponse)conn.SendRequest(new SearchRequest(baseDn, filter, SearchScope.Subtree, attrs));
        return Sort(resp.Entries.Cast<SearchResultEntry>().Select(map));
    }

    private static List<DirectoryItem> Sort(IEnumerable<DirectoryItem> items) =>
        items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static DirectoryItem MapGroup(SearchResultEntry e) =>
        new(e.DistinguishedName, GetAttribute(e, "cn") ?? e.DistinguishedName, GetAttribute(e, "description"));

    private DirectoryItem MapUser(SearchResultEntry e) =>
        new(e.DistinguishedName, FormatUserName(e));

    // UI format: "LastName, FirstName (Username)". Falls back gracefully if attributes are missing.
    private string FormatUserName(SearchResultEntry e)
    {
        var firstName = GetAttribute(e, _profile.FirstNameAttribute);
        var lastName = GetAttribute(e, _profile.LastNameAttribute);
        var username = GetAttribute(e, _profile.UsernameAttribute);
        var displayName = GetAttribute(e, _profile.DisplayNameAttribute);
        var mail = GetAttribute(e, _profile.MailAttribute);

        var hasNames = !string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(lastName);
        var primary = hasNames
            ? $"{lastName}, {firstName}"
            : displayName ?? username ?? mail ?? e.DistinguishedName;

        return string.IsNullOrWhiteSpace(username) ? primary : $"{primary} ({username})";
    }

    private List<DirectoryItem> ReadGroupMembers(LdapConnection conn, string groupDn)
    {
        var groupResp = (SearchResponse)conn.SendRequest(
            new SearchRequest(groupDn, "(objectClass=*)", SearchScope.Base, new[] { _profile.MemberAttribute }));

        var groupEntry = groupResp.Entries.Cast<SearchResultEntry>().FirstOrDefault();
        var memberDns = groupEntry?.Attributes[_profile.MemberAttribute]?
            .GetValues(typeof(string)).Cast<string>().ToList();
        if (memberDns == null || memberDns.Count == 0) return new List<DirectoryItem>();

        var result = new List<DirectoryItem>(memberDns.Count);
        foreach (var memberDn in memberDns)
        {
            try
            {
                var resp = (SearchResponse)conn.SendRequest(
                    new SearchRequest(memberDn, "(objectClass=*)", SearchScope.Base, _profile.UserSearchAttributes));
                var entry = resp.Entries.Cast<SearchResultEntry>().FirstOrDefault();
                if (entry != null) result.Add(MapUser(entry));
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogDebug(ex, "Could not read member {Member} of group {Group}", memberDn, groupDn);
            }
        }
        return result;
    }

    private string BuildUserSearchFilter(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return WrapClassFilter(string.Empty, _profile.UserObjectClasses);

        var s = EscapeFilter(search);
        var nameFilter =
            $"(|({_profile.DisplayNameAttribute}=*{s}*)({_profile.FirstNameAttribute}=*{s}*)" +
            $"({_profile.LastNameAttribute}=*{s}*)({_profile.UsernameAttribute}=*{s}*)({_profile.MailAttribute}=*{s}*))";
        return WrapClassFilter(nameFilter, _profile.UserObjectClasses);
    }

    // ---------- Mutation ----------

    private bool CreateGroupCore(LdapConnection conn, string groupName, string? description)
    {
        var groupsBase = ResolveContainerDn(_settings.GroupsContainer);
        var existsFilter = WrapClassFilter($"(cn={EscapeFilter(groupName)})", _profile.GroupObjectClasses);
        var existing = (SearchResponse)conn.SendRequest(
            new SearchRequest(groupsBase, existsFilter, SearchScope.Subtree, null));
        if (existing.Entries.Count > 0) return false;

        var dn = $"CN={EscapeRdn(groupName)},{groupsBase}";
        var add = new AddRequest(dn);
        add.Attributes.Add(new DirectoryAttribute("objectClass", _profile.GroupObjectClasses));
        add.Attributes.Add(new DirectoryAttribute("cn", groupName));

        if (!string.IsNullOrWhiteSpace(description))
            add.Attributes.Add(new DirectoryAttribute("description", description));

        if (_profile.Type == DirectoryServerType.ActiveDirectory)
        {
            add.Attributes.Add(new DirectoryAttribute("sAMAccountName", groupName));
        }
        else if (_profile.GroupRequiresInitialMember && !string.IsNullOrWhiteSpace(_settings.BindDn))
        {
            // groupOfNames requires at least one member; seed with the bind DN to satisfy the schema.
            add.Attributes.Add(new DirectoryAttribute("member", _settings.BindDn));
        }

        conn.SendRequest(add);
        return true;
    }

    private void CreateUserCore(LdapConnection conn, string firstName, string lastName, string username, string password, IEnumerable<string>? groups, string email)
    {
        var usersContainerDn = ResolveContainerDn(_settings.UsersContainer);
        var groupsContainerDn = ResolveContainerDn(_settings.GroupsContainer);

        var displayName = $"{firstName} {lastName}";
        var userDn = $"CN={EscapeRdn(displayName)},{usersContainerDn}";
        var mail = ResolveMailAttribute(email);

        conn.SendRequest(BuildAddUserRequest(userDn, displayName, firstName, lastName, username, mail));
        TrySetPasswordAndEnable(conn, userDn, password);
        AddUserToGroups(conn, userDn, groupsContainerDn, groups);
    }

    private string ResolveMailAttribute(string username)
    {
        if (!string.IsNullOrWhiteSpace(_settings.InvitationSenderEmail))
            return _settings.InvitationSenderEmail;
        if (username.Contains('@')) return username;
        var domain = DomainFromSearchBase(_settings.SearchBase) ?? "local";
        return $"{username}@{domain}";
    }

    private AddRequest BuildAddUserRequest(string userDn, string displayName, string firstName, string lastName, string username, string mail)
    {
        var add = new AddRequest(userDn);
        add.Attributes.Add(new DirectoryAttribute("objectClass", _profile.UserCreationObjectClasses));
        add.Attributes.Add(new DirectoryAttribute("cn", displayName));
        add.Attributes.Add(new DirectoryAttribute("sn", lastName));
        add.Attributes.Add(new DirectoryAttribute("givenName", firstName));
        add.Attributes.Add(new DirectoryAttribute("displayName", displayName));
        add.Attributes.Add(new DirectoryAttribute("mail", mail));

        if (_profile.Type == DirectoryServerType.ActiveDirectory)
        {
            var sam = username.Contains('@') ? username.Split('@')[0] : username;
            var upn = username.Contains('@')
                ? username
                : $"{sam}@{DomainFromSearchBase(_settings.SearchBase) ?? "local"}";
            add.Attributes.Add(new DirectoryAttribute("sAMAccountName", sam));
            add.Attributes.Add(new DirectoryAttribute("userPrincipalName", upn));
        }
        else
        {
            add.Attributes.Add(new DirectoryAttribute(_profile.UsernameAttribute, username));
        }

        return add;
    }

    private void TrySetPasswordAndEnable(LdapConnection conn, string userDn, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("No password supplied for {UserDn}; account left without password.", userDn);
            return;
        }

        try
        {
            if (_profile.RequiresQuotedUtf16Password)
                ReplaceAttribute(conn, userDn, _profile.PasswordAttribute, EncodeAdPassword(password));
            else
                ReplaceAttribute(conn, userDn, _profile.PasswordAttribute, password);

            if (_profile.RequiresAccountEnable)
                ReplaceAttribute(conn, userDn, "userAccountControl", "512");
        }
        catch (DirectoryOperationException ex)
        {
            // AD typically requires LDAPS for `unicodePwd`. Log and continue so the account still exists.
            _logger.LogWarning(ex, "Could not set password or enable account for {UserDn}.", userDn);
        }
    }

    private void AddUserToGroups(LdapConnection conn, string userDn, string groupsContainerDn, IEnumerable<string>? groups)
    {
        foreach (var groupRef in groups ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(groupRef)) continue;

            try
            {
                var groupDn = LooksLikeDn(groupRef) ? groupRef : ResolveGroupDn(conn, groupsContainerDn, groupRef);
                if (groupDn == null)
                {
                    _logger.LogWarning("Group {Group} not found in directory", groupRef);
                    continue;
                }
                AddAttribute(conn, groupDn, _profile.MemberAttribute, userDn);
            }
            catch (DirectoryOperationException ex)
            {
                _logger.LogError(ex, "Failed to add user {User} to group {Group}", userDn, groupRef);
            }
        }
    }

    private string? ResolveGroupDn(LdapConnection conn, string groupsContainerDn, string cn)
    {
        var filter = WrapClassFilter($"(cn={EscapeFilter(cn)})", _profile.GroupObjectClasses);
        var resp = (SearchResponse)conn.SendRequest(
            new SearchRequest(groupsContainerDn, filter, SearchScope.Subtree, new[] { "distinguishedName" }));
        return resp.Entries.Cast<SearchResultEntry>().FirstOrDefault()?.DistinguishedName;
    }

    private static void ReplaceAttribute(LdapConnection conn, string dn, string name, string value) =>
        SendModify(conn, dn, BuildModification(name, value, DirectoryAttributeOperation.Replace));

    private static void ReplaceAttribute(LdapConnection conn, string dn, string name, byte[] value) =>
        SendModify(conn, dn, BuildModification(name, value, DirectoryAttributeOperation.Replace));

    private static void AddAttribute(LdapConnection conn, string dn, string name, string value) =>
        SendModify(conn, dn, BuildModification(name, value, DirectoryAttributeOperation.Add));

    private static DirectoryAttributeModification BuildModification(string name, string value, DirectoryAttributeOperation op)
    {
        var mod = new DirectoryAttributeModification { Name = name, Operation = op };
        mod.Add(value);
        return mod;
    }

    private static DirectoryAttributeModification BuildModification(string name, byte[] value, DirectoryAttributeOperation op)
    {
        var mod = new DirectoryAttributeModification { Name = name, Operation = op };
        mod.Add(value);
        return mod;
    }

    private static void SendModify(LdapConnection conn, string dn, DirectoryAttributeModification mod) =>
        conn.SendRequest(new ModifyRequest(dn, mod));

    // ---------- Connection / execution ----------

    private async Task<T> Execute<T>(Func<LdapConnection, T> action, string operation)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var conn = CreateConnection();
                return action(conn);
            }
            catch (Exception ex) when (ex is DirectoryOperationException or LdapException or InvalidOperationException)
            {
                _logger.LogError(ex, "{Operation} failed (Server={ServerType}, SearchBase={SearchBase})",
                    operation, _profile.Type, _settings.SearchBase);
                throw new DirectoryServiceException(BuildFriendlyMessage(ex), ex);
            }
        });
    }

    private static string BuildFriendlyMessage(Exception ex) => ex switch
    {
        LdapException l => $"Cannot reach the directory server: {l.Message}",
        DirectoryOperationException d => $"Directory rejected the operation: {d.Message}",
        InvalidOperationException i => i.Message,
        _ => $"Directory operation failed: {ex.Message}"
    };

    private LdapConnection CreateConnection()
    {
        if (string.IsNullOrWhiteSpace(_settings.LdapUrl))
            throw new InvalidOperationException("LDAP URL is not configured (AD__LdapUrl)");

        var (host, port, useSsl) = ParseLdapUrl(_settings.LdapUrl);

        var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port, false, false))
        {
            AuthType = AuthType.Basic
        };
        conn.SessionOptions.ProtocolVersion = 3;

        if (useSsl)
        {
            conn.SessionOptions.SecureSocketLayer = true;
            if (_settings.AllowInvalidCertificate)
                conn.SessionOptions.VerifyServerCertificate += (_, _) => true;
        }

        conn.Credential = new NetworkCredential(_settings.BindDn, _settings.BindPassword);
        conn.Bind();
        return conn;
    }

    private static (string Host, int Port, bool UseSsl) ParseLdapUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var ssl = uri.Scheme.Equals("ldaps", StringComparison.OrdinalIgnoreCase);
            var port = uri.Port > 0 ? uri.Port : (ssl ? 636 : 389);
            return (uri.Host, port, ssl);
        }

        var idx = url.IndexOf(':');
        if (idx > 0 && int.TryParse(url[(idx + 1)..], out var p))
            return (url[..idx], p, false);

        return (url, 389, false);
    }

    // ---------- DN / filter helpers ----------

    // Resolves a configured container value to a full DN.
    // Accepts:
    //   * empty                                              -> SearchBase
    //   * single relative RDN, e.g. "OU=Users"               -> "OU=Users,<SearchBase>"
    //   * nested relative RDNs, e.g. "OU=Users,OU=Domain Users" -> "OU=Users,OU=Domain Users,<SearchBase>"
    //   * full DN containing DC= components                  -> used as-is
    //   * value that already ends with SearchBase            -> used as-is
    private string ResolveContainerDn(string containerSetting)
    {
        var baseDn = _settings.SearchBase?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(containerSetting)) return baseDn;

        var trimmed = containerSetting.Trim().TrimEnd(',');

        if (!string.IsNullOrWhiteSpace(baseDn) &&
            (trimmed.Equals(baseDn, StringComparison.OrdinalIgnoreCase) ||
             trimmed.EndsWith($",{baseDn}", StringComparison.OrdinalIgnoreCase)))
            return trimmed;

        // A value is only an absolute DN when it carries a DC= component (the domain root).
        // Otherwise it is a relative path — even if it contains nested OU=/CN= segments — and
        // must be anchored under SearchBase.
        if (HasDomainComponent(trimmed)) return trimmed;

        return string.IsNullOrWhiteSpace(baseDn) ? trimmed : $"{trimmed},{baseDn}";
    }

    private static bool HasDomainComponent(string dn) =>
        dn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Any(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase));

    private static string WrapClassFilter(string innerFilter, string[] objectClasses)
    {
        var classFilter = BuildObjectClassFilter(objectClasses);
        if (string.IsNullOrEmpty(classFilter))
            return string.IsNullOrEmpty(innerFilter) ? "(objectClass=*)" : innerFilter;
        if (string.IsNullOrEmpty(innerFilter)) return classFilter;
        return $"(&{classFilter}{innerFilter})";
    }

    private static string BuildObjectClassFilter(string[] classes)
    {
        if (classes.Length == 0) return string.Empty;
        if (classes.Length == 1) return $"(objectClass={EscapeFilter(classes[0])})";
        return "(|" + string.Concat(classes.Select(c => $"(objectClass={EscapeFilter(c)})")) + ")";
    }

    private static bool LooksLikeDn(string value) => value.Contains('=') && value.Contains(',');

    private static string? GetAttribute(SearchResultEntry entry, string name)
    {
        var attr = entry.Attributes[name];
        if (attr == null) return null;
        return attr.GetValues(typeof(string)).Cast<string>().FirstOrDefault();
    }

    private static byte[] EncodeAdPassword(string pwd) =>
        Encoding.Unicode.GetBytes($"\"{pwd}\"");

    private static string EscapeRdn(string input) =>
        input.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=");

    private static string EscapeFilter(string input) =>
        input.Replace("\\", "\\5c").Replace("*", "\\2a").Replace("(", "\\28").Replace(")", "\\29").Replace("\0", "\\00");

    private static string? DomainFromSearchBase(string searchBase)
    {
        if (string.IsNullOrWhiteSpace(searchBase)) return null;
        try
        {
            var parts = searchBase.Split(',')
                .Select(p => p.Trim())
                .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[3..])
                .ToArray();
            return parts.Length == 0 ? null : string.Join('.', parts);
        }
        catch
        {
            return null;
        }
    }

    // ---------- Profile ----------

    // Per-server-type configuration (attribute names, object classes, password rules).
    // Active Directory takes priority and is the default; the LDAP profile covers OpenLDAP-style servers.
    private sealed class DirectoryProfile
    {
        public DirectoryServerType Type { get; private init; }
        public string[] UserObjectClasses { get; private init; } = Array.Empty<string>();
        public string[] GroupObjectClasses { get; private init; } = Array.Empty<string>();
        public string[] UserCreationObjectClasses { get; private init; } = Array.Empty<string>();
        public string UsernameAttribute { get; private init; } = "sAMAccountName";

        public string FirstNameAttribute => "givenName";
        public string LastNameAttribute => "sn";
        public string MailAttribute => "mail";
        public string MemberAttribute => "member";
        public string DisplayNameAttribute => Type == DirectoryServerType.ActiveDirectory ? "displayName" : "cn";
        public string PasswordAttribute => Type == DirectoryServerType.ActiveDirectory ? "unicodePwd" : "userPassword";
        public bool RequiresQuotedUtf16Password => Type == DirectoryServerType.ActiveDirectory;
        public bool RequiresAccountEnable => Type == DirectoryServerType.ActiveDirectory;
        // memberOf is reliable on AD; on plain LDAP it depends on the memberof overlay, so we resolve via the group's member list.
        public bool SupportsMemberOf => Type == DirectoryServerType.ActiveDirectory;
        public bool GroupRequiresInitialMember =>
            Type != DirectoryServerType.ActiveDirectory &&
            GroupObjectClasses.Any(c => c.Equals("groupOfNames", StringComparison.OrdinalIgnoreCase));

        public string[] UserSearchAttributes => new[]
        {
            "distinguishedName", DisplayNameAttribute, FirstNameAttribute, LastNameAttribute, UsernameAttribute, MailAttribute
        };

        public string[] GroupSearchAttributes => new[] { "distinguishedName", "cn", "description", MemberAttribute };

        public static DirectoryProfile From(ADSettings settings)
        {
            var isAd = settings.ServerType == DirectoryServerType.ActiveDirectory;

            var defaultUserClasses = isAd ? new[] { "user" } : new[] { "inetOrgPerson" };
            var defaultGroupClasses = isAd ? new[] { "group" } : new[] { "groupOfNames" };
            var defaultUsername = isAd ? "sAMAccountName" : "uid";

            var userClasses = ParseCsv(settings.UsersObjectClasses, defaultUserClasses);
            var groupClasses = ParseCsv(settings.GroupsObjectClasses, defaultGroupClasses);

            // Object classes used when adding a new user. AD always needs the canonical chain;
            // for LDAP we merge any extra classes the operator configured with the base inetOrgPerson chain.
            var userCreationClasses = isAd
                ? new[] { "top", "person", "organizationalPerson", "user" }
                : MergeWithDefaults(userClasses, new[] { "top", "person", "organizationalPerson", "inetOrgPerson" });

            return new DirectoryProfile
            {
                Type = settings.ServerType,
                UserObjectClasses = userClasses,
                GroupObjectClasses = groupClasses,
                UserCreationObjectClasses = userCreationClasses,
                UsernameAttribute = string.IsNullOrWhiteSpace(settings.UsernameAttribute)
                    ? defaultUsername
                    : settings.UsernameAttribute.Trim()
            };
        }

        private static string[] ParseCsv(string csv, string[] fallback)
        {
            if (string.IsNullOrWhiteSpace(csv)) return fallback;
            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? fallback : parts;
        }

        private static string[] MergeWithDefaults(string[] configured, string[] defaults)
        {
            if (configured.Length == 0) return defaults;
            return defaults.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
