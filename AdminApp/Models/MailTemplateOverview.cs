using Shared.Models;

namespace AdminApp.Models;

// One row on the Manage Mail Templates landing page: one MailKey with the
// number of stored templates and (for multi-template keys) the name of the
// current default.
public class MailTemplateOverview
{
    public MailKey Key { get; set; } = null!;
    public int TemplateCount { get; set; }
    public string? DefaultTemplateName { get; set; }
}
