using Microsoft.AspNetCore.Mvc;
using Shared.Services;

namespace AdminApp.Controllers
{
    public class SetupController(AuthService auth) : Controller
    {
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (auth.HasAdminUser)
                return RedirectToAction("Login", "Auth");

            return View(new AdminApp.Models.SetupViewModel());
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromForm] AdminApp.Models.SetupViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return View("Index", model);
            }

            await auth.CreateAdmin(model.Username, model.Password);

            return RedirectToAction("Login", "Auth");
        }
    }
}
