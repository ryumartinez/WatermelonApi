using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient; // Required for connection string manipulation
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace WatermelonApi.Tests;

public class WatermelonApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _dbContainer = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            // 1. Get the base connection string (points to master by default)
            var baseConnectionString = _dbContainer.GetConnectionString();

            // 2. Use SqlConnectionStringBuilder to change the database name
            var builder = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "WatermelonTestDb",
                TrustServerCertificate = true
            };

            var testConnectionString = builder.ConnectionString;

            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(testConnectionString));
        });
    }

    public async Task InitializeAsync() => await _dbContainer.StartAsync();
    public new async Task DisposeAsync() => await _dbContainer.DisposeAsync();
}