using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VolumeGuard;

[ApiController]
[Route("VolumeGuard/status")]
[Authorize(Policy = "RequiresElevation")]
public sealed class VolumeGuardStatusController(IServiceProvider services) : ControllerBase
{
    [HttpGet]
    public IActionResult GetStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = services.GetService<ISessionManager>()?.Sessions.ToArray() ?? [];
        var manager = services.GetService<ITranscodeManager>();
        var analysis = services.GetService<AnalysisService>()?.Snapshot();
        Configuration configuration;
        lock (Plugin.ConfigurationLock) configuration = Plugin.Instance.Configuration;
        try { Policy.Validate(configuration); }
        catch (ArgumentException) { return StatusCode(503, new ProblemDetails { Status = 503, Title = "Volume Guard configuration is invalid. Save valid settings to restore status." }); }
        var known = sessions.Select(s => s.DeviceId).Concat(configuration.DeviceOverrides.Select(d => d.DeviceId))
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal);
        var devices = known.Select(id =>
        {
            var session = sessions.Where(s => s.DeviceId == id).OrderByDescending(s => s.LastActivityDate).FirstOrDefault();
            var record = ProcessingRegistry.Get(id);
            var sameItem = session?.NowPlayingItem is not null && record?.ItemId == session.NowPlayingItem.Id;
            var confirmed = sameItem && record?.State == "launched" && !string.IsNullOrEmpty(record.PlaySessionId)
                && ProcessingRegistry.GetPlaybackSession(session!.Id) == record.PlaySessionId
                && manager?.GetTranscodingJob(record.PlaySessionId) is not null && session!.PlayState.PlayMethod == MediaBrowser.Model.Session.PlayMethod.Transcode;
            string desired;
            string desiredFingerprint;
            try { var settings = PlaybackSettings.Resolve(id); desired = PlaybackSettings.Mode(settings, now); desiredFingerprint = PlaybackSettings.Fingerprint(settings, now); }
            catch (ArgumentException) { desired = "Invalid settings"; desiredFingerprint = "invalid"; }
            return new
            {
                deviceId = id, deviceName = session?.DeviceName ?? configuration.DeviceOverrides.FirstOrDefault(d => d.DeviceId == id)?.DeviceName ?? id,
                client = session?.Client ?? "Offline", mode = desired, processing = confirmed,
                reason = session?.NowPlayingItem is null ? "No active playback."
                    : confirmed ? $"Confirmed encoder launch: {record!.Mode}. " + (record.Fingerprint != desiredFingerprint ? "Requested settings differ; reconnect or start playback again. " : "") + ProcessingRegistry.GetSwitch(id)
                    : sameItem && record?.State == "failed" ? record.Reason
                    : "No matching processed encoding job is confirmed for this playback. " + ProcessingRegistry.GetSwitch(id),
                dialogue = confirmed ? record!.Dialogue : "Not confirmed", analysis = confirmed ? record!.Analysis : "Not confirmed",
                playSessionId = sameItem ? record?.PlaySessionId : null
            };
        }).ToArray();
        return Ok(new
        {
            serverTimeUtc = now, devices,
            analysis = new
            {
                enabled = configuration.LoudnessAnalysisEnabled, cachedTracks = analysis?.CachedTracks ?? 0,
                running = analysis?.Running ?? false, lastError = analysis?.LastError, lastScanUtc = analysis?.LastScanUtc, progress = analysis?.Progress ?? 0,
                summary = analysis is null ? "Analysis service is unavailable." :
                    $"{analysis.CachedTracks} cached audio tracks. " + (analysis.Running ? $"Scanning: {analysis.Progress:0}% complete." : "Not scanning.")
                    + (!configuration.LoudnessAnalysisEnabled ? " Analysis is disabled." : "") + (analysis.LastError is null ? "" : " " + analysis.LastError)
            }
        });
    }
}
