using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Shared.Services;
using System.Security.Claims;

namespace AdminApp.Controllers;

public class AuthController(AuthService auth) : Controller
{

    [HttpGet]
    public IActionResult Login()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Login(string username, string password)
    {
        var ok = auth.Login(username, password);

        if (!ok)
        {
            TempData["Error"] = "Invalid credentials";
            return View();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username)
        };

        var identity = new ClaimsIdentity(claims, "Cookies");
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(24),
            AllowRefresh = true,
        };

        await HttpContext.SignInAsync("Cookies", new ClaimsPrincipal(identity), authProperties);

        return RedirectToAction("Index", "Admin");
    }

    [HttpGet]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync("Cookies");
        return RedirectToAction("Login", "Auth");
    }
}
