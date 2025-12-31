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
            Created: changes.Where(p => !p.IsDeleted && (isFirstSync || p.ServerCreatedAt > lastPulledAt)).Cast<object>().ToList(),
            Updated: changes.Where(p => !p.IsDeleted && !isFirstSync && p.ServerCreatedAt <= lastPulledAt).Cast<object>().ToList(),
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
                var newBatch = new WatermelonProductBatch { Id = id, ServerCreatedAt = now };
                UpdateBatchFields(newBatch, raw, now);
                context.ProductBatches.Add(newBatch);
            }
        }
        await HandleDeletions(context.ProductBatches, changes.Deleted, now);
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
}