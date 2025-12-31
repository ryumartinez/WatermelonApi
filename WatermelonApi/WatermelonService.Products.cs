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