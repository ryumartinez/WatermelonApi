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
    
    // Explicitly using snake_case for assertion comparisons
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
            LastModified = 1000,
            ServerCreatedAt = 1000 
        };
        
        context.Products.Add(initialProduct);
        await context.SaveChangesAsync();
        output.WriteLine($"Seeded: {initialProduct.Id} (last_modified: {initialProduct.LastModified})");
    }

    [Fact]
    public async Task Pull_InitialSync_TurboMode_VerifiesSnakeCaseFormat()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var url = "/api/sync/pull?last_pulled_at=0&turbo=true";
        
        // Act
        var response = await _client.GetAsync(url);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawContent = await response.Content.ReadAsStringAsync();
        
        // CRITICAL: Ensure no PascalCase keys like "Id" or "LastModified" exist in raw JSON [cite: 1270, 1293]
        Assert.Contains("\"id\":", rawContent);
        Assert.Contains("\"last_modified\":", rawContent);
        Assert.DoesNotContain("\"Id\":", rawContent);
        Assert.DoesNotContain("\"LastModified\":", rawContent);
        
        output.WriteLine($"Validated Snake Case in Raw JSON: {rawContent}");
    }

    [Fact]
    public async Task Pull_CategorizesRecordsCorrectly_VerifiesChangesShape()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        }

        // Act
        var lastPulledAt = 2000;
        var response = await _client.GetAsync($"/api/sync/pull?last_pulled_at={lastPulledAt}");
        var json = await response.Content.ReadAsStringAsync();
        
        // Deserialize using snake_case options to ensure matching 
        var syncResponse = JsonSerializer.Deserialize<SyncPullResponse>(json, _assertOptions);

        // Assert
        Assert.NotNull(syncResponse?.Changes);
        var productChanges = syncResponse.Changes["products"];
        
        // Verify proper categorization [cite: 1316, 1317]
        Assert.Single(productChanges.Created); // prod_2
        Assert.Single(productChanges.Updated); // prod_1
        
        // Deep verification of a record to ensure "id" is present and not null
        var createdRecord = (JsonElement)productChanges.Created[0];
        Assert.Equal("prod_2", createdRecord.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Push_SuccessfulCreation_BridgesClientSnakeCaseToPascalCaseServer()
    {
        // Arrange
        await ResetAndSeedDatabase();
        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = new[] { new { id = "new_client_prod", name = "Client Side", price = 10 } },
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
        var record = await db.Products.FindAsync("new_client_prod");
        
        // Verify server-side PascalCase object was populated from snake_case JSON [cite: 1352, 1353]
        Assert.NotNull(record);
        Assert.Equal("Client Side", record.Name);
    }

    [Fact]
    public async Task Push_ConflictResolution_AbortsOnNewerServerChanges()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using (var scope = factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p1 = await context.Products.FindAsync("prod_1");
            p1!.LastModified = 3000; // Newer than the client's hypothetical last_pulled_at
            await context.SaveChangesAsync();
        }

        var pushRequest = new {
            changes = new Dictionary<string, object> {
                { "products", new {
                    created = Array.Empty<object>(),
                    updated = new[] { new { id = "prod_1", name = "Conflict Request" } },
                    deleted = Array.Empty<string>()
                }}
            },
            last_pulled_at = 1000 // Client thinks it's up to date with 1000
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest, _assertOptions);

        // Assert: Push MUST abort if record modified after lastPulledAt 
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        output.WriteLine("Conflict successfully detected and transaction aborted.");
    }
}