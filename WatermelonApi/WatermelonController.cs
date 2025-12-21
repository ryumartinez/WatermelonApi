using Microsoft.AspNetCore.Mvc;

namespace WatermelonApi;

[ApiController]
[Route("api/sync")]
public class WatermelonController(WatermelonService dbService) : ControllerBase
{
    [HttpGet("seed-database")]
    public async Task<IActionResult> GetTurboSync()
    {
        byte[] fileData = await dbService.GenerateSqliteSyncFileAsync();
        return File(
            fileData, 
            "application/x-sqlite3", 
            $"sync_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.db"
        );
    }
    
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