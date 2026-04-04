using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared.Data;
using Shared.Models;
using Shared.Services;
using System.Net.Http.Headers;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddLogging();

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=/data/adreg.db"));

// Add Midpoint client
builder.Services.Configure<MidpointSettings>(builder.Configuration.GetSection("MIDPOINT"));
builder.Services.AddHttpClient<MidpointService>((sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<MidpointSettings>>().Value;

    client.BaseAddress = new Uri(settings.BaseUrl);

    var auth = Convert.ToBase64String(
        Encoding.ASCII.GetBytes($"{settings.Username}:{settings.Password}"));

    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Basic", auth);

    client.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});

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
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always; 
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
