using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions; // Required for ITestOutputHelper

namespace WatermelonApi.Tests;

// Added 'output' to the primary constructor
public class WatermelonIntegrationTests(WatermelonApiFactory factory, ITestOutputHelper output) : IClassFixture<WatermelonApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

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
            LastModified = 1000,
            ServerCreatedAt = 1000 
        };
        
        context.Products.Add(initialProduct);
        await context.SaveChangesAsync();
        output.WriteLine($"Seeded: {initialProduct.Id} with LastModified: {initialProduct.LastModified}");
    }

    [Fact]
    public async Task Pull_InitialSync_TurboMode_ReturnsRawJson()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var url = "/api/sync/pull?last_pulled_at=0&turbo=true";
        output.WriteLine($"Act: Requesting {url}");
        
        // Act
        var response = await _client.GetAsync(url);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        output.WriteLine($"Response Content: {rawContent}");
        
        Assert.Contains("\"changes\":", rawContent);
        Assert.Contains("\"timestamp\":", rawContent);
        Assert.Contains("prod_1", rawContent);
    }

    [Fact]
    public async Task Pull_CategorizesRecordsCorrectly_BasedOnCreation()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
        output.WriteLine("Updating prod_1 and adding prod_2...");
        var p1 = await context.Products.FindAsync("prod_1");
        p1!.Name = "Updated Name";
        p1.LastModified = 2600;
    
        context.Products.Add(new Product { 
            Id = "prod_2", 
            Name = "New Product", 
            ServerCreatedAt = 2500, 
            LastModified = 2500 
        });
        await context.SaveChangesAsync();

        // Act
        var lastPulledAt = 2000;
        output.WriteLine($"Act: Pulling changes since {lastPulledAt}");
        var syncResponse = await _client.GetFromJsonAsync<SyncPullResponse>($"/api/sync/pull?last_pulled_at={lastPulledAt}");

        // Assert
        Assert.NotNull(syncResponse);
        var productChanges = syncResponse.Changes!["products"];
        
        output.WriteLine($"Received {productChanges.Created.Count} Created and {productChanges.Updated.Count} Updated products.");
        
        Assert.Contains(productChanges.Created, item => item.ToString()!.Contains("prod_2"));
        Assert.Contains(productChanges.Updated, item => item.ToString()!.Contains("prod_1"));
    }

    [Fact]
    public async Task Push_IncludesLastPulledAtInBody_AlignsWithBackendDTO()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = new[] { new { id = "new_client_prod", name = "Client Side", price = 10, sku = "C1" } },
                    updated = Array.Empty<object>(),
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 500
        };

        output.WriteLine("Act: Pushing new record 'new_client_prod'");

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert
        output.WriteLine($"Response Status: {response.StatusCode}");
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
        await ResetAndSeedDatabase();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var serverModifiedAt = 2000;
        var p1 = await context.Products.FindAsync("prod_1");
        p1!.LastModified = serverModifiedAt;
        await context.SaveChangesAsync();
        output.WriteLine($"Server modified prod_1 at: {serverModifiedAt}");

        var clientLastPulledAt = 500;
        output.WriteLine($"Client attempting push with last_pulled_at: {clientLastPulledAt} (Should Conflict)");

        // Act
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = Array.Empty<object>(),
                    updated = new[] { new { id = "prod_1", name = "Conflict Attempt" } },
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = clientLastPulledAt
        };

        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert
        output.WriteLine($"Actual Response: {response.StatusCode}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}