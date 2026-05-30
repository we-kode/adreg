using Shared.Models;

namespace AdminApp.Models;

// View model used by the Manage Mails page. Carries the catalog metadata
// from MailKeys plus the persisted toggle/recipient.
public class MailSettingRow
{
    public string Key { get; set; } = string.Empty;
    public MailAudience Audience { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string? Recipient { get; set; }
    public string DefaultRecipientHint { get; set; } = string.Empty;
}
