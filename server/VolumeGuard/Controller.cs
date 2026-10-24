using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.VolumeGuard;

[ApiController]
[Route("VolumeGuard")]
[Authorize]
public sealed class VolumeGuardController : ControllerBase
{
    [HttpGet("policy")]
    public ActionResult<PlaybackPolicy> GetPolicy()
    {
        try
        {
            lock (Plugin.ConfigurationLock) return Policy.Evaluate(Plugin.Instance.Configuration, DateTimeOffset.UtcNow);
        }
        catch (ArgumentException)
        {
            // A manually edited or legacy-invalid configuration must not break playback.
            return StatusCode(503, new ProblemDetails { Title = "Volume Guard configuration is invalid.", Status = 503, Detail = "Ask an administrator to save a valid Volume Guard configuration." });
        }
    }

    [HttpGet("configuration")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<Configuration> GetConfiguration()
    {
        lock (Plugin.ConfigurationLock) return Plugin.Instance.Configuration;
    }

    [HttpPost("configuration")]
    [Authorize(Policy = "RequiresElevation")]
    [RequestSizeLimit(32768)]
    public IActionResult SaveConfiguration([FromBody] Configuration config)
    {
        try { Policy.Validate(config); }
        catch (ArgumentException ex) { return BadRequest(new ProblemDetails { Title = "Invalid Volume Guard configuration.", Detail = ex.Message, Status = 400 }); }
        lock (Plugin.ConfigurationLock) Plugin.Instance.UpdateConfiguration(config);
        return NoContent();
    }
}
