using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Data;
using Shared.Models;
using Shared.Services;
using System.Text.Json;

namespace AdminApp.Controllers
{
    [Authorize]
    public class AdminController(AppDbContext db, MailService mail, ADService adService, IConfiguration config) : Controller
    {
        public IActionResult Index()
        {
            // Do not expose PasswordBase64 to the admin UI — project to instances without the password
            var list = db.PendingRegistrations
                .OrderByDescending(x => x.Id)
                .Select(p => new Shared.Models.PendingRegistrationDto
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

            if (req == null) return RedirectToAction("Index", "Admin");

            // decode password from Base64 and call AD service. ADService will accept a username (without @) and build an UPN
            string password = null;
            try
            {
                if (!string.IsNullOrEmpty(req.PasswordBase64))
                    password = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(req.PasswordBase64));
            }
            catch { password = null; }

            await adService.CreateUser(req.FirstName + " " + req.LastName, req.Username, password, JsonSerializer.Deserialize<List<string>>(req.GroupsJson) ?? []);

            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();

            TempData["Success"] = "User approved";

            return RedirectToAction("Index");
        }

        [HttpPost]
        public async Task<IActionResult> Reject(int id)
        {
            var req = await db.PendingRegistrations.FindAsync(id);

            if (req == null) return RedirectToAction("Index", "Admin");

            db.PendingRegistrations.Remove(req);
            await db.SaveChangesAsync();

            TempData["Success"] = "User rejected";

            return RedirectToAction("Index", "Admin");
        }

        [HttpGet]
        public async Task<IActionResult> CreateLink()
        {
            var groups = await adService.GetRoles();
            ViewBag.Groups = groups;
            return View();
        }

        [HttpGet]
        public IActionResult CreateGroup()
        {
            return View();
        }

        [HttpGet]
        public IActionResult Manage()
        {
            // view that allows switching between groups and users and performs backend filtering
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateGroup(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                TempData["Error"] = "Group name is required";
                return View();
            }

            try
            {
                var created = await adService.CreateGroup(name);
                if (!created)
                {
                    TempData["Error"] = "Group already exists";
                    return View();
                }

                TempData["Success"] = "Group created";
                return RedirectToAction("Manage", "Admin");
            }
            catch (Exception)
            {
                TempData["Error"] = "Failed to create group";
                return View();
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetGroups(string? q)
        {
            var groups = await adService.GetRoles(q);
            var result = groups.Select(g => new { dn = g.Dn, name = g.Name }).ToList();
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetUsers(string? q)
        {
            var users = await adService.GetUsers(q);
            var result = users.Select(u => new { dn = u.Dn, name = u.Name }).ToList();
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetUsersInGroup(string groupDn)
        {
            var users = await adService.GetUsersInGroup(groupDn);
            var result = users.Select(u => new { dn = u.Dn, name = u.Name }).ToList();
            return Json(result);
        }

        [HttpGet]
        public async Task<IActionResult> GetGroupsForUser(string userDn)
        {
            var groups = await adService.GetGroupsForUser(userDn);
            var result = groups.Select(g => new { dn = g.Dn, name = g.Name }).ToList();
            return Json(result);
        }

        [HttpPost]
        public async Task<IActionResult> CreateLink(DateTime? validUntil, bool singleUse, string email, List<string>? selectedGroups)
        {
            var link = new RegistrationLink
            {
                Id = Guid.NewGuid(),
                ValidUntil = validUntil,
                IsSingleUse = singleUse,
                IsUsed = false,
                GroupsJson = JsonSerializer.Serialize(selectedGroups ?? new List<string>())
            };

            db.Links.Add(link);
            await db.SaveChangesAsync();

            var request = HttpContext.Request;
            var baseUrl = config.GetValue<string?>("LinkBaseUrl");
            var url = $"{baseUrl}/Register/{link.Id}";

            await mail.SendRegistrationLink(email, url);

            TempData["Success"] = "Link created & mail sent";

            return RedirectToAction("Index", "Admin");
        }
    }
}
