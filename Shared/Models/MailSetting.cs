using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

// Per-scenario configuration for outgoing mails. One row per logical
// notification (see MailKeys). The admin can toggle delivery and (for
// admin-targeted notifications) override the recipient address.
public class MailSetting
{
    [Key]
    [MaxLength(64)]
    public string Key { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    // Optional recipient override. Used for admin-targeted notifications.
    // User-targeted notifications always go to the user's email and ignore this field.
    public string? Recipient { get; set; }
}

public enum MailAudience
{
    User,
    Admin
}

// Catalog of every notification the system can send. Adding a new entry here
// is the only place that needs updating to expose the toggle in the UI.
// AllowsMultiple = true means the admin can maintain several user-defined
// templates for this scenario and pick one as the default. Only the
// "approved" mail uses this today.
public sealed record MailKey(
    string Id,
    MailAudience Audience,
    string DisplayName,
    string Description,
    bool AllowsMultiple = false);

public static class MailKeys
{
    public const string RegistrationLinkUser = "RegistrationLinkUser";
    public const string RegistrationLinkAdmin = "RegistrationLinkAdmin";
    public const string RegistrationReceivedUser = "RegistrationReceivedUser";
    public const string RegistrationReceivedAdmin = "RegistrationReceivedAdmin";
    public const string RegistrationApprovedUser = "RegistrationApprovedUser";
    public const string RegistrationRejectedUser = "RegistrationRejectedUser";
    public const string PasswordResetLinkUser = "PasswordResetLinkUser";
    public const string PasswordResetLinkAdmin = "PasswordResetLinkAdmin";
    public const string PasswordChangedUser = "PasswordChangedUser";
    public const string PasswordChangedAdmin = "PasswordChangedAdmin";

    public static readonly IReadOnlyList<MailKey> All = new[]
    {
        new MailKey(RegistrationLinkUser,     MailAudience.User,  "Registrierungslink → Nutzer",
            "Wird beim Anlegen eines Registrierungslinks an den Nutzer gesendet."),
        new MailKey(RegistrationLinkAdmin,    MailAudience.Admin, "Registrierungslink → Admin",
            "Bestätigung an den Admin, dass ein Registrierungslink angelegt wurde."),
        new MailKey(RegistrationReceivedUser, MailAudience.User,  "Registrierung eingegangen → Nutzer",
            "Wird an den Nutzer gesendet, sobald er die Registrierung abgeschickt hat."),
        new MailKey(RegistrationReceivedAdmin,MailAudience.Admin, "Registrierung eingegangen → Admin",
            "Info an den Admin, dass eine neue Registrierung auf Freigabe wartet."),
        new MailKey(RegistrationApprovedUser, MailAudience.User,  "Registrierung freigegeben → Nutzer",
            "Bestätigung an den Nutzer, dass sein Konto freigegeben wurde.",
            AllowsMultiple: true),
        new MailKey(RegistrationRejectedUser, MailAudience.User,  "Registrierung abgelehnt → Nutzer",
            "Information an den Nutzer, dass seine Registrierung abgelehnt wurde."),
        new MailKey(PasswordResetLinkUser,    MailAudience.User,  "Passwort-Reset-Link → Nutzer",
            "Wird beim Anlegen eines Passwort-Reset-Links an den Nutzer gesendet."),
        new MailKey(PasswordResetLinkAdmin,   MailAudience.Admin, "Passwort-Reset-Link → Admin",
            "Bestätigung an den Admin, dass ein Passwort-Reset-Link angelegt wurde."),
        new MailKey(PasswordChangedUser,      MailAudience.User,  "Passwort geändert → Nutzer",
            "Bestätigung an den Nutzer, dass sein Passwort geändert wurde."),
        new MailKey(PasswordChangedAdmin,     MailAudience.Admin, "Passwort geändert → Admin",
            "Info an den Admin, dass ein Nutzer sein Passwort geändert hat."),
    };

    public static MailKey? Find(string id) =>
        All.FirstOrDefault(k => string.Equals(k.Id, id, StringComparison.Ordinal));
}
