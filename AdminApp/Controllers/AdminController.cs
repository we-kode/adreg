using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Data;
using Shared.Models;
using Shared.Services;
using System.Text.Json;

namespace AdminApp.Controllers
{
    [Authorize]
    public class AdminController(AppDbContext db, MailService mail, MidpointService midpoint) : Controller
    {
        public IActionResult Index()
        {
            var list = db.PendingRegistrations
            .OrderByDescending(x => x.Id)
            .ToList();

            return View(list);
        }

        [HttpPost]
        public async Task<IActionResult> Approve(int id)
        {
            var req = await db.PendingRegistrations.FindAsync(id);

            if (req == null) return RedirectToAction("Index", "Admin");

            await midpoint.CreateUser(req.FirstName, req.LastName, req.Email, JsonSerializer.Deserialize<List<string>>(req.GroupsJson) ?? []);

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
            var groups = await midpoint.GetRoles();
            ViewBag.Groups = groups;
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateLink(DateTime validUntil, bool singleUse, string email, List<string> selectedGroups)
        {
            var link = new RegistrationLink
            {
                Id = Guid.NewGuid(),
                ValidUntil = validUntil,
                IsSingleUse = singleUse,
                IsUsed = false,
                GroupsJson = JsonSerializer.Serialize(selectedGroups)
            };

            db.Links.Add(link);
            await db.SaveChangesAsync();

            var request = HttpContext.Request;
            var baseUrl = $"{request.Scheme}://{request.Host}";
            var url = $"{baseUrl}/Register/{link.Id}";

            await mail.SendRegistrationLink(email, url);

            TempData["Success"] = "Link created & mail sent";

            return RedirectToAction("Index", "Admin");
        }
    }
}
