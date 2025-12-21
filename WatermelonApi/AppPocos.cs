using System.Text.Json.Serialization;

namespace WatermelonApi;

public class Product
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Sku { get; set; } = string.Empty;
    
    public long LastModified { get; set; } // Timestamp Unix en ms
    public long ServerCreatedAt { get; set; } 
    public bool IsDeleted { get; set; } // Soft Delete para informar a otros clientes
}

public record SyncPullResponse(
    [property: JsonPropertyName("changes")] Dictionary<string, TableChanges> Changes,
    [property: JsonPropertyName("timestamp")] long Timestamp
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