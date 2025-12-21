using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class WatermelonService(AppDbContext context)
{
    // Logic for Initial SQLite File Generation
    public async Task<string> CreateInitialDatabaseAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        using var pragma = new SqliteCommand("PRAGMA user_version = 1;", connection);
        await pragma.ExecuteNonQueryAsync();

        // 1. Updated price type to TEXT in the schema
        using var createTable = new SqliteCommand(@"
            CREATE TABLE products (
                id TEXT PRIMARY KEY, name TEXT, price TEXT, sku TEXT, 
                _status TEXT, _changed TEXT, created_at INTEGER, updated_at INTEGER
            );", connection);
        await createTable.ExecuteNonQueryAsync();

        var products = await context.Products.Where(p => !p.IsDeleted).ToListAsync();
        using var transaction = connection.BeginTransaction();
        var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO products (id, name, price, sku, _status, _changed, created_at, updated_at) " +
                             "VALUES ($id, $name, $price, $sku, 'synced', '', $at, $at)";

        var parameters = new[] { "$id", "$name", "$price", "$sku", "$at" }
            .ToDictionary(p => p, p => insert.Parameters.Add(p, SqliteType.Text));

        foreach (var p in products)
        {
            parameters["$id"].Value = p.Id;
            parameters["$name"].Value = p.Name ?? (object)DBNull.Value;
            
            // 2. Explicitly convert price to string. 
            // Using InvariantCulture ensures the decimal separator is always a dot (.)
            parameters["$price"].Value = p.Price.ToString(CultureInfo.InvariantCulture);
            
            parameters["$sku"].Value = p.Sku ?? (object)DBNull.Value;
            parameters["$at"].Value = p.LastModified;
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return dbPath;
    }
    
    public async Task<SyncPullResponse> GetPullChangesAsync(long lastPulledAt)
    {
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isFirstSync = lastPulledAt == 0;

        var changes = await context.Products
            .Where(p => p.LastModified > lastPulledAt)
            .ToListAsync();

        var tableChanges = new TableChanges(
            Created: changes.Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt)).Cast<object>().ToList(),
            Updated: changes.Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt).Cast<object>().ToList(),
            Deleted: changes.Where(p => p.IsDeleted).Select(p => p.Id).ToList()
        );

        return new SyncPullResponse(new() { { "products", tableChanges } }, serverTimestamp);
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