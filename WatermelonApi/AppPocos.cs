using System.Text.Json.Serialization;

namespace WatermelonApi;

public class WatermelonProduct
{
    // WatermelonDB Metadata (Required)
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long LastModified { get; set; } 
    public long ServerCreatedAt { get; set; } 
    public bool IsDeleted { get; set; }

    // Your Business Fields
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
    // WatermelonDB Metadata (Required)
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long LastModified { get; set; } 
    public long ServerCreatedAt { get; set; } 
    public bool IsDeleted { get; set; }

    // Your Business Fields
    public string DataAreaId { get; set; } = string.Empty;
    public string ItemNumber { get; set; } = string.Empty;
    public string BatchNumber { get; set; } = string.Empty;
    public string? VendorBatchNumber { get; set; }
    public long? VendorExpirationDate { get; set; }
    public long? BatchExpirationDate { get; set; } 
}

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