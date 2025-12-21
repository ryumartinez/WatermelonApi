using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    
    public async Task<byte[]> GenerateSqliteSyncFileAsync()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
    var connectionString = new SqliteConnectionStringBuilder { DataSource = tempPath }.ToString();

    try
    {
        using (var sqliteConn = new SqliteConnection(connectionString))
        {
            await sqliteConn.OpenAsync();

            // 1. Optimize SQLite for bulk insertion
            using (var pragmaCmd = sqliteConn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode = OFF; PRAGMA synchronous = OFF;";
                await pragmaCmd.ExecuteNonQueryAsync();
            }

            // 2. Create Schema (Adjust field types to match WatermelonDB expectations)
            using (var createCmd = sqliteConn.CreateCommand())
            {
                createCmd.CommandText = @"
                    CREATE TABLE products (
                        id TEXT PRIMARY KEY,
                        name TEXT,
                        item_id TEXT,
                        bar_code TEXT,
                        brand_code TEXT,
                        brand_name TEXT,
                        color_code TEXT,
                        color_name TEXT,
                        size_code TEXT,
                        size_name TEXT,
                        unit TEXT,
                        data_area_id TEXT,
                        invent_dim_id TEXT,
                        is_required_batch_id INTEGER,
                        last_modified INTEGER,
                        server_created_at INTEGER,
                        is_deleted INTEGER
                    );";
                await createCmd.ExecuteNonQueryAsync();
            }

            // 3. Fetch data and Insert
            var products = await context.Products.AsNoTracking().Where(p => !p.IsDeleted).ToListAsync();

            using var transaction = sqliteConn.BeginTransaction();
            var insertCmd = sqliteConn.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO products (id, name, item_id, bar_code, brand_code, brand_name, color_code, color_name, 
                                     size_code, size_name, unit, data_area_id, invent_dim_id, 
                                     is_required_batch_id, last_modified, server_created_at, is_deleted)
                VALUES ($id, $name, $item_id, $bar_code, $brand_code, $brand_name, $color_code, $color_name, 
                        $size_code, $size_name, $unit, $data_area_id, $invent_dim_id, 
                        $is_required_batch_id, $last_modified, $server_created_at, $is_deleted)";

            // Define parameters once for performance
            var parameters = new[] {
                "$id", "$name", "$item_id", "$bar_code", "$brand_code", "$brand_name", "$color_code", "$color_name",
                "$size_code", "$size_name", "$unit", "$data_area_id", "$invent_dim_id", 
                "$is_required_batch_id", "$last_modified", "$server_created_at", "$is_deleted"
            }.ToDictionary(p => p, p => insertCmd.Parameters.Add(p, SqliteType.Text));

            foreach (var p in products)
            {
                parameters["$id"].Value = p.Id;
                parameters["$name"].Value = p.Name ?? (object)DBNull.Value;
                parameters["$item_id"].Value = p.ItemId ?? (object)DBNull.Value;
                parameters["$bar_code"].Value = p.BarCode ?? (object)DBNull.Value;
                parameters["$brand_code"].Value = p.BrandCode ?? (object)DBNull.Value;
                parameters["$brand_name"].Value = p.BrandName ?? (object)DBNull.Value;
                parameters["$color_code"].Value = p.ColorCode ?? (object)DBNull.Value;
                parameters["$color_name"].Value = p.ColorName ?? (object)DBNull.Value;
                parameters["$size_code"].Value = p.SizeCode ?? (object)DBNull.Value;
                parameters["$size_name"].Value = p.SizeName ?? (object)DBNull.Value;
                parameters["$unit"].Value = p.Unit ?? (object)DBNull.Value;
                parameters["$data_area_id"].Value = p.DataAreaId ?? (object)DBNull.Value;
                parameters["$invent_dim_id"].Value = p.InventDimId ?? (object)DBNull.Value;
                parameters["$is_required_batch_id"].Value = p.IsRequiredBatchId ? 1 : 0;
                parameters["$last_modified"].Value = p.LastModified;
                parameters["$server_created_at"].Value = p.ServerCreatedAt;
                parameters["$is_deleted"].Value = 0;

                await insertCmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        // 4. Read file into memory and clean up
        byte[] fileBytes = await File.ReadAllBytesAsync(tempPath);
        return fileBytes;
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}

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