using Microsoft.AspNetCore.Mvc;
using Shared.Data;
using Shared.Models;

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
    public async Task<IActionResult> Submit(Guid id, string firstname, string lastname, string email, string password)
    {
        var link = await _db.Links.FindAsync(id);
        if (link == null) return Content("Invalid link");

        _db.PendingRegistrations.Add(new PendingRegistration
        {
            FirstName = firstname,
            LastName = lastname,
            Email = email,
            GroupsJson = link.GroupsJson,
            LinkId = link.Id
        });

        link.IsUsed = true;

        await _db.SaveChangesAsync();

        return Content("Submitted");
    }
}
