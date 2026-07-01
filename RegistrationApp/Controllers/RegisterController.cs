using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ADSettings _adSettings;

    public RegisterController(AppDbContext db, ADService adService, MailService mail, ILogger<RegisterController> logger, IOptions<ADSettings> adSettings)
    {
        _db = db;
        _adService = adService;
        _mail = mail;
        _logger = logger;
        _adSettings = adSettings.Value;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Index(Guid id)
    {
        var link = await _db.Links.FindAsync(id);

        if (link == null) return Content("Invalid link");
        if (link.ValidUntil < DateTime.UtcNow) return Content("Link expired");
        if (link.IsSingleUse && link.IsUsed) return Content("Link already used");

        await SetPolicyViewBag();
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

        firstname = firstname.Trim();
        lastname = lastname.Trim();

        if (password != confirm)
            return BadRequest("Passwords do not match");

        var baseUsername = MakeUsername(firstname, lastname);
        var username = GenerateAvailableUsername(baseUsername);

        var pwdCheck = await ValidatePasswordAsync(password, username, firstname, lastname);
        if (!pwdCheck.IsValid) return BadRequest(pwdCheck.Message);

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

        await SetPolicyViewBag();
        ViewBag.DisplayName = await SafeGetUserName(link.UserDn);
        return View(link);
    }

    [HttpPost("ResetPassword/{id}")]
    public async Task<IActionResult> ResetPasswordSubmit(Guid id, string password, string confirm)
    {
        var link = await _db.PasswordResetLinks.FindAsync(id);
        if (link == null) return Content("Invalid link");
        if (link.IsUsed) return Content("Link already used");
        if (link.ValidUntil < DateTime.UtcNow) return Content("Link expired");

        // Make the policy and display name available so the form (incl. its live checks) re-renders correctly on error.
        await SetPolicyViewBag();
        var displayName = await SafeGetUserName(link.UserDn);
        ViewBag.DisplayName = displayName;

        if (password != confirm)
        {
            ViewBag.Error = "Passwörter stimmen nicht überein";
            return View("ResetPassword", link);
        }

        var pwdCheck = await ValidatePasswordAsync(password, link.Username, displayName, string.Empty);
        if (!pwdCheck.IsValid)
        {
            ViewBag.Error = pwdCheck.Message;
            return View("ResetPassword", link);
        }

        try
        {
            await _adService.SetUserPassword(link.UserDn, password);
        }
        catch (DirectoryServiceException ex)
        {
            _logger.LogError(ex, "Failed to update password for {Dn}", link.UserDn);
            ViewBag.Error = $"Der Domaincontroller hat das Passwort nicht akzeptiert. Fehler: {ex.Message}";
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

        var baseUsername = MakeUsername(firstname.Trim(), lastname.Trim());
        var suggestion = GenerateAvailableUsername(baseUsername);
        return Json(new { username = suggestion });
    }

    [HttpGet("GetPasswordPolicy")]
    public async Task<IActionResult> GetPasswordPolicyEndpoint()
    {
        var (minLength, requireComplexity) = await GetEffectivePolicyAsync();
        return Json(new { minLength, requireComplexity });
    }

    // Resolves the password policy that is actually enforced: the live directory policy if
    // it could be read, otherwise the configured fallbacks. Never throws.
    private async Task<(int MinLength, bool RequireComplexity)> GetEffectivePolicyAsync()
    {
        try
        {
            var policy = await _adService.GetPasswordPolicy();
            var minLength = policy.IsConfigured ? policy.MinLength : (_adSettings.FallbackMinPasswordLength ?? 8);
            var requireComplexity = policy.IsConfigured ? policy.RequireComplexity : (_adSettings.FallbackRequirePasswordComplexity ?? true);
            return (minLength, requireComplexity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch password policy, using configured fallback defaults");
            return (_adSettings.FallbackMinPasswordLength ?? 8, _adSettings.FallbackRequirePasswordComplexity ?? true);
        }
    }

    // Exposes the effective policy to the view so the hint list can be rendered server-side,
    // matching the configured policy without a hardcoded default or a flash before JS runs.
    private async Task SetPolicyViewBag()
    {
        var (minLength, requireComplexity) = await GetEffectivePolicyAsync();
        ViewBag.MinLength = minLength;
        ViewBag.RequireComplexity = requireComplexity;
    }

    // Best-effort lookup of the directory display name. Used both for the password check and
    // to feed the client-side validation, so the "must not contain your name" rule fires on
    // input rather than only after submit. Never throws.
    private async Task<string> SafeGetUserName(string userDn)
    {
        try
        {
            var user = await _adService.GetUserByDn(userDn);
            return user?.Name ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load user name for {Dn}", userDn);
            return string.Empty;
        }
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

    private async Task<(bool IsValid, string Message)> ValidatePasswordAsync(string pwd, string username, string firstname, string lastname)
    {
        if (string.IsNullOrEmpty(pwd))
            return (false, "Password cannot be empty");

        if (!string.IsNullOrEmpty(username) && pwd.Contains(username, StringComparison.OrdinalIgnoreCase))
            return (false, "Password must not contain your username");
            
        if (!string.IsNullOrEmpty(firstname) && pwd.Contains(firstname, StringComparison.OrdinalIgnoreCase))
            return (false, "Password must not contain your first name");
            
        if (!string.IsNullOrEmpty(lastname) && pwd.Contains(lastname, StringComparison.OrdinalIgnoreCase))
            return (false, "Password must not contain your last name");

        var (minLength, requireComplexity) = await GetEffectivePolicyAsync();

        if (pwd.Length < minLength)
            return (false, $"Password must be at least {minLength} characters long");

        if (requireComplexity)
        {
            if (!Regex.IsMatch(pwd, "[A-Z]")) return (false, "Password must contain an uppercase letter");
            if (!Regex.IsMatch(pwd, "[a-z]")) return (false, "Password must contain a lowercase letter");
            if (!Regex.IsMatch(pwd, "[0-9]")) return (false, "Password must contain a digit");
            if (!Regex.IsMatch(pwd, "[^a-zA-Z0-9]")) return (false, "Password must contain a special character");
        }

        return (true, "");
    }
}
