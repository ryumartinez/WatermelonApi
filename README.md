
# WatermelonDB Sync Backend (.NET 9)

This service implements the **WatermelonDB Sync Protocol** to synchronize offline-first data between mobile clients (React Native) and a SQL Server database. It handles incremental updates, conflict resolution, and large-dataset optimization using .NET 9 and EF Core.

## 1. System Architecture

The system uses a "Changes-based" synchronization pattern.

* **Offline-First:** The server is the source of truth, but clients can work offline and sync when connected.
* **Incremental Sync:** Clients only download data that has changed since their last sync.
* **Soft Deletes:** Records are never physically deleted; they are marked `IsDeleted = true` so the deletion can propagate to other clients.

## 2. API Reference

### A. Pull Changes (Server  Client)

Fetches all records created, updated, or deleted since the client's last sync.

**Endpoint:** `GET /api/sync/pull`

**Query Parameters:**
| Parameter | Type | Required | Description |
| :--- | :--- | :--- | :--- |
| `last_pulled_at` | `long` | Yes | Unix timestamp (ms) of the client's last successful sync. Send `0` for the first sync. |
| `turbo` | `bool` | No | If `true` and `last_pulled_at=0`, enables **Turbo Mode** (skips object overhead for faster initial load). |

**Response (`200 OK`):**

```json
{
  "changes": {
    "products": {
      "created": [ ... ],
      "updated": [ ... ],
      "deleted": [ "guid-1", "guid-2" ]
    },
    "product_batches": { ... }
  },
  "timestamp": 1706649999123 // Server time at moment of sync
}

```

### B. Push Changes (Client  Server)

Applies local changes made by the client to the server.

**Endpoint:** `POST /api/sync/push`

**Request Body:**

```json
{
  "last_pulled_at": 1706649999123,
  "changes": {
    "products": {
      "created": [ ... ],
      "updated": [ ... ],
      "deleted": [ "guid-id" ]
    },
    "product_batches": { ... }
  }
}

```

**Status Codes:**

* `200 OK`: Sync successful.
* `409 Conflict`: Server has newer data than the client. Client must Pull again before Pushing.
* `400 Bad Request`: General processing error (transaction rolled back).

---

## 3. Data Models

### `WatermelonProduct` (Table: `Products`)

Represents the main inventory item.

| Field | Type | Sync Logic | Description |
| --- | --- | --- | --- |
| `Id` | `string` | **Key** | UUIDv4. |
| `LastModified` | `long` | **Metadata** | Timestamp of last update. Used for sync filtering. |
| `ServerCreatedAt` | `long` | **Metadata** | Timestamp of creation. Used to distinguish "Created" vs "Updated". |
| `IsDeleted` | `bool` | **Metadata** | Soft-delete flag. |
| `Name`, `ItemId`... | `string` | Business | Standard product details. |

### `WatermelonProductBatch` (Table: `ProductBatches`)

Represents specific batches/lots of a product.

| Field | Type | Sync Logic | Description |
| --- | --- | --- | --- |
| `Id` | `string` | **Key** | UUIDv4. |
| `LastModified` | `long` | **Metadata** | Timestamp of last update. |
| `IsDeleted` | `bool` | **Metadata** | Soft-delete flag. |
| `Status` | `string?` | Business | Batch status (e.g., "Available", "Expired"). |
| `BatchExpirationDate` | `long?` | Business | Unix timestamp for expiration. |

---

## 4. Sync Protocol Implementation

### Pull Logic (Download)

1. **Timestamp Filtering:** Queries DB for `LastModified > last_pulled_at`.
2. **Categorization:**
* **Created:** Records where `ServerCreatedAt > last_pulled_at`.
* **Updated:** Records where `ServerCreatedAt <= last_pulled_at` but modified recently.
* **Deleted:** Records where `IsDeleted == true`.


3. **Turbo Mode:** If the client requests a full sync (`last_pulled_at=0`), the server bypasses standard JSON object graphs and streams a pre-serialized string to handle large payloads efficiently.

### Push Logic (Upload)

