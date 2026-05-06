using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shared.Data;
using Shared.Models;
using Shared.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=/data/adreg.db"));

// Persist DataProtection keys to the mounted /data volume so antiforgery
// tokens remain valid across container recreates.
var dpKeysPath = new DirectoryInfo("/data/dp");
dpKeysPath.Create();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(dpKeysPath)
    .SetApplicationName("adreg");

// Active Directory client (used for password reset).
builder.Services.Configure<ADSettings>(builder.Configuration.GetSection("AD"));
builder.Services.AddSingleton<ADService>();

var app = builder.Build();

if (bool.TryParse(Environment.GetEnvironmentVariable("MIGRATE_REGISTER"), out var doMigration) && doMigration)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Register}/{action=Index}/{id}");

app.Run();
