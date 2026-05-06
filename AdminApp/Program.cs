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

// Persist DataProtection keys to the mounted /data volume so antiforgery and
// auth cookies remain valid across container recreates.
var dpKeysPath = new DirectoryInfo("/data/dp_admin");
dpKeysPath.Create();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(dpKeysPath)
    .SetApplicationName("adregadmin");

// Add Active Directory client
builder.Services.Configure<ADSettings>(builder.Configuration.GetSection("AD"));
builder.Services.AddSingleton<ADService>();

// MailKit
builder.Services.Configure<SmtpSettings>(builder.Configuration.GetSection("SMTP"));
builder.Services.AddSingleton<MailService>();

builder.Services.AddScoped<AuthService>();

builder.Services.AddAuthentication("Cookies")
    .AddCookie(o => 
    { 
        o.LoginPath = "/Auth/Login";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; 
        o.ExpireTimeSpan = TimeSpan.FromHours(24);
        o.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (bool.TryParse(Environment.GetEnvironmentVariable("MIGRATE_ADMIN"), out var doMigration) && doMigration)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.Use(async (ctx, next) =>
{
    using var scope = app.Services.CreateScope();
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    if (!auth.HasAdminUser && !ctx.Request.Path.StartsWithSegments("/Setup"))
    {
        ctx.Response.Redirect("/Setup");
        return;
    }

    await next();
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Admin}/{action=Index}/{id?}");


app.Run();
