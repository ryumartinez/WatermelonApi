using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WatermelonApi;

public partial class WatermelonService(AppDbContext context, ILogger<WatermelonService> logger)
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

        // Fetch changes from partial methods
        var productTableChanges = await GetProductPullChangesAsync(lastPulledAt, isFirstSync);
        var batchTableChanges = await GetBatchPullChangesAsync(lastPulledAt, isFirstSync);

        var responseData = new Dictionary<string, TableChanges> 
        { 
            { "products", productTableChanges },
            { "product_batches", batchTableChanges }
        };

        if (isFirstSync && requestTurbo)
        {
            var syncObj = new { changes = responseData, timestamp = serverTimestamp };
            return new SyncPullResponse(null, serverTimestamp, JsonSerializer.Serialize(syncObj, SyncOptions));
        }

        return new SyncPullResponse(responseData, serverTimestamp);
    }

    public async Task ProcessPushChangesAsync(SyncPushRequest request)
    {
        using var tx = await context.Database.BeginTransactionAsync();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (request.Changes.TryGetValue("products", out var productChanges))
                await ProcessProductChanges(productChanges, request.LastPulledAt, now);

            if (request.Changes.TryGetValue("product_batches", out var batchChanges))
                await ProcessBatchChanges(batchChanges, request.LastPulledAt, now);

            await context.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            logger.LogError(ex, "Push failed. Transaction rolled back.");
            throw;
        }
    }

    // Shared Helpers
    private async Task HandleDeletions<T>(DbSet<T> dbSet, List<string> ids, long now) where T : class
    {
        if (!ids.Any()) return;
        var records = await dbSet.Where(r => ids.Contains(EF.Property<string>(r, "Id"))).ToListAsync();
        foreach (var r in records)
        {
            var entry = context.Entry(r);
            entry.Property("IsDeleted").CurrentValue = true;
            entry.Property("LastModified").CurrentValue = now;
        }
    }

    private string GetStr(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
    private bool GetBool(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && (v is JsonElement el ? el.GetBoolean() : Convert.ToBoolean(v));
    private long? GetNullableLong(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v != null ? (v is JsonElement el ? el.GetInt64() : Convert.ToInt64(v)) : null;
    private List<string> GetIds(List<object> items) => items.Select(i => MapToDictionary(i)["id"]?.ToString()!).Where(id => id != null).ToList();
    private Dictionary<string, object> MapToDictionary(object item) => JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(item, SyncOptions), SyncOptions)!;
}