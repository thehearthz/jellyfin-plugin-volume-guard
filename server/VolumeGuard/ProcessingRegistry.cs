using System.Collections.Concurrent;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed record ProcessingRecord(string DeviceId, string PlaySessionId, string Mode, string Dialogue,
    string Analysis, string Fingerprint, DateTimeOffset StartedUtc, string State, string Reason, Guid ItemId);

// Records what reached the encoder hook, never a fabricated acoustic/gain meter.
public static class ProcessingRegistry
{
    private static readonly ConcurrentDictionary<string, ProcessingRecord> Records = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Switches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> PlaybackSessions = new(StringComparer.Ordinal);
    public static void ObservePlayback(string sessionId, string? playSessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        if (string.IsNullOrWhiteSpace(playSessionId)) PlaybackSessions.TryRemove(sessionId, out _);
        else PlaybackSessions[sessionId] = playSessionId;
        if (PlaybackSessions.Count > 1024) PlaybackSessions.Clear();
    }
    public static string? GetPlaybackSession(string sessionId) => PlaybackSessions.GetValueOrDefault(sessionId);
    public static void StopPlayback(string sessionId, string? playSessionId)
    {
        if (playSessionId is not null) ((ICollection<KeyValuePair<string, string>>)PlaybackSessions).Remove(new(sessionId, playSessionId));
    }
    public static void Record(ProcessingRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.DeviceId)) return;
        Records[record.DeviceId] = record;
        foreach (var item in Records.Where(p => p.Value.StartedUtc < DateTimeOffset.UtcNow.AddDays(-1))) Records.TryRemove(item.Key, out _);
        if (Records.Count > 512)
            foreach (var item in Records.OrderBy(p => p.Value.StartedUtc).Take(Records.Count - 512)) Records.TryRemove(item.Key, out _);
    }
    public static ProcessingRecord? Get(string deviceId) => Records.GetValueOrDefault(deviceId);
    public static string? GetAppliedFingerprint(string deviceId) => Get(deviceId)?.Fingerprint;
    public static void SetSwitch(string deviceId, string state)
    {
        if (!string.IsNullOrWhiteSpace(deviceId)) Switches[deviceId] = state;
        if (Switches.Count > 512) Switches.Clear();
    }
    public static string? GetSwitch(string deviceId) => Switches.GetValueOrDefault(deviceId);
}
