using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WatermelonApi;

public class WatermelonService(AppDbContext context, ILogger<WatermelonService> logger)
{
    // WatermelonDB requires snake_case to match JavaScript/Schema conventions [cite: 1270, 1293]
    private static readonly JsonSerializerOptions SyncOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SyncPullResponse> GetPullChangesAsync(long lastPulledAt, bool requestTurbo = false)
    {
        // 1. Mark the current server time synchronously with queries to ensure consistency [cite: 1324, 1326]
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isFirstSync = lastPulledAt == 0;

        // 2. Fetch all relevant records (Created, Updated, or Deleted) [cite: 1315-1317]
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
                .Select(p => p.Id).ToList() // Deleted records only need IDs [cite: 1271, 1318]
        );

        var responseData = new Dictionary<string, TableChanges> { { "products", tableChanges } };

        logger.LogInformation("Sync Pull: {Created} created, {Updated} updated, {Deleted} deleted", 
            tableChanges.Created.Count, tableChanges.Updated.Count, tableChanges.Deleted.Count);

        // 3. Handle Turbo Login (First Sync Only) [cite: 1530]
        if (isFirstSync && requestTurbo)
        {
            var syncObj = new { changes = responseData, timestamp = serverTimestamp };
            
            // Turbo requires raw JSON text to skip JS processing on the client 
            string rawJson = JsonSerializer.Serialize(syncObj, SyncOptions);
            
            logger.LogDebug("Turbo Sync Response: {Json}", rawJson);
            return new SyncPullResponse(null, serverTimestamp, rawJson);
        }

        return new SyncPullResponse(responseData, serverTimestamp);
    }

    public async Task ProcessPushChangesAsync(SyncPushRequest request)
    {
        // Push endpoint MUST be fully transactional 
        using var tx = await context.Database.BeginTransactionAsync();
        try
        {
            if (request.Changes.TryGetValue("products", out var productChanges))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Handle Created/Updated records [cite: 1353-1354]
                foreach (var item in productChanges.Created.Concat(productChanges.Updated))
                {
                    var raw = MapToDictionary(item);
                    var id = raw["id"]?.ToString() ?? throw new Exception("Incoming record missing ID");
                    
                    var existing = await context.Products.FindAsync(id);

                   // Conflict Resolution: If record was modified on server after lastPulledAt, abort [cite: 1364-1365]
                    if (existing != null && existing.LastModified > request.LastPulledAt)
                    {
                        throw new InvalidOperationException("CONFLICT");
                    }

                    if (existing != null) 
                    {
                        UpdateFields(existing, raw, now);
                    }
                    else 
                    {
                        context.Products.Add(CreateNewRecord(id, raw, now));
                    }
                }

                // Handle Deletions [cite: 1355]
                foreach (var id in productChanges.Deleted)
                {
                    var existing = await context.Products.FindAsync(id);
                    if (existing != null)
                    {
                        existing.IsDeleted = true; // Use soft-deletes to track server-side deletions [cite: 1396]
                        existing.LastModified = now;
                    }
                }
            }

            await context.SaveChangesAsync();
            await tx.CommitAsync();
            logger.LogInformation("Push successful at {Timestamp}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            logger.LogError(ex, "Push failed. Transaction rolled back.");
            throw;
        }
    }

    private Dictionary<string, object> MapToDictionary(object item) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(item, SyncOptions), SyncOptions)!;

    private Product CreateNewRecord(string id, Dictionary<string, object> raw, long now) => new()
    {
        Id = id,
        Name = raw.GetValueOrDefault("name")?.ToString(),
        Price = Convert.ToDecimal(raw.GetValueOrDefault("price")?.ToString() ?? "0"),
        Sku = raw.GetValueOrDefault("sku")?.ToString(),
        LastModified = now,
        ServerCreatedAt = now
    };

    private void UpdateFields(Product p, Dictionary<string, object> raw, long now)
    {
        p.Name = raw.GetValueOrDefault("name")?.ToString();
        p.Price = Convert.ToDecimal(raw.GetValueOrDefault("price")?.ToString() ?? "0");
        p.Sku = raw.GetValueOrDefault("sku")?.ToString();
        p.LastModified = now;
    }
}