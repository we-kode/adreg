using System.ComponentModel.DataAnnotations;

namespace Shared.Models;

// A single editable mail template. One row per concrete template; one template
// is linked to exactly one MailKey scenario. Built-in templates are seeded
// automatically the first time the scenario is needed and are protected from
// deletion (IsBuiltIn = true). For scenarios that allow multiple variants
// (e.g. RegistrationApprovedUser) admins can add their own templates and pick
// one as default via IsDefault.
public class MailTemplate
{
    [Key]
    public int Id { get; set; }

    [MaxLength(64)]
    public string MailKey { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    // Rich HTML produced by the Quill editor.
    public string BodyHtml { get; set; } = string.Empty;

    // Marks the template that should be picked by default for its MailKey.
    // For single-template scenarios this is always true.
    public bool IsDefault { get; set; }

    // Built-in templates cannot be deleted (only edited).
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
