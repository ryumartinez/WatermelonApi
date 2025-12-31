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

    /// <summary>
    /// Implements the Pull Sync protocol: returns all changes since the last sync.
    /// </summary>
    public async Task<SyncPullResponse> GetPullChangesAsync(long lastPulledAt, bool requestTurbo = false)
    {
        // Mark current server time BEFORE querying to ensure a consistent view
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isFirstSync = lastPulledAt == 0;

        // 1. Process Products table
        var productChanges = await context.Products
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();

        var productTableChanges = new TableChanges(
            Created: productChanges
                .Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt))
                .Cast<object>().ToList(),
            Updated: productChanges
                .Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt)
                .Cast<object>().ToList(),
            Deleted: productChanges
                .Where(p => p.IsDeleted)
                .Select(p => p.Id).ToList()
        );

        // 2. Process ProductBatches table
        var batchChanges = await context.ProductBatches
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();

        var batchTableChanges = new TableChanges(
            Created: batchChanges
                .Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt))
                .Cast<object>().ToList(),
            Updated: batchChanges
                .Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt)
                .Cast<object>().ToList(),
            Deleted: batchChanges
                .Where(p => p.IsDeleted)
                .Select(p => p.Id).ToList()
        );

        var responseData = new Dictionary<string, TableChanges> 
        { 
            { "products", productTableChanges },
            { "product_batches", batchTableChanges }
        };

        logger.LogInformation("Pull: Products (C:{PC} U:{PU} D:{PD}), Batches (C:{BC} U:{BU} D:{BD})", 
            productTableChanges.Created.Count, productTableChanges.Updated.Count, productTableChanges.Deleted.Count,
            batchTableChanges.Created.Count, batchTableChanges.Updated.Count, batchTableChanges.Deleted.Count);

        // Turbo Login optimization: return raw JSON text to avoid client JS overhead
        if (isFirstSync && requestTurbo)
        {
            var syncObj = new { changes = responseData, timestamp = serverTimestamp };
            string rawJson = JsonSerializer.Serialize(syncObj, SyncOptions);
            return new SyncPullResponse(null, serverTimestamp, rawJson);
        }

        return new SyncPullResponse(responseData, serverTimestamp);
    }

    /// <summary>
    /// Implements the Push Sync protocol: applies local changes to the server.
    /// </summary>
    public async Task ProcessPushChangesAsync(SyncPushRequest request)
    {
        // Use a transaction to ensure atomicity: all changes must succeed or all fail
        using var tx = await context.Database.BeginTransactionAsync();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (request.Changes.TryGetValue("products", out var productChanges))
            {
                await ProcessProductChanges(productChanges, request.LastPulledAt, now);
            }

            if (request.Changes.TryGetValue("product_batches", out var batchChanges))
            {
                await ProcessBatchChanges(batchChanges, request.LastPulledAt, now);
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

    private async Task ProcessProductChanges(TableChanges changes, long lastPulledAt, long now)
    {
        var incoming = changes.Created.Concat(changes.Updated).ToList();
        var incomingIds = GetIds(incoming);
        var existing = await context.Products.Where(p => incomingIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var item in incoming)
        {
            var raw = MapToDictionary(item);
            var id = raw["id"]?.ToString()!;

            if (existing.TryGetValue(id, out var record))
            {
                // Conflict Detection: If server was modified after client last pulled
                if (record.LastModified > lastPulledAt) throw new InvalidOperationException("CONFLICT");
                UpdateProductFields(record, raw, now);
            }
            else
            {
                var newProd = new WatermelonProduct { Id = id, ServerCreatedAt = now };
                UpdateProductFields(newProd, raw, now);
                context.Products.Add(newProd);
            }
        }

        await HandleDeletions(context.Products, changes.Deleted, now);
    }

    private async Task ProcessBatchChanges(TableChanges changes, long lastPulledAt, long now)
    {
        var incoming = changes.Created.Concat(changes.Updated).ToList();
        var incomingIds = GetIds(incoming);
        var existing = await context.ProductBatches.Where(p => incomingIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var item in incoming)
        {
            var raw = MapToDictionary(item);
            var id = raw["id"]?.ToString()!;

            if (existing.TryGetValue(id, out var record))
            {
                if (record.LastModified > lastPulledAt) throw new InvalidOperationException("CONFLICT");
                UpdateBatchFields(record, raw, now);
            }
            else
            {
                var newBatch = new WatermelonProductBatch { Id = id, ServerCreatedAt = now };
                UpdateBatchFields(newBatch, raw, now);
                context.ProductBatches.Add(newBatch);
            }
        }

        await HandleDeletions(context.ProductBatches, changes.Deleted, now);
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
        p.IsRequiredBatchId = GetBool(raw, "is_required_batch_id");
        p.LastModified = now;
    }

    private void UpdateBatchFields(WatermelonProductBatch b, Dictionary<string, object> raw, long now)
    {
        b.DataAreaId = GetStr(raw, "data_area_id");
        b.ItemNumber = GetStr(raw, "item_number");
        b.BatchNumber = GetStr(raw, "batch_number");
        b.VendorBatchNumber = GetStr(raw, "vendor_batch_number");
        b.VendorExpirationDate = GetNullableLong(raw, "vendor_expiration_date");
        b.BatchExpirationDate = GetNullableLong(raw, "batch_expiration_date");
        b.LastModified = now;
    }

    private async Task HandleDeletions<T>(DbSet<T> dbSet, List<string> ids, long now) where T : class
    {
        if (!ids.Any()) return;
        var records = await dbSet.Where(r => ids.Contains(EF.Property<string>(r, "Id"))).ToListAsync();
        foreach (var r in records)
        {
            // Soft delete: keep record but mark it for other clients to pull deletion [cite: 1826, 1827]
            var entry = context.Entry(r);
            entry.Property("IsDeleted").CurrentValue = true;
            entry.Property("LastModified").CurrentValue = now;
        }
    }

    // Helpers
    private string GetStr(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
    private bool GetBool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && (v is JsonElement el ? el.GetBoolean() : Convert.ToBoolean(v));
    private long? GetNullableLong(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? (v is JsonElement el ? el.GetInt64() : Convert.ToInt64(v)) : null;
    private List<string> GetIds(List<object> items) => items.Select(i => MapToDictionary(i)["id"]?.ToString()!).Where(id => id != null).ToList();
    private Dictionary<string, object> MapToDictionary(object item) => JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(item, SyncOptions), SyncOptions)!;
}