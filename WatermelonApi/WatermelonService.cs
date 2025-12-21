using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class WatermelonService(AppDbContext context)
{
    public async Task<SyncPullResponse> GetPullChangesAsync(long lastPulledAt, bool requestTurbo = false)
    {

        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isFirstSync = lastPulledAt == 0;

        var allChanges = await context.Products
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();


        var tableChanges = new TableChanges(
            Created: allChanges
                .Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt))
                .Cast<object>().ToList(),
            Updated: allChanges
                .Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt)
                .Cast<object>().ToList(),
            Deleted: allChanges
                .Where(p => p.IsDeleted)
                .Select(p => p.Id).ToList()
        );

        var responseData = new Dictionary<string, TableChanges> { { "products", tableChanges } };

        if (isFirstSync && requestTurbo)
        {
            var syncObj = new { changes = responseData, timestamp = serverTimestamp };
            string rawJson = System.Text.Json.JsonSerializer.Serialize(syncObj);
            return new SyncPullResponse(null, serverTimestamp, rawJson);
        }

        return new SyncPullResponse(responseData, serverTimestamp);
    }
    
    public async Task ProcessPushChangesAsync(SyncPushRequest request)
    {
        using var tx = await context.Database.BeginTransactionAsync();
        try 
        {
            if (request.Changes.TryGetValue("products", out var productChanges))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                foreach (var item in productChanges.Created)
                {
                    var raw = MapToDictionary(item);
                    var id = raw["id"].ToString()!;
                    var existing = await context.Products.FindAsync(id);

                    if (existing != null) UpdateFields(existing, raw, now);
                    else context.Products.Add(CreateNewRecord(id, raw, now));
                }
                
                foreach (var item in productChanges.Updated)
                {
                    var raw = MapToDictionary(item);
                    var id = raw["id"].ToString()!;
                    var existing = await context.Products.FindAsync(id) 
                        ?? throw new Exception($"Record {id} not found");

                    if (existing.LastModified > request.LastPulledAt)
                        throw new InvalidOperationException("CONFLICT"); // Handled by controller

                    UpdateFields(existing, raw, now);
                }
                
                foreach (var id in productChanges.Deleted)
                {
                    var existing = await context.Products.FindAsync(id);
                    if (existing != null)
                    {
                        existing.IsDeleted = true;
                        existing.LastModified = now;
                    }
                }
            }
            await context.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch 
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private Dictionary<string, object> MapToDictionary(object item) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            System.Text.Json.JsonSerializer.Serialize(item))!;

    private Product CreateNewRecord(string id, Dictionary<string, object> raw, long now) => new() {
        Id = id,
        Name = raw.GetValueOrDefault("name")?.ToString(),
        Price = Convert.ToDecimal(raw.GetValueOrDefault("price")?.ToString() ?? "0"),
        Sku = raw.GetValueOrDefault("sku")?.ToString(),
        LastModified = now,
        ServerCreatedAt = now
    };

    private void UpdateFields(Product p, Dictionary<string, object> raw, long now) {
        p.Name = raw.GetValueOrDefault("name")?.ToString();
        p.Price = Convert.ToDecimal(raw.GetValueOrDefault("price")?.ToString() ?? "0");
        p.Sku = raw.GetValueOrDefault("sku")?.ToString();
        p.LastModified = now;
    }
}