using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace WatermelonApi;

[ApiController]
[Route("api/sync")]
public class WatermelonController(AppDbContext context, WatermelonService dbService) : ControllerBase
{
    [HttpGet("seed-db")]
    public async Task<IActionResult> SeedDatabase()
    {
        var path = await dbService.CreateInitialDatabaseAsync();
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        System.IO.File.Delete(path);
        return File(bytes, "application/x-sqlite3", "initial.db");
    }

    [HttpGet("pull")]
    public async Task<ActionResult<SyncPullResponse>> Pull([FromQuery] long last_pulled_at)
    {
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        var changes = await context.Products
            .Where(p => p.LastModified > last_pulled_at)
            .ToListAsync();

        var tableChanges = new TableChanges(
            Created: changes.Where(p => !p.IsDeleted && p.ServerCreatedAt > last_pulled_at).Cast<object>().ToList(),
            Updated: changes.Where(p => !p.IsDeleted && p.ServerCreatedAt <= last_pulled_at).Cast<object>().ToList(),
            Deleted: changes.Where(p => p.IsDeleted).Select(p => p.Id).ToList()
        );

        return Ok(new SyncPullResponse(new() { { "products", tableChanges } }, serverTimestamp));
    }

    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] SyncPushRequest request)
    {
        using var tx = await context.Database.BeginTransactionAsync();
        try {
            if (request.Changes.TryGetValue("products", out var productChanges))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            await context.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(new { ok = true });
        } catch {
            await tx.RollbackAsync();
            return BadRequest();
        }
    }
}