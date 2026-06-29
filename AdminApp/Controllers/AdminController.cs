using AdminApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Data;
using Shared.Models;
using Shared.Services;
using System.Text;
using System.Text.Json;

namespace AdminApp.Controllers;

[Authorize]
public class AdminController(
    AppDbContext db,
    MailService mail,
    MailTemplateService mailTemplates,
    ADService adService,
    IConfiguration config,
    IOptions<SmtpSettings> smtpOptions,
    ILogger<AdminController> logger) : Controller
{
    public async Task<IActionResult> Index()
    {
        var list = db.PendingRegistrations
            .OrderByDescending(x => x.Id)
            .Select(p => new PendingRegistrationDto
            {
                Id = p.Id,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Username = p.Username,
                Email = p.Email,
                GroupsJson = p.GroupsJson
            })
            .ToList();

        // For the per-row "approve with template" dropdown.
        var approvedTemplates = await mailTemplates.ListAsync(MailKeys.RegistrationApprovedUser);
        ViewBag.ApprovedTemplates = approvedTemplates;
        ViewBag.DefaultApprovedTemplateId = approvedTemplates
            .FirstOrDefault(t => t.IsDefault)?.Id
            ?? approvedTemplates.FirstOrDefault()?.Id;

        return View(list);
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id, int? templateId)
    {
        var req = await db.PendingRegistrations.FindAsync(id);
        if (req == null) return RedirectToAction("Index");

        try
        {
            var password = DecodePassword(req.PasswordBase64);
            var groups = JsonSerializer.Deserialize<List<string>>(req.GroupsJson) ?? new List<string>();
            var email = req.Email;
            var firstName = req.FirstName;
            var lastName = req.LastName;
            var username = req.Username;

            await adService.CreateUser(req.FirstName, req.LastName, req.Username, password, groups, email);

            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();

            var groupNames = groups.Select(ExtractCn)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            await TrySend(() => mail.SendRegistrationApprovedToUser(
                    email, firstName, lastName, username, groupNames, templateId),
                $"approval notice to {email}");

            TempData["Success"] = "User approved";
        }
        catch (Exception ex)
        {
            HandleError(ex, $"Approve registration {id}", "Fehler beim Erstellen des Nutzers im Domaincontroller/LDAP");
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id)
    {
        var req = await db.PendingRegistrations.FindAsync(id);
        if (req != null)
        {
            var email = req.Email;
            var firstName = req.FirstName;
            var lastName = req.LastName;

            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();

            await TrySend(() => mail.SendRegistrationRejectedToUser(email, firstName, lastName),
                $"rejection notice to {email}");

            TempData["Success"] = "User rejected";
        }
        return RedirectToAction("Index");
    }

    [HttpGet]
    public async Task<IActionResult> CreateLink()
    {
        ViewBag.Groups = await SafeGetGroups();
        return View();
    }

    [HttpGet] public IActionResult CreateGroup() => View();

    [HttpGet] public IActionResult Manage() => View();

    [HttpGet]
    public async Task<IActionResult> ManageMails()
    {
        var stored = await db.MailSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s);
        var defaultAdminRecipient = smtpOptions.Value.From ?? string.Empty;

        var rows = MailKeys.All.Select(k =>
        {
            stored.TryGetValue(k.Id, out var s);
            return new MailSettingRow
            {
                Key = k.Id,
                Audience = k.Audience,
                DisplayName = k.DisplayName,
                Description = k.Description,
                Enabled = s?.Enabled ?? true,
                Recipient = s?.Recipient,
                DefaultRecipientHint = k.Audience == MailAudience.User
                    ? "(Adresse des betroffenen Nutzers)"
                    : string.IsNullOrWhiteSpace(defaultAdminRecipient)
                        ? "(SMTP From-Adresse — derzeit nicht konfiguriert)"
                        : defaultAdminRecipient
            };
        }).ToList();

        return View(rows);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ManageMails(List<MailSettingRow> rows)
    {
        rows ??= new List<MailSettingRow>();

        foreach (var row in rows)
        {
            var meta = MailKeys.Find(row.Key);
            if (meta == null) continue;

            var entity = await db.MailSettings.FindAsync(row.Key);
            if (entity == null)
            {
                entity = new MailSetting { Key = row.Key };
                db.MailSettings.Add(entity);
            }

            entity.Enabled = row.Enabled;

            // Only admin-targeted mails support a recipient override.
            entity.Recipient = meta.Audience == MailAudience.Admin
                ? (string.IsNullOrWhiteSpace(row.Recipient) ? null : row.Recipient.Trim())
                : null;
        }

        await db.SaveChangesAsync();
        TempData["Success"] = "Mail-Einstellungen gespeichert";
        return RedirectToAction("ManageMails");
    }

    // ---------- Mail templates ----------

    [HttpGet]
    public async Task<IActionResult> ManageTemplates()
    {
        var counts = await db.MailTemplates
            .GroupBy(t => t.MailKey)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                DefaultName = g.Where(t => t.IsDefault).Select(t => t.Name).FirstOrDefault(),
            })
            .ToListAsync();

        var byKey = counts.ToDictionary(c => c.Key);

        var rows = MailKeys.All.Select(k =>
        {
            byKey.TryGetValue(k.Id, out var info);
            return new MailTemplateOverview
            {
                Key = k,
                TemplateCount = info?.Count ?? 0,
                DefaultTemplateName = info?.DefaultName,
            };
        }).ToList();

        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Templates(string key)
    {
        var meta = MailKeys.Find(key);
        if (meta == null) return RedirectToAction("ManageTemplates");

        var rows = await mailTemplates.ListAsync(key);
        ViewBag.KeyMeta = meta;
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> EditTemplate(int id)
    {
        var template = await mailTemplates.FindAsync(id);
        if (template == null) return RedirectToAction("ManageTemplates");
        var meta = MailKeys.Find(template.MailKey);

        var vm = new MailTemplateEditViewModel
        {
            Id = template.Id,
            MailKey = template.MailKey,
            Name = template.Name,
            Subject = template.Subject,
            BodyHtml = template.BodyHtml,
            KeyMeta = meta,
            IsBuiltIn = template.IsBuiltIn,
            IsDefault = template.IsDefault,
        };
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTemplate(MailTemplateEditViewModel model)
    {
        var template = await mailTemplates.FindAsync(model.Id);
        if (template == null) return RedirectToAction("ManageTemplates");

        if (!ModelState.IsValid)
        {
            model.KeyMeta = MailKeys.Find(template.MailKey);
            model.IsBuiltIn = template.IsBuiltIn;
            model.IsDefault = template.IsDefault;
            return View(model);
        }

        await mailTemplates.UpdateAsync(template, model.Name.Trim(), model.Subject.Trim(), model.BodyHtml);
        TempData["Success"] = "Vorlage gespeichert";
        return RedirectToAction("Templates", new { key = template.MailKey });
    }

    [HttpGet]
    public IActionResult CreateTemplate(string key)
    {
        var meta = MailKeys.Find(key);
        if (meta == null || !meta.AllowsMultiple)
        {
            TempData["Error"] = "Diese Vorlage erlaubt keine weiteren Varianten.";
            return RedirectToAction("ManageTemplates");
        }

        var seed = MailTemplateDefaults.For(key);
        var vm = new MailTemplateEditViewModel
        {
            Id = 0,
            MailKey = key,
            Name = "Neue Vorlage",
            Subject = seed.Subject,
            BodyHtml = seed.BodyHtml,
            KeyMeta = meta,
            IsBuiltIn = false,
            IsDefault = false,
        };
        return View("CreateTemplate", vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTemplate(MailTemplateEditViewModel model)
    {
        var meta = MailKeys.Find(model.MailKey);
        if (meta == null || !meta.AllowsMultiple)
        {
            TempData["Error"] = "Diese Vorlage erlaubt keine weiteren Varianten.";
            return RedirectToAction("ManageTemplates");
        }

        if (!ModelState.IsValid)
        {
            model.KeyMeta = meta;
            return View(model);
        }

        var created = await mailTemplates.CreateCustomAsync(
            model.MailKey, model.Name.Trim(), model.Subject.Trim(), model.BodyHtml);
        TempData["Success"] = $"Vorlage \"{created.Name}\" angelegt";
        return RedirectToAction("Templates", new { key = model.MailKey });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(int id)
    {
        var template = await mailTemplates.FindAsync(id);
        if (template == null) return RedirectToAction("ManageTemplates");

        if (template.IsBuiltIn)
        {
            TempData["Error"] = "Eingebaute Vorlagen können nicht gelöscht werden.";
            return RedirectToAction("Templates", new { key = template.MailKey });
        }

        var key = template.MailKey;
        await mailTemplates.DeleteAsync(id);
        TempData["Success"] = "Vorlage gelöscht";
        return RedirectToAction("Templates", new { key });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefaultTemplate(int id)
    {
        var template = await mailTemplates.FindAsync(id);
        if (template == null) return RedirectToAction("ManageTemplates");

        await mailTemplates.SetDefaultAsync(id);
        TempData["Success"] = "Standard-Vorlage aktualisiert";
        return RedirectToAction("Templates", new { key = template.MailKey });
    }

    [HttpPost]
    public async Task<IActionResult> CreateGroup(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Group name is required";
            return View();
        }

        try
        {
            var created = await adService.CreateGroup(name, description);
            if (!created)
            {
                TempData["Error"] = "Group already exists";
                return View();
            }

            TempData["Success"] = "Group created";
            return RedirectToAction("Manage");
        }
        catch (Exception ex)
        {
            HandleError(ex, $"Create group {name}", "Failed to create group");
            return View();
        }
    }

    [HttpGet]
    public Task<IActionResult> GetGroups(string? q) =>
        DirectoryJson(() => adService.GetGroups(q), "groups");

    [HttpGet]
    public Task<IActionResult> GetUsers(string? q) =>
        DirectoryJson(() => adService.GetUsers(q), "users");

    [HttpGet]
    public Task<IActionResult> GetUsersInGroup(string groupDn) =>
        DirectoryJson(() => adService.GetUsersInGroup(groupDn), "users in group");

    [HttpGet]
    public Task<IActionResult> GetGroupsForUser(string userDn) =>
        DirectoryJson(() => adService.GetGroupsForUser(userDn), "groups for user");

    [HttpPost]
    public async Task<IActionResult> AddUserToGroup(string userDn, string groupDn)
    {
        if (string.IsNullOrWhiteSpace(userDn) || string.IsNullOrWhiteSpace(groupDn))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new { error = "userDn and groupDn are required" });
        }
        try
        {
            await adService.AddUserToGroup(userDn, groupDn);
            return Json(new { ok = true });
        }
        catch (DirectoryServiceException ex)
        {
            logger.LogError(ex, "Failed to add user {User} to group {Group}", userDn, groupDn);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Json(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> RemoveUserFromGroup(string userDn, string groupDn)
    {
        if (string.IsNullOrWhiteSpace(userDn) || string.IsNullOrWhiteSpace(groupDn))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new { error = "userDn and groupDn are required" });
        }
        try
        {
            await adService.RemoveUserFromGroup(userDn, groupDn);
            return Json(new { ok = true });
        }
        catch (DirectoryServiceException ex)
        {
            logger.LogError(ex, "Failed to remove user {User} from group {Group}", userDn, groupDn);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Json(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetUserDetails(string userDn)
    {
        if (string.IsNullOrWhiteSpace(userDn))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new { error = "userDn is required" });
        }

        try
        {
            var user = await adService.GetUserByDn(userDn);
            if (user == null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Json(new { error = "User not found" });
            }
            return Json(new { dn = user.Dn, name = user.Name, mail = user.Mail, username = user.Username });
        }
        catch (DirectoryServiceException ex)
        {
            logger.LogError(ex, "Failed to load user details for {Dn}", userDn);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Json(new { error = $"Failed to load user details: {ex.Message}" });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SendPasswordReset(string userDn, string email)
    {
        if (string.IsNullOrWhiteSpace(userDn))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new { error = "userDn is required" });
        }
        if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Json(new { error = "A valid email address is required" });
        }

        try
        {
            var user = await adService.GetUserByDn(userDn);
            if (user == null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Json(new { error = "User not found in directory" });
            }

            var resetHours = config.GetValue<int?>("PasswordResetValidHours") ?? 24;
            var link = new PasswordResetLink
            {
                Id = Guid.NewGuid(),
                UserDn = user.Dn,
                Username = user.Username ?? string.Empty,
                ValidUntil = DateTime.UtcNow.AddHours(resetHours),
                IsUsed = false
            };

            db.PasswordResetLinks.Add(link);
            await db.SaveChangesAsync();

            var baseUrl = config.GetValue<string?>("LinkBaseUrl");
            var recipient = email.Trim();
            await mail.SendPasswordResetLink(recipient, $"{baseUrl}/Register/ResetPassword/{link.Id}", link.ValidUntil);
            await mail.SendAdminPasswordResetLinkCreated(link.Username, recipient);

            return Json(new { ok = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send password reset for {Dn}", userDn);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            var message = ex is DirectoryServiceException
                ? $"Directory error: {ex.Message}"
                : "Failed to create or send the reset link.";
            return Json(new { error = message });
        }
    }

    private static bool IsValidEmail(string value)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value.Trim();
        }
        catch
        {
            return false;
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateLink(DateTime? validUntil, bool singleUse, string email, List<string>? selectedGroups)
    {
        try
        {
            var link = new RegistrationLink
            {
                Id = Guid.NewGuid(),
                ValidUntil = validUntil,
                IsSingleUse = singleUse,
                IsUsed = false,
                GroupsJson = JsonSerializer.Serialize(selectedGroups ?? []),
                Email = email.Trim()
            };

            db.Links.Add(link);
            await db.SaveChangesAsync();

            var baseUrl = config.GetValue<string?>("LinkBaseUrl");
            await mail.SendRegistrationLink(email, $"{baseUrl}/Register/{link.Id}");
            await mail.SendAdminRegistrationLinkCreated(email);

            TempData["Success"] = "Link created & mail sent";
        }
        catch (Exception ex)
        {
            HandleError(ex, "Create registration link", "Failed to create or send link");
        }

        return RedirectToAction("Index");
    }

    // ---------- Helpers ----------

    private async Task<List<DirectoryItem>> SafeGetGroups()
    {
        try
        {
            return await adService.GetGroups();
        }
        catch (DirectoryServiceException ex)
        {
            logger.LogError(ex, "Failed to load groups for view");
            TempData["Error"] = $"Could not load directory groups: {ex.Message}";
            return new List<DirectoryItem>();
        }
    }

    private async Task<IActionResult> DirectoryJson(Func<Task<List<DirectoryItem>>> fetch, string what)
    {
        try
        {
            var items = await fetch();
            return Json(items.Select(i => new { dn = i.Dn, name = i.Name, description = i.Description }));
        }
        catch (DirectoryServiceException ex)
        {
            logger.LogError(ex, "Failed to load {What}", what);
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Json(new { error = $"Failed to load {what}: {ex.Message}" });
        }
    }

    private async Task TrySend(Func<Task> send, string what)
    {
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send {What}", what);
        }
    }

    private void HandleError(Exception ex, string action, string userPrefix)
    {
        logger.LogError(ex, "{Action} failed", action);
        var message = ex is DirectoryServiceException
            ? $"{userPrefix}: {ex.Message}"
            : $"Unexpected error during {action.ToLowerInvariant()}.";
        TempData["Error"] = message;
    }

    private static string ExtractCn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
        var firstRdn = dn.Split(',', 2)[0].Trim();
        var eq = firstRdn.IndexOf('=');
        return eq >= 0 ? firstRdn[(eq + 1)..].Trim() : firstRdn;
    }

    private static string DecodePassword(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return string.Empty;
        }
    }
}
