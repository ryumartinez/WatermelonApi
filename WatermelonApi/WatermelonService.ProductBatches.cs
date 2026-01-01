using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

public partial class WatermelonService
{
    private async Task<TableChanges> GetBatchPullChangesAsync(long lastPulledAt, bool isFirstSync)
    {
        // Filter changes based on the LastModified timestamp
        var changes = await context.ProductBatches
            .Where(p => isFirstSync || p.LastModified > lastPulledAt)
            .ToListAsync();

        return new TableChanges(
            // If it's the first sync, everything non-deleted is "Created". 
            // Otherwise, we treat modifications as "Updated".
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
                // Conflict detection based on LastModified
                if (record.LastModified > lastPulledAt) throw new InvalidOperationException("CONFLICT");
                UpdateBatchFields(record, raw, now);
            }
            else
            {
                // Version 1 uses empty string default for Id, no ServerCreatedAt
                var newBatch = new WatermelonProductBatch { Id = id };
                UpdateBatchFields(newBatch, raw, now);
                context.ProductBatches.Add(newBatch);
            }
        }
        await HandleDeletions(context.ProductBatches, changes.Deleted, now);
    }

    private void UpdateBatchFields(WatermelonProductBatch b, Dictionary<string, object> raw, long now)
    {
        // Metadata fields from Version 1
        b.Status = GetStr(raw, "_status");
        b.Changed = GetStr(raw, "_changed");
        
        // Business Fields
        b.DataAreaId = GetStr(raw, "data_area_id");
        b.ItemNumber = GetStr(raw, "item_number");
        b.BatchNumber = GetStr(raw, "batch_number");
        b.VendorBatchNumber = GetStr(raw, "vendor_batch_number");
        b.VendorExpirationDate = GetNullableLong(raw, "vendor_expiration_date");
        b.BatchExpirationDate = GetNullableLong(raw, "batch_expiration_date");
        
        // Update the sync timestamp
        b.LastModified = now;
    }
}