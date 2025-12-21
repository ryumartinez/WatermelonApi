using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WatermelonApi;

public class WatermelonService(AppDbContext context, ILogger<WatermelonService> logger)
{
    private static readonly JsonSerializerOptions SyncOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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

        logger.LogInformation("Sync Pull: {Created} created, {Updated} updated, {Deleted} deleted", 
            tableChanges.Created.Count, tableChanges.Updated.Count, tableChanges.Deleted.Count);

        if (isFirstSync && requestTurbo)
        {
            var syncObj = new { changes = responseData, timestamp = serverTimestamp };
            string rawJson = JsonSerializer.Serialize(syncObj, SyncOptions);
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

                // 1. Bulk fetch existing records to avoid N+1 queries
                var incomingCreatedUpdated = productChanges.Created.Concat(productChanges.Updated).ToList();
                var incomingIds = incomingCreatedUpdated
                    .Select(item => MapToDictionary(item)["id"]?.ToString())
                    .Where(id => id != null)
                    .Cast<string>()
                    .ToList();

                var existingRecords = await context.Products
                    .Where(p => incomingIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id);

                // 2. Process Created & Updated
                foreach (var item in incomingCreatedUpdated)
                {
                    var raw = MapToDictionary(item);
                    var id = raw["id"]?.ToString() ?? throw new Exception("Incoming record missing ID");

                    if (existingRecords.TryGetValue(id, out var existing))
                    {
                        // Conflict Resolution
                        if (existing.LastModified > request.LastPulledAt)
                        {
                            throw new InvalidOperationException("CONFLICT");
                        }
                        UpdateProductFields(existing, raw, now);
                    }
                    else
                    {
                        var newProduct = CreateNewProduct(id, raw, now);
                        context.Products.Add(newProduct);
                    }
                }

                // 3. Process Deleted
                if (productChanges.Deleted.Any())
                {
                    var toDelete = await context.Products
                        .Where(p => productChanges.Deleted.Contains(p.Id))
                        .ToListAsync();

                    foreach (var p in toDelete)
                    {
                        p.IsDeleted = true;
                        p.LastModified = now;
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

    private WatermelonProduct CreateNewProduct(string id, Dictionary<string, object> raw, long now)
    {
        var p = new WatermelonProduct { Id = id, ServerCreatedAt = now };
        UpdateProductFields(p, raw, now);
        return p;
    }

    private void UpdateProductFields(WatermelonProduct p, Dictionary<string, object> raw, long now)
    {
        p.Name = GetStr(raw, "name");
        p.ItemId = GetStr(raw, "item_id");
        p.BarCode = GetStr(raw, "bar_code");
        p.BrandCode = GetStr(raw, "brand_code");
        p.BrandName = GetStr(raw, "brand_name");
        p.ColorCode = GetStr(raw, "color_code");
        p.ColorName = GetStr(raw, "color_name");
        p.SizeCode = GetStr(raw, "size_code");
        p.SizeName = GetStr(raw, "size_name");
        p.Unit = GetStr(raw, "unit");
        p.DataAreaId = GetStr(raw, "data_area_id");
        p.InventDimId = GetStr(raw, "invent_dim_id");
        
        // Boolean handling
        if (raw.TryGetValue("is_required_batch_id", out var batchVal))
        {
            p.IsRequiredBatchId = batchVal is JsonElement el 
                ? el.GetBoolean() 
                : Convert.ToBoolean(batchVal);
        }

        p.LastModified = now;
    }

    private string GetStr(Dictionary<string, object> dict, string key) =>
        dict.TryGetValue(key, out var val) ? val?.ToString() ?? string.Empty : string.Empty;

    private Dictionary<string, object> MapToDictionary(object item) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(item, SyncOptions), SyncOptions)!;
}