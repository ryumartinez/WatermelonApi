using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace WatermelonApi.Tests;

public class WatermelonIntegrationTests(WatermelonApiFactory factory, ITestOutputHelper output) : IClassFixture<WatermelonApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    
    private readonly JsonSerializerOptions _assertOptions = new() 
    { 
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower 
    };

    private async Task ResetAndSeedDatabase()
    {
        output.WriteLine("--- Resetting and Seeding Database ---");
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var initialProduct = new Product { 
            Id = "prod_1", 
            Name = "Initial Product", 
            ItemId = "ITEM-001",
            BarCode = "123456789",
            DataAreaId = "US01",
            LastModified = 1000,
            ServerCreatedAt = 1000 
        };
        
        context.Products.Add(initialProduct);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Pull_InitialSync_TurboMode_VerifiesAllEnterpriseFields()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var url = "/api/sync/pull?last_pulled_at=0&turbo=true";
        
        // Act
        var response = await _client.GetAsync(url);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        
        // Verify snake_case for enterprise fields
        Assert.Contains("\"item_id\":", rawContent);
        Assert.Contains("\"bar_code\":", rawContent);
        Assert.Contains("\"data_area_id\":", rawContent);
        Assert.Contains("\"is_required_batch_id\":", rawContent);
        
        output.WriteLine("Verified: All enterprise fields present in snake_case.");
    }

    [Fact]
    public async Task Push_SuccessfulCreation_MapsComplexFieldsToServer()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = new[] { 
                        new { 
                            id = "client_prod_xyz", 
                            name = "New D365 Item", 
                            item_id = "D365-999",
                            bar_code = "777888999",
                            brand_code = "MSFT",
                            data_area_id = "PY01",
                            is_required_batch_id = true
                        } 
                    },
                    updated = Array.Empty<object>(),
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 500
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest, _assertOptions);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.Products.FindAsync("client_prod_xyz");
        
        Assert.NotNull(record);
        Assert.Equal("New D365 Item", record.Name);
        Assert.Equal("D365-999", record.ItemId);
        Assert.Equal("PY01", record.DataAreaId);
        Assert.True(record.IsRequiredBatchId);
    }

    [Fact]
    public async Task Pull_DetectsUpdatesSinceLastSync()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p1 = await context.Products.FindAsync("prod_1");
            p1!.Name = "Modified on Server";
            p1.LastModified = 3000; // Updated later
            await context.SaveChangesAsync();
        }

        // Act
        var response = await _client.GetAsync("/api/sync/pull?last_pulled_at=2000");
        var syncResponse = await response.Content.ReadFromJsonAsync<SyncPullResponse>(_assertOptions);

        // Assert
        var productChanges = syncResponse!.Changes!["products"];
        Assert.Single(productChanges.Updated);
        
        var updatedRecord = (JsonElement)productChanges.Updated[0];
        Assert.Equal("Modified on Server", updatedRecord.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Push_Conflict_WhenServerHasNewerData()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p1 = await context.Products.FindAsync("prod_1");
            p1!.LastModified = 5000; // Server updated after client's last pull
            await context.SaveChangesAsync();
        }

        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = Array.Empty<object>(),
                    updated = new[] { new { id = "prod_1", name = "Stale Client Update" } },
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 1000 // Client thinks it is at timestamp 1000
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest, _assertOptions);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}