1. **Transaction:** All changes (Products + Batches) are wrapped in a single database transaction.
2. **Conflict Detection:**
* Before updating a record, the server checks:


* If **True**: The record changed on the server *after* the client last saw it. The push is rejected (`409 Conflict`).


3. **Soft Deletes:** Incoming IDs in the `deleted` array are looked up, and their `IsDeleted` flag is set to `true`.

---

## 5. Development & Setup

### Prerequisites

* .NET 9 SDK
* Docker Desktop (Required for Testcontainers)

### Running the Project

The project uses **Testcontainers**. It will automatically spin up a SQL Server 2022 container when the app starts.

```bash
dotnet run

```

### Automatic Seeding

On startup, the app checks if the database is empty. If so, it seeds:

* **100,000 Products**
* **30,000 Product Batches**

---

## 6. Frontend Integration Notes

To consume this API in a WatermelonDB client, map the endpoints in your `synchronize()` function:

* **Migration Support:** The current implementation sends all data. For datasets >20k rows, consider implementing pagination if mobile clients experience timeouts.
* **JSON Naming:** The server uses `SnakeCaseLower`. Ensure your client models map to these snake_case properties.

---

## 7. The Code Itself

This section contains the full source code implementation for reference.

### 7.1 Program & Configuration (`Program.cs`)

Handles startup, dependency injection, container management, and database seeding.

```csharp
using Microsoft.EntityFrameworkCore;
using WatermelonApi;
using Testcontainers.MsSql;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// 1. Initialize and Start the Container (SQL Server 2022)
var dbContainer = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
    .Build();

await dbContainer.StartAsync();
var connectionString = dbContainer.GetConnectionString();

// 2. Register Services
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
    });

builder.Services.AddDbContext<AppDbContext>(opt => 
    opt.UseSqlServer(connectionString));

builder.Services.AddScoped<WatermelonService>();

var app = builder.Build();

// 3. Seed the Database on Startup
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
    await SeedData(context);
}

app.MapOpenApi();
app.MapScalarApiReference();
app.MapControllers();

app.Run();

// --- Seeding Logic ---
async Task SeedData(AppDbContext context)
{
    var random = new Random();
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var dataAreas = new[] { "US01", "PY01", "BR01" };

    // 1. Seed Products (100,000)
    if (!await context.Products.AnyAsync())
    {
        var brands = new[] { ("NK", "Nike"), ("AD", "Adidas"), ("AP", "Apple"), ("SN", "Sony"), ("LG", "Logitech") };
        var colors = new[] { ("01", "Black"), ("02", "White"), ("03", "Red"), ("04", "Blue"), ("05", "Silver") };
        var sizes = new[] { ("S", "Small"), ("M", "Medium"), ("L", "Large"), ("XL", "Extra Large") };
        var adjectives = new[] { "Premium", "Pro", "Ultra", "Classic", "Limited", "Essential" };
        var categories = new[] { "Headset", "Sneakers", "Watch", "Controller", "Bottle" };

        Console.WriteLine("Seeding 100,000 products...");
        var products = new List<WatermelonProduct>();

        for (int i = 1; i <= 100000; i++)
        {
            var brand = brands[random.Next(brands.Length)];
            var color = colors[random.Next(colors.Length)];
            var size = sizes[random.Next(sizes.Length)];
            var area = dataAreas[random.Next(dataAreas.Length)];
            var productName = $"{adjectives[random.Next(adjectives.Length)]} {brand.Item2} {categories[random.Next(categories.Length)]}";

            products.Add(new WatermelonProduct
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{productName} {i}",
                ItemId = $"ITEM-{random.Next(1000, 9999)}-{i:D4}",
                BarCode = $"789{random.NextInt64(1000000000, 9999999999)}",
                BrandCode = brand.Item1,
                BrandName = brand.Item2,
                ColorCode = color.Item1,
                ColorName = color.Item2,
                SizeCode = size.Item1,
                SizeName = size.Item2,
                Unit = "PCS",
                DataAreaId = area,
                InventDimId = $"DIM-{Guid.NewGuid().ToString()[..8].ToUpper()}",
                IsRequiredBatchId = random.Next(10) > 8,
                LastModified = now,
                ServerCreatedAt = now,
                IsDeleted = false
            });

            if (i % 5000 == 0)
            {
                await context.Products.AddRangeAsync(products);
                await context.SaveChangesAsync();
                products.Clear();
                Console.WriteLine($"Products Progress: {i}/100,000");
            }
        }
    }

    // 2. Seed Product Batches (30,000)
    if (!await context.ProductBatches.AnyAsync())
    {
        Console.WriteLine("Seeding 30,000 product batches...");
        var batches = new List<WatermelonProductBatch>();

        for (int i = 1; i <= 30000; i++)
        {
            var expiryDate = DateTimeOffset.UtcNow.AddDays(random.Next(30, 365)).ToUnixTimeMilliseconds();
        
            batches.Add(new WatermelonProductBatch
            {
                Id = Guid.NewGuid().ToString(),
                DataAreaId = dataAreas[random.Next(dataAreas.Length)],
                ItemNumber = $"ITEM-{random.Next(1000, 9999)}",
                BatchNumber = $"LOT-{random.Next(100, 999)}-{i:D5}",
                VendorBatchNumber = random.Next(10) > 5 ? $"VND-{random.Next(1000, 9999)}" : null,
                BatchExpirationDate = expiryDate,
                VendorExpirationDate = expiryDate,
                LastModified = now,
                IsDeleted = false,
                Status = null,
                Changed = null
            });

            if (i % 5000 == 0)
            {
                await context.ProductBatches.AddRangeAsync(batches);
                await context.SaveChangesAsync();
                batches.Clear();
                Console.WriteLine($"Batches Progress: {i}/30,000");
            }
        }
    }
    Console.WriteLine("Seeding complete.");
}

```

