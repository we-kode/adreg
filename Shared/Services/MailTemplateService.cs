using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shared.Data;
using Shared.Models;

namespace Shared.Services;

// Resolves the MailTemplate row to use for a given MailKey, seeding the
// built-in default if no row exists yet. Also provides helpers used by the
// admin UI to list / create / update / delete templates with the right
// invariants (built-in cannot be deleted, exactly one default per key).
public class MailTemplateService(AppDbContext db, ILogger<MailTemplateService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly ILogger<MailTemplateService> _logger = logger;

    // Load the template that should be used for the given MailKey. If a
    // specific templateId is provided (e.g. the admin picked one in the
    // approve dropdown) it is preferred; otherwise the default template wins.
    // If no template exists at all the built-in seed is inserted on the fly
    // so the system can always send something.
    public async Task<MailTemplate> GetForSendAsync(string mailKey, int? templateId = null)
    {
        if (templateId is int requestedId)
        {
            var requested = await _db.MailTemplates
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == requestedId && t.MailKey == mailKey);
            if (requested != null) return requested;
        }

        var existing = await _db.MailTemplates
            .AsNoTracking()
            .Where(t => t.MailKey == mailKey)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync();

        if (existing != null) return existing;

        // Lazy-seed the built-in template the first time this key is needed.
        var seeded = MailTemplateDefaults.CreateBuiltIn(mailKey);
        _db.MailTemplates.Add(seeded);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Seeded built-in mail template for key {Key}.", mailKey);
        return seeded;
    }

    // Returns all templates for a key, sorting the default first. Seeds the
    // built-in default if nothing exists yet so the admin always sees at
    // least one row to edit.
    public async Task<List<MailTemplate>> ListAsync(string mailKey)
    {
        var rows = await _db.MailTemplates
            .Where(t => t.MailKey == mailKey)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Id)
            .ToListAsync();

        if (rows.Count == 0)
        {
            var seeded = MailTemplateDefaults.CreateBuiltIn(mailKey);
            _db.MailTemplates.Add(seeded);
            await _db.SaveChangesAsync();
            rows.Add(seeded);
        }

        return rows;
    }

    public Task<MailTemplate?> FindAsync(int id) =>
        _db.MailTemplates.FirstOrDefaultAsync(t => t.Id == id);

    // Saves Subject + Body + Name (Name only matters for multi-template keys).
    // Mail key / built-in / default flags are not changed here.
    public async Task UpdateAsync(MailTemplate template, string name, string subject, string bodyHtml)
    {
        template.Name = name;
        template.Subject = subject;
        template.BodyHtml = bodyHtml;
        template.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // Creates a custom template for a multi-template MailKey. The new row is
    // never built-in and only becomes the default if no other template
    // exists yet for this key (defensive — the seeded built-in should always
    // be present by then).
    public async Task<MailTemplate> CreateCustomAsync(string mailKey, string name, string subject, string bodyHtml)
    {
        var key = MailKeys.Find(mailKey)
            ?? throw new InvalidOperationException($"Unknown mail key '{mailKey}'.");
        if (!key.AllowsMultiple)
            throw new InvalidOperationException($"Mail key '{mailKey}' does not allow custom templates.");

        var hasAny = await _db.MailTemplates.AnyAsync(t => t.MailKey == mailKey);
        var now = DateTime.UtcNow;
        var entity = new MailTemplate
        {
            MailKey = mailKey,
            Name = string.IsNullOrWhiteSpace(name) ? "Neue Vorlage" : name.Trim(),
            Subject = subject ?? string.Empty,
            BodyHtml = bodyHtml ?? string.Empty,
            IsBuiltIn = false,
            IsDefault = !hasAny,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.MailTemplates.Add(entity);
        await _db.SaveChangesAsync();
        return entity;
    }

    // Removes a custom template. Built-in templates cannot be deleted. If
    // the deleted template was the default, the lowest-id remaining row
    // for the same key is promoted automatically.
    public async Task<bool> DeleteAsync(int id)
    {
        var template = await _db.MailTemplates.FindAsync(id);
        if (template == null) return false;
        if (template.IsBuiltIn) return false;

        var wasDefault = template.IsDefault;
        var mailKey = template.MailKey;
        _db.MailTemplates.Remove(template);
        await _db.SaveChangesAsync();

        if (wasDefault)
        {
            var promote = await _db.MailTemplates
                .Where(t => t.MailKey == mailKey)
                .OrderBy(t => t.Id)
                .FirstOrDefaultAsync();
            if (promote != null)
            {
                promote.IsDefault = true;
                promote.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
        return true;
    }

    // Promotes the given template as the new default for its MailKey. Any
    // previously-default rows are demoted in a single SaveChanges.
    public async Task<bool> SetDefaultAsync(int id)
    {
        var target = await _db.MailTemplates.FindAsync(id);
        if (target == null) return false;

        var siblings = await _db.MailTemplates
            .Where(t => t.MailKey == target.MailKey && t.Id != target.Id && t.IsDefault)
            .ToListAsync();

        foreach (var s in siblings)
        {
            s.IsDefault = false;
            s.UpdatedAt = DateTime.UtcNow;
        }
        target.IsDefault = true;
        target.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }
}
