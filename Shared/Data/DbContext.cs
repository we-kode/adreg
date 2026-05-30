using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Shared.Data;
public class AppDbContext : DbContext
{
    public DbSet<RegistrationLink> Links { get; set; }
    public DbSet<PendingRegistration> PendingRegistrations { get; set; }
    public DbSet<AdminUser> AdminUsers { get; set; }
    public DbSet<PasswordResetLink> PasswordResetLinks { get; set; }
    public DbSet<MailSetting> MailSettings { get; set; }
    public DbSet<MailTemplate> MailTemplates { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // optional: Index auf Username für PendingRegistrations
        modelBuilder.Entity<PendingRegistration>()
            .HasIndex(p => p.Username)
            .IsUnique(false);

        // Helps the lookup MailKey → templates and the default-template query.
        modelBuilder.Entity<MailTemplate>()
            .HasIndex(t => new { t.MailKey, t.IsDefault });
    }
}