### 7.2 Database Context (`AppDbContext.cs`)

Defines the Entity Framework Core context and model relationships.

```csharp
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<WatermelonProduct> Products => Set<WatermelonProduct>();
    public DbSet<WatermelonProductBatch> ProductBatches => Set<WatermelonProductBatch>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WatermelonProduct>().HasKey(p => p.Id);
        // Indexes are crucial for sync performance
        modelBuilder.Entity<WatermelonProduct>().HasIndex(p => p.LastModified);
        modelBuilder.Entity<WatermelonProductBatch>().HasIndex(p => p.LastModified);
    }
}

```

### 7.3 Data Models & DTOs

These classes define the database schema and the JSON contracts for the sync protocol.

**Models**

```csharp
using System.Text.Json.Serialization;

namespace WatermelonApi;

public class WatermelonProduct
{
    // WatermelonDB Metadata
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long LastModified { get; set; } 
    public long ServerCreatedAt { get; set; } 
    public bool IsDeleted { get; set; }

    // Business Fields
    public string Name { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string BarCode { get; set; } = string.Empty;
    public string BrandCode { get; set; } = string.Empty;
    public string BrandName { get; set; } = string.Empty;
    public string ColorCode { get; set; } = string.Empty;
    public string ColorName { get; set; } = string.Empty;
    public string SizeCode { get; set; } = string.Empty;
    public string SizeName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string DataAreaId { get; set; } = string.Empty;
    public string InventDimId { get; set; } = string.Empty;
    public bool IsRequiredBatchId { get; set; }
}

public class WatermelonProductBatch
{
    public string Id { get; set; } = string.Empty;
    public long LastModified { get; set; }
    public string? Status { get; set; }
    public string? Changed { get; set; }
    public string DataAreaId { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public string? VendorBatchNumber { get; set; }
    public long? VendorExpirationDate { get; set; }
    public long? BatchExpirationDate { get; set; }
    public bool IsDeleted { get; set; }
}

```

**Sync DTOs**

