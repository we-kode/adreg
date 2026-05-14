using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    ADService adService,
    IConfiguration config,
    ILogger<AdminController> logger) : Controller
{
    public IActionResult Index()
    {
        var list = db.PendingRegistrations
            .OrderByDescending(x => x.Id)
            .Select(p => new PendingRegistrationDto
            {
                Id = p.Id,
                FirstName = p.FirstName,
                LastName = p.LastName,
                Username = p.Username,
                GroupsJson = p.GroupsJson
            })
            .ToList();

        return View(list);
    }

    [HttpPost]
    public async Task<IActionResult> Approve(int id)
    {
        var req = await db.PendingRegistrations.FindAsync(id);
        if (req == null) return RedirectToAction("Index");

        try
        {
            var password = DecodePassword(req.PasswordBase64);
            var groups = JsonSerializer.Deserialize<List<string>>(req.GroupsJson) ?? new List<string>();
            var email = req.Email;

            await adService.CreateUser(req.FirstName, req.LastName, req.Username, password, groups, email);

            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();

            TempData["Success"] = "User approved";
        }
        catch (Exception ex)
        {
            HandleError(ex, $"Approve registration {id}", "Failed to create user in directory");
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Reject(int id)
    {
        var req = await db.PendingRegistrations.FindAsync(id);
        if (req != null)
        {
            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();
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
            await mail.SendPasswordResetLink(email.Trim(), $"{baseUrl}/Register/ResetPassword/{link.Id}", link.ValidUntil);

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

    private void HandleError(Exception ex, string action, string userPrefix)
    {
        logger.LogError(ex, "{Action} failed", action);
        var message = ex is DirectoryServiceException
            ? $"{userPrefix}: {ex.Message}"
            : $"Unexpected error during {action.ToLowerInvariant()}.";
        TempData["Error"] = message;
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
