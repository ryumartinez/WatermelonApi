using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<WatermelonProduct> Products => Set<WatermelonProduct>();
    public DbSet<WatermelonProductBatch> ProductBatches => Set<WatermelonProductBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WatermelonProduct>().HasKey(p => p.Id);
        modelBuilder.Entity<WatermelonProduct>().HasIndex(p => p.LastModified);
        modelBuilder.Entity<WatermelonProductBatch>().HasIndex(p => p.LastModified);
    }
}