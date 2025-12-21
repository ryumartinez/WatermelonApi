using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

        // Initial seed with specific timestamps to test categorization logic
        context.Products.Add(new Product { 
            Id = "prod_1", 
            Name = "Initial Product", 
            LastModified = 1000,
            ServerCreatedAt = 1000 
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Pull_InitialSync_TurboMode_ReturnsRawJson()
    {
        // Arrange
        await ResetAndSeedDatabase();
        
        // Act: Requesting initial sync with turbo=true [cite: 1538, 1548]
        var response = await _client.GetAsync("/api/sync/pull?last_pulled_at=0&turbo=true");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        
        // Watermelon Turbo Login expects a raw JSON string containing changes and timestamp [cite: 1556, 1558]
        Assert.Contains("\"changes\":", rawContent);
        Assert.Contains("\"timestamp\":", rawContent);
        Assert.Contains("prod_1", rawContent);
    }

    [Fact]
    public async Task Pull_CategorizesRecordsCorrectly_BasedOnCreation()
    {
        // Arrange
        await ResetAndSeedDatabase(); // prod_1 created at 1000
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
        // Server modification for prod_1 at 2600
        var p1 = await context.Products.FindAsync("prod_1");
        p1!.Name = "Updated Name";
        p1.LastModified = 2600;
    
        // New product prod_2 created at 2500
        context.Products.Add(new Product { 
            Id = "prod_2", 
            Name = "New Product", 
            ServerCreatedAt = 2500, 
            LastModified = 2500 
        });
        await context.SaveChangesAsync();

        // Act: Pull from timestamp 2000
        var syncResponse = await _client.GetFromJsonAsync<SyncPullResponse>("/api/sync/pull?last_pulled_at=2000");

        // Assert
        Assert.NotNull(syncResponse);
        var productChanges = syncResponse.Changes!["products"];
        
        Assert.Contains(productChanges.Created, item => item.ToString()!.Contains("prod_2"));
        Assert.Contains(productChanges.Updated, item => item.ToString()!.Contains("prod_1"));
    }

    [Fact]
    public async Task Push_IncludesLastPulledAtInBody_AlignsWithBackendDTO()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Prepare request using JSON property names expected by the C# Record 
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = new[] { new { id = "new_client_prod", name = "Client Side", price = 10, sku = "C1" } },
                    updated = Array.Empty<object>(),
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 500 // Must match the JsonPropertyName attribute in your C# code
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.Products.FindAsync("new_client_prod");
        Assert.NotNull(record);
    }

    [Fact]
    public async Task Push_DetectsConflict_WhenServerRecordIsNewer()
    {
        // Arrange
        await ResetAndSeedDatabase(); // prod_1 modified at 1000
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        // Server update occurs at 2000
        var p1 = await context.Products.FindAsync("prod_1");
        p1!.LastModified = 2000;
        await context.SaveChangesAsync();

        // Act: Client tries to push changes based on an old pull (last_pulled_at = 500)
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = Array.Empty<object>(),
                    updated = new[] { new { id = "prod_1", name = "Conflict Attempt" } },
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 500 // 500 < 2000 -> Conflict [cite: 1796, 1797]
        };

        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert
        // Watermelon requires push failure if record was modified on server after lastPulledAt [cite: 1796]
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}