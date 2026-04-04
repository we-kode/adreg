using Microsoft.AspNetCore.Mvc;
using Shared.Services;

namespace AdminApp.Controllers
{
    public class SetupController(AuthService auth) : Controller
    {
        [HttpGet()]
        public async Task<IActionResult> Index()
        {
            if (auth.HasAdminUser)
                return RedirectToAction("Login", "Auth");

            return View();
        }

        [HttpPost()]
        public async Task<IActionResult> Create([FromForm]string username, [FromForm]string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                TempData["Error"] = "Invalid input";
                return View("Index");
            }

            await auth.CreateAdmin(username, password);

            return RedirectToAction("Login", "Auth");
        }
    }
}
