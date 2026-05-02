using Microsoft.AspNetCore.Mvc;
using Shared.Data;
using Shared.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace RegistrationApp.Controllers;

[Route("Register")]
public class RegisterController : Controller
{
    private readonly AppDbContext _db;

    public RegisterController(AppDbContext db)
    {
        _db = db;
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
            LinkId = link.Id
        });

        link.IsUsed = true;

        await _db.SaveChangesAsync();

        return RedirectToAction("Submitted");
    }

    [HttpGet("Submitted")]
    public IActionResult Submitted()
    {
        return View();
    }

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
