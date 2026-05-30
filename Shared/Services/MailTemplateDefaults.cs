using Shared.Models;

namespace Shared.Services;

// Seed content for every MailKey. Used the first time the system needs to
// send a notification for which no MailTemplate row exists yet: a built-in
// template is inserted on-the-fly with these values.
//
// Bodies use Quill-compatible HTML and reference {{PLACEHOLDER}} tokens —
// see MailPlaceholderCatalog for the supported set.
public static class MailTemplateDefaults
{
    public sealed record Seed(string Name, string Subject, string BodyHtml);

    public static Seed For(string key) => key switch
    {
        MailKeys.RegistrationLinkUser => new(
            "Registrierungslink",
            "Ihr Registrierungslink",
            @"<h2>Ihr Registrierungslink</h2>
<p>Hallo,</p>
<p>bitte klicken Sie auf den folgenden Link, um Ihre Registrierung abzuschließen:</p>
<p><a href=""{{LINK}}"">{{LINK}}</a></p>
<p>Sollte der Link nicht funktionieren, kopieren Sie ihn bitte in Ihren Browser.</p>"),

        MailKeys.RegistrationLinkAdmin => new(
            "Registrierungslink angelegt",
            "Registrierungslink angelegt",
            @"<h2>Registrierungslink angelegt</h2>
<p>Ein neuer Registrierungslink wurde erstellt und versendet an:</p>
<p><strong>{{EMAIL}}</strong></p>"),

        MailKeys.RegistrationReceivedUser => new(
            "Registrierung eingegangen",
            "Registrierung eingegangen",
            @"<h2>Registrierung eingegangen</h2>
<p>Hallo {{NAME}},</p>
<p>vielen Dank für Ihre Registrierung. Ihre Daten wurden erfasst und werden in Kürze von einem Administrator geprüft.</p>
<p>Sobald Ihr Konto freigegeben wurde, erhalten Sie eine weitere E-Mail.</p>"),

        MailKeys.RegistrationReceivedAdmin => new(
            "Neue Registrierung",
            "Neue Registrierung",
            @"<h2>Neue Registrierung wartet auf Freigabe</h2>
<p>Eine neue Registrierung ist eingegangen und wartet auf Ihre Prüfung:</p>
<ul>
  <li><strong>Name:</strong> {{NAME}}</li>
  <li><strong>Benutzername:</strong> {{USERNAME}}</li>
  <li><strong>E-Mail:</strong> {{EMAIL}}</li>
  <li><strong>Gruppen:</strong> {{GROUPS}}</li>
</ul>
<p>Bitte melden Sie sich im Admin-Bereich an, um die Registrierung zu prüfen.</p>"),

        MailKeys.RegistrationApprovedUser => new(
            "Standard",
            "Registrierung freigegeben",
            @"<h2>Registrierung freigegeben</h2>
<p>Hallo {{FIRSTNAME}},</p>
<p>Ihre Registrierung wurde durch einen Administrator freigegeben. Ihr Konto ist jetzt aktiv und kann verwendet werden.</p>
<p>Ihr Benutzername lautet: <strong>{{USERNAME}}</strong></p>"),

        MailKeys.RegistrationRejectedUser => new(
            "Registrierung abgelehnt",
            "Registrierung abgelehnt",
            @"<h2>Registrierung abgelehnt</h2>
<p>Hallo {{FIRSTNAME}},</p>
<p>leider wurde Ihre Registrierung durch einen Administrator abgelehnt. Ihr Konto wurde daher nicht angelegt.</p>
<p>Bei Rückfragen wenden Sie sich bitte an Ihren Administrator.</p>"),

        MailKeys.PasswordResetLinkUser => new(
            "Passwort zurücksetzen",
            "Passwort zurücksetzen",
            @"<h2>Passwort zurücksetzen</h2>
<p>Hallo,</p>
<p>ein Administrator hat das Zurücksetzen Ihres Passworts angefordert. Bitte klicken Sie auf den folgenden Link, um ein neues Passwort zu vergeben:</p>
<p><a href=""{{LINK}}"">{{LINK}}</a></p>
<p>Der Link ist gültig bis <strong>{{VALID_UNTIL}}</strong> und kann nur einmal verwendet werden.</p>"),

        MailKeys.PasswordResetLinkAdmin => new(
            "Passwort-Reset angelegt",
            "Passwort-Reset angelegt",
            @"<h2>Passwort-Reset-Link angelegt</h2>
<p>Für folgenden Benutzer wurde ein Passwort-Reset-Link erstellt und versendet:</p>
<ul>
  <li><strong>Benutzer:</strong> {{USERNAME}}</li>
  <li><strong>E-Mail:</strong> {{EMAIL}}</li>
</ul>"),

        MailKeys.PasswordChangedUser => new(
            "Passwort geändert",
            "Ihr Passwort wurde geändert",
            @"<h2>Passwort geändert</h2>
<p>Das Passwort für den Benutzer <strong>{{USERNAME}}</strong> wurde soeben geändert.</p>
<p>Sollten Sie diese Änderung nicht selbst veranlasst haben, wenden Sie sich bitte umgehend an Ihren Administrator.</p>"),

        MailKeys.PasswordChangedAdmin => new(
            "Passwortänderung",
            "Passwortänderung",
            @"<h2>Passwort geändert</h2>
<p>Folgender Benutzer hat soeben sein Passwort geändert:</p>
<ul>
  <li><strong>Benutzer:</strong> {{USERNAME}}</li>
  <li><strong>E-Mail:</strong> {{EMAIL}}</li>
</ul>"),

        _ => new("Vorlage", "(kein Betreff)", "<p>(noch keine Vorlage hinterlegt)</p>"),
    };

    public static MailTemplate CreateBuiltIn(string mailKey)
    {
        var seed = For(mailKey);
        var now = DateTime.UtcNow;
        return new MailTemplate
        {
            MailKey = mailKey,
            Name = seed.Name,
            Subject = seed.Subject,
            BodyHtml = seed.BodyHtml,
            IsDefault = true,
            IsBuiltIn = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
