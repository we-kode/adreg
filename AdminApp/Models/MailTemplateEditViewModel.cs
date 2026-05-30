using System.ComponentModel.DataAnnotations;
using Shared.Models;

namespace AdminApp.Models;

// Backing model for the rich-text edit page. The body HTML produced by the
// Quill editor lives in BodyHtml; everything else is metadata used to render
// the surrounding UI.
public class MailTemplateEditViewModel
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string MailKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "Name darf nicht leer sein")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Betreff darf nicht leer sein")]
    [MaxLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required(ErrorMessage = "Inhalt darf nicht leer sein")]
    public string BodyHtml { get; set; } = string.Empty;

    // For display only; comes from the MailKeys catalog, not the form post.
    public MailKey? KeyMeta { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsDefault { get; set; }
}