```csharp
public record SyncPullResponse(
    Dictionary<string, TableChanges>? Changes, 
    long Timestamp,
    string? SyncJson = null
);

public record TableChanges(
    [property: JsonPropertyName("created")] List<object> Created,
    [property: JsonPropertyName("updated")] List<object> Updated,
    [property: JsonPropertyName("deleted")] List<string> Deleted
);

public record SyncPushRequest(
    [property: JsonPropertyName("changes")] Dictionary<string, TableChanges> Changes,
    [property: JsonPropertyName("last_pulled_at")] long LastPulledAt
);

```

### 7.4 Sync Logic: Main Service (`WatermelonService.cs`)

Orchestrates the sync process, including transactions and shared helpers.

```csharp
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

```

### 7.5 Sync Logic: Product Partials (`WatermelonService.Products.cs`)

Handles specific logic for querying and updating products.

```csharp
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public partial class WatermelonService
{
    private async Task<TableChanges> GetProductPullChangesAsync(long lastPulledAt, bool isFirstSync)
    {
        var changes = await context.Products
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();

        return new TableChanges(
            Created: changes.Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt)).Cast<object>().ToList(),
            Updated: changes.Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt).Cast<object>().ToList(),
            Deleted: changes.Where(p => p.IsDeleted).Select(p => p.Id).ToList()
        );
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
}

```

### 7.6 Sync Logic: Batch Partials (`WatermelonService.Batches.cs`)

Handles specific logic for querying and updating product batches.

```csharp
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public partial class WatermelonService
{
    private async Task<TableChanges> GetBatchPullChangesAsync(long lastPulledAt, bool isFirstSync)
    {
        var changes = await context.ProductBatches
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();

        return new TableChanges(
            Created: changes.Where(p => !p.IsDeleted && isFirstSync).Cast<object>().ToList(),
            Updated: changes.Where(p => !p.IsDeleted && !isFirstSync).Cast<object>().ToList(),
            Deleted: changes.Where(p => p.IsDeleted).Select(p => p.Id).ToList()
        );
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
                var newBatch = new WatermelonProductBatch { Id = id };
                UpdateBatchFields(newBatch, raw, now);
                context.ProductBatches.Add(newBatch);
            }
        }
        await HandleDeletions(context.ProductBatches, changes.Deleted, now);
    }

    private void UpdateBatchFields(WatermelonProductBatch b, Dictionary<string, object> raw, long now)
    {
        b.Status = GetStr(raw, "_status");
        b.Changed = GetStr(raw, "_changed");
        b.DataAreaId = GetStr(raw, "data_area_id");
        b.ItemNumber = GetStr(raw, "item_number");
        b.BatchNumber = GetStr(raw, "batch_number");
        b.VendorBatchNumber = GetStr(raw, "vendor_batch_number");
        b.VendorExpirationDate = GetNullableLong(raw, "vendor_expiration_date");
        b.BatchExpirationDate = GetNullableLong(raw, "batch_expiration_date");
        b.LastModified = now;
    }
}

```

### 7.7 API Controller (`WatermelonController.cs`)

Exposes the sync endpoints via HTTP.

```csharp
using Microsoft.AspNetCore.Mvc;

namespace WatermelonApi;

[ApiController]
[Route("api/sync")]
public class WatermelonController(WatermelonService dbService) : ControllerBase
{
    [HttpGet("pull")]
    public async Task<ActionResult> Pull(
        [FromQuery(Name = "last_pulled_at")] long lastPulledAt,
        [FromQuery] bool turbo = false)
    {
        var response = await dbService.GetPullChangesAsync(lastPulledAt, turbo);
        
        if (response.SyncJson != null)
        {
            return Content(response.SyncJson, "application/json");
        }
    
        return Ok(response);
    }

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] SyncPushRequest request)
    {
        try 
        {
            await dbService.ProcessPushChangesAsync(request);
            return Ok(new { ok = true });
        }
        catch (InvalidOperationException ex) when (ex.Message == "CONFLICT")
        {
            return Conflict(new { error = "Server has newer changes. Please pull first." });
        }
        catch (Exception)
        {
            return BadRequest(new { error = "Sync failed. Batch rolled back." });
        }
    }
}

```

