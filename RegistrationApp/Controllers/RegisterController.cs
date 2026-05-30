using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shared.Data;
using Shared.Models;
using Shared.Services;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RegistrationApp.Controllers;

[Route("Register")]
public class RegisterController : Controller
{
    private readonly AppDbContext _db;
    private readonly ADService _adService;
    private readonly MailService _mail;
    private readonly ILogger<RegisterController> _logger;

    public RegisterController(AppDbContext db, ADService adService, MailService mail, ILogger<RegisterController> logger)
    {
        _db = db;
        _adService = adService;
        _mail = mail;
        _logger = logger;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Index(Guid id)
    {
        var link = await _db.Links.FindAsync(id);

        if (link == null) return Content("Invalid link");
        if (link.ValidUntil < DateTime.UtcNow) return Content("Link expired");
        if (link.IsSingleUse && link.IsUsed) return Content("Link already used");

        return View(link);
    }

    [HttpPost("{id}")]
    public async Task<IActionResult> Submit(Guid id, string firstname, string lastname, string password, string confirm)
    {
        var link = await _db.Links.FindAsync(id);
        if (link == null) return Content("Invalid link");

        // basic server side validation
        if (string.IsNullOrWhiteSpace(firstname) || string.IsNullOrWhiteSpace(lastname))
            return BadRequest("Firstname and lastname are required");

        if (password != confirm)
            return BadRequest("Passwords do not match");

        var pwdOk = ValidatePassword(password, out var pwdMsg);
        if (!pwdOk) return BadRequest(pwdMsg);

        // build username suggestion and ensure uniqueness among pending registrations
        var baseUsername = MakeUsername(firstname, lastname);
        var username = GenerateAvailableUsername(baseUsername);

        _db.PendingRegistrations.Add(new PendingRegistration
        {
            FirstName = firstname,
            LastName = lastname,
            Username = username,
            PasswordBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)),
            GroupsJson = link.GroupsJson,
            LinkId = link.Id,
            Email = link.Email
        });

        link.IsUsed = true;

        await _db.SaveChangesAsync();

        var groupNames = ExtractGroupNames(link.GroupsJson);

        await TrySend(() => _mail.SendRegistrationReceivedToUser(link.Email, firstname, lastname),
            $"registration confirmation to {link.Email}");
        await TrySend(() => _mail.SendAdminNewRegistration(firstname, lastname, username, link.Email, groupNames),
            "new-registration notice to admin");

