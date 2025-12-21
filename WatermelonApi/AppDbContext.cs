using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<WatermelonProduct> Products => Set<WatermelonProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WatermelonProduct>().HasKey(p => p.Id);
        modelBuilder.Entity<WatermelonProduct>().HasIndex(p => p.LastModified);
    }
}