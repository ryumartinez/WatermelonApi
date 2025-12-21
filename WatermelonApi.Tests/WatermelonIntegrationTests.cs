using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace WatermelonApi.Tests;

public class WatermelonIntegrationTests(WatermelonApiFactory factory) : IClassFixture<WatermelonApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task ResetAndSeedDatabase()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        context.Products.Add(new Product { 
            Id = "prod_1", 
            Name = "Initial Product", 
            LastModified = 1000,
            ServerCreatedAt = 1000 
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