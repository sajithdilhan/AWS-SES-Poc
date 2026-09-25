using Microsoft.EntityFrameworkCore;
using SesPoc.Api.Models;

namespace SesPoc.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The InMemory provider does not enforce unique indexes; uniqueness is also checked in the endpoint.
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
    }
}