        return RedirectToAction("Submitted");
    }

    [HttpGet("Submitted")]
    public IActionResult Submitted()
    {
        return View();
    }

    [HttpGet("ResetPassword/{id}")]
    public async Task<IActionResult> ResetPassword(Guid id)
    {
        var link = await _db.PasswordResetLinks.FindAsync(id);
        if (link == null) return Content("Invalid link");
        if (link.IsUsed) return Content("Link already used");
        if (link.ValidUntil < DateTime.UtcNow) return Content("Link expired");

        return View(link);
    }

    [HttpPost("ResetPassword/{id}")]
    public async Task<IActionResult> ResetPasswordSubmit(Guid id, string password, string confirm)
    {
        var link = await _db.PasswordResetLinks.FindAsync(id);
        if (link == null) return Content("Invalid link");
        if (link.IsUsed) return Content("Link already used");
        if (link.ValidUntil < DateTime.UtcNow) return Content("Link expired");

        if (password != confirm)
        {
            ViewBag.Error = "Passwörter stimmen nicht überein";
            return View("ResetPassword", link);
        }

        if (!ValidatePassword(password, out var pwdMsg))
        {
            ViewBag.Error = pwdMsg;
            return View("ResetPassword", link);
        }

        try
        {
            await _adService.SetUserPassword(link.UserDn, password);
        }
        catch (DirectoryServiceException ex)
        {
            _logger.LogError(ex, "Failed to update password for {Dn}", link.UserDn);
            ViewBag.Error = $"Passwort konnte nicht aktualisiert werden: {ex.Message}";
            return View("ResetPassword", link);
        }

        link.IsUsed = true;
        await _db.SaveChangesAsync();

        await NotifyPasswordChanged(link);

        return RedirectToAction("ResetPasswordDone");
    }

    private async Task NotifyPasswordChanged(PasswordResetLink link)
    {
        string? userEmail = null;
        try
        {
            var user = await _adService.GetUserByDn(link.UserDn);
            userEmail = user?.Mail;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load user mail for password-change notification ({Dn})", link.UserDn);
        }

        if (!string.IsNullOrWhiteSpace(userEmail))
        {
            await TrySend(() => _mail.SendPasswordChangedToUser(userEmail, link.Username),
                $"password-change confirmation to {userEmail}");
        }

        await TrySend(() => _mail.SendAdminPasswordChanged(link.Username, userEmail ?? "(unbekannt)"),
            "password-change notice to admin");
    }

    private static List<string> ExtractGroupNames(string groupsJson)
    {
        try
        {
            var dns = JsonSerializer.Deserialize<List<string>>(groupsJson) ?? new List<string>();
            return dns.Select(dn =>
            {
                if (string.IsNullOrWhiteSpace(dn)) return string.Empty;
                var firstRdn = dn.Split(',', 2)[0].Trim();
                var eq = firstRdn.IndexOf('=');
                return eq >= 0 ? firstRdn[(eq + 1)..].Trim() : firstRdn;
            })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        }
        catch
        {
            return new List<string>();
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
            _logger.LogError(ex, "Failed to send {What}", what);
        }
    }

    [HttpGet("ResetPasswordDone")]
    public IActionResult ResetPasswordDone() => View();

    [HttpGet("CheckUsername")]
    public IActionResult CheckUsername(string firstname, string lastname)
    {
        if (string.IsNullOrWhiteSpace(firstname) || string.IsNullOrWhiteSpace(lastname))
            return Json(new { username = string.Empty });

        var baseUsername = MakeUsername(firstname, lastname);
        var suggestion = GenerateAvailableUsername(baseUsername);
        return Json(new { username = suggestion });
    }

    private static string MakeUsername(string firstname, string lastname)
    {
        var raw = (firstname + "." + lastname).ToLowerInvariant();
        // remove invalid chars except dot and alphanumerics
        var cleaned = Regex.Replace(raw, "[^a-z0-9\\.]", "");
        // collapse multiple dots
        cleaned = Regex.Replace(cleaned, "\\.+", ".");
        return cleaned.Trim('.');
    }

    private string GenerateAvailableUsername(string baseUsername)
    {
        var existing = _db.PendingRegistrations
            .Where(p => p.Username.StartsWith(baseUsername))
            .Select(p => p.Username)
            .ToList();

        if (!existing.Contains(baseUsername)) return baseUsername;

        var max = 0;
        foreach (var u in existing)
        {
            if (u == baseUsername) { if (max < 1) max = 1; continue; }
            var suffix = u.Substring(baseUsername.Length);
            if (int.TryParse(suffix, out var v)) if (v > max) max = v;
        }

        return baseUsername + (max + 1);
    }

    private static bool ValidatePassword(string pwd, out string message)
    {
        message = "";
        if (string.IsNullOrEmpty(pwd) || pwd.Length < 8)
        {
            message = "Password must be at least 8 characters long";
            return false;
        }

        if (!Regex.IsMatch(pwd, "[A-Z]")) { message = "Password must contain an uppercase letter"; return false; }
        if (!Regex.IsMatch(pwd, "[a-z]")) { message = "Password must contain a lowercase letter"; return false; }
        if (!Regex.IsMatch(pwd, "[0-9]")) { message = "Password must contain a digit"; return false; }
        if (!Regex.IsMatch(pwd, "[^a-zA-Z0-9]")) { message = "Password must contain a special character"; return false; }

        return true;
    }
}
