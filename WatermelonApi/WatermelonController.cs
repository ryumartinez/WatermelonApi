using Microsoft.AspNetCore.Mvc;

namespace WatermelonApi;

[ApiController]
[Route("api/sync")]
public class WatermelonController(WatermelonService dbService) : ControllerBase
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
    public async Task<ActionResult<SyncPullResponse>> Pull([FromQuery] long lastPulledAt)
    {
        var response = await dbService.GetPullChangesAsync(lastPulledAt);
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