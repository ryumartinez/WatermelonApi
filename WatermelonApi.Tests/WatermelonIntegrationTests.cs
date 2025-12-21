using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
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
    
    [Fact]
    public async Task Pull_IncludesDeletedRecords_WhenModifiedAfterLastSync()
    {
        // Arrange
        await ResetAndSeedDatabase();
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var deletedProduct = new Product { 
            Id = "del_1", 
            IsDeleted = true, 
            LastModified = 3000
        };
        context.Products.Add(deletedProduct);
        await context.SaveChangesAsync();

        // Act
        var response = await _client.GetFromJsonAsync<SyncPullResponse>("/api/sync/pull?last_pulled_at=2000");

        // Assert
        Assert.Contains("del_1", response.Changes["products"].Deleted);
        Assert.Empty(response.Changes["products"].Created);
    }
    
    [Fact]
    public async Task Push_ExistingIdInCreatedArray_UpdatesInsteadOfFailing()
    {
        // 1. Arrange: Seed the database with an initial product
        await ResetAndSeedDatabase(); // This creates "prod_1" with Name "Initial Product"

        // 2. Prepare a push request that tries to "Create" the same ID again
        // This simulates a retry after a network failure (Idempotency)
        var duplicatePush = new SyncPushRequest(
            new Dictionary<string, TableChanges> {
                { "products", new TableChanges(
                    new List<object> { 
                        new { 
                            id = "prod_1", 
                            name = "Retried Name", 
                            price = 99.9,
                            sku = "SKU-updated"
                        } 
                    }, 
                    new(), 
                    new()
                )}
            },
            LastPulledAt: 500
        );

        // 3. Act: Send the request to the controller
        var response = await _client.PostAsJsonAsync("/api/sync/push", duplicatePush);

        // 4. Assert: Status should be 200 OK (not 409 or 500)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); 
    
        // 5. Verify: Check that the database record was actually UPDATED
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
        // Disable EF tracking to ensure we get a fresh read from SQL Server
        var updated = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == "prod_1");
    
        Assert.NotNull(updated);
        Assert.Equal("Retried Name", updated.Name);
        Assert.Equal(99.9m, updated.Price);
    }
    
    [Fact]
    public async Task Push_PartialFailure_RollsBackEntireBatch()
    {
        // Arrange
        var pushRequest = new SyncPushRequest(
            new Dictionary<string, TableChanges> {
                { "products", new TableChanges(
                        new List<object> { 
                            new { id = "valid_1", name = "I am valid" },
                            new { id = "invalid_1", name = (string)null } // Supongamos que Name es obligatorio 
                }, 
                new(), 
                new()
                )}
            },
            LastPulledAt: 1000
        );

        // Act
        var response = await _client.PostAsJsonAsync("/api/sync/push", pushRequest);

        // Assert
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var validRecord = await db.Products.FindAsync("valid_1");
        Assert.Null(validRecord); // El registro válido no debe existir porque el lote falló 
    }
    
    [Fact]
    public async Task Pull_CategorizesExistingRecordsAsUpdated()
    {
        // Arrange
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        context.Products.Add(new Product { 
            Id = "upd_1", 
            ServerCreatedAt = 500,  // Creado hace mucho 
            LastModified = 1500     // Modificado recientemente
        });
        await context.SaveChangesAsync();

        // Act: Sincronizamos desde el tiempo 1000
        var response = await _client.GetFromJsonAsync<SyncPullResponse>("/api/sync/pull?last_pulled_at=1000");

        // Assert
        var productChanges = response.Changes["products"];
        Assert.Empty(productChanges.Created); // No es nuevo para el cliente 
        Assert.Single(productChanges.Updated); // Debe aparecer aquí
    }
}