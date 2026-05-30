using System.Net;
using System.Text.RegularExpressions;

namespace Shared.Services;

// Catalog of supported {{PLACEHOLDER}} tokens. Used both at runtime (to know
// which keys to substitute) and by the admin UI to show the available chips.
public static class MailPlaceholderCatalog
{
    public sealed record Entry(string Token, string Description);

    public static readonly IReadOnlyList<Entry> All = new[]
    {
        new Entry("FIRSTNAME",  "Vorname des Nutzers"),
        new Entry("LASTNAME",   "Nachname des Nutzers"),
        new Entry("NAME",       "Vor- und Nachname"),
        new Entry("USERNAME",   "Benutzername / Login"),
        new Entry("EMAIL",      "E-Mail-Adresse des Nutzers"),
        new Entry("GROUPS",     "Zugewiesene Gruppen (Komma-getrennt)"),
        new Entry("LINK",       "Aktionslink (Registrierung / Passwort-Reset)"),
        new Entry("VALID_UNTIL","Gültigkeitsende eines Links (UTC)"),
    };
}

// Data bag for one render pass. Only set the fields that make sense for the
// current scenario; everything else falls back to an empty string.
public class MailPlaceholders
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public IEnumerable<string>? Groups { get; set; }
    public string? Link { get; set; }
    public DateTime? ValidUntilUtc { get; set; }

    public string FullName =>
        string.Join(' ', new[] { FirstName, LastName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string ResolveSubject(string template) => Apply(template, encode: false);
    public string ResolveBody(string template)    => Apply(template, encode: true);

    private static readonly Regex TokenPattern = new(@"\{\{\s*([A-Z_][A-Z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    // Replace {{TOKEN}} occurrences. For HTML bodies we HTML-encode the value;
    // subjects are plain text and get the raw value.
    private string Apply(string template, bool encode)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return TokenPattern.Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            var resolved = ResolveToken(token);
            // Unknown tokens stay verbatim so admins notice typos in their templates.
            if (resolved == null) return match.Value;
            return encode ? WebUtility.HtmlEncode(resolved) : resolved;
        });
    }

    private string? ResolveToken(string token) => token switch
    {
        "FIRSTNAME"   => FirstName ?? string.Empty,
        "LASTNAME"    => LastName ?? string.Empty,
        "NAME"        => FullName,
        "USERNAME"    => Username ?? string.Empty,
        "EMAIL"       => Email ?? string.Empty,
        "GROUPS"      => Groups == null ? string.Empty : string.Join(", ", Groups),
        "LINK"        => Link ?? string.Empty,
        "VALID_UNTIL" => ValidUntilUtc?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? string.Empty,
        _             => null,
    };
}
