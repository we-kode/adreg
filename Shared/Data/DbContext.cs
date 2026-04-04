using Microsoft.EntityFrameworkCore;
using Shared.Models;

namespace Shared.Data;
public class AppDbContext : DbContext
{
    public DbSet<RegistrationLink> Links { get; set; }
    public DbSet<PendingRegistration> PendingRegistrations { get; set; }
    public DbSet<AdminUser> AdminUsers { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // optional: Index auf Email für PendingRegistrations
        modelBuilder.Entity<PendingRegistration>()
            .HasIndex(p => p.Email)
            .IsUnique(false);
    }
}
