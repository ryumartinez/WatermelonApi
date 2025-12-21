using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class WatermelonService(AppDbContext context)
{
    public async Task<string> CreateInitialDatabaseAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.db");
        
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();

            using var pragma = new SqliteCommand("PRAGMA user_version = 1;", connection);
            await pragma.ExecuteNonQueryAsync();

            using var createTable = new SqliteCommand(@"
                CREATE TABLE products (
                    id TEXT PRIMARY KEY,
                    name TEXT,
                    price REAL,
                    sku TEXT,
                    _status TEXT,
                    _changed TEXT,
                    created_at INTEGER,
                    updated_at INTEGER
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
                parameters["$name"].Value = p.Name;
                parameters["$price"].Value = p.Price;
                parameters["$sku"].Value = p.Sku;
                parameters["$at"].Value = p.LastModified;
                await insert.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        return dbPath;
    }
}