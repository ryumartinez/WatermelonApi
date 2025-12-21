using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace WatermelonApi.Tests;

public class WatermelonIntegrationTests : IClassFixture<WatermelonApiFactory>
{
    private readonly WatermelonApiFactory _factory;
    private readonly HttpClient _client;

    public WatermelonIntegrationTests(WatermelonApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task ResetAndSeedDatabase()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Clean and recreate schema
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        context.Products.Add(new Product { 
            Id = "prod_1", 
            Name = "Initial Product", 
            LastModified = 1000 
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Pull_InitialSync_ReturnsAllData()
    {
        await ResetAndSeedDatabase();
        
        var response = await _client.GetFromJsonAsync<SyncPullResponse>("/api/sync/pull?last_pulled_at=0");

        Assert.NotNull(response);
        Assert.Single(response.Changes["products"].Created);
    }
}