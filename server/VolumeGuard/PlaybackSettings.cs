using System.Text.Json;
using System.Globalization;

namespace Jellyfin.Plugin.VolumeGuard;

public static class PlaybackSettings
{
    // Never mutate persisted/shared configuration while applying a device override.
    public static Configuration Resolve(string? deviceId)
    {
        lock (Plugin.ConfigurationLock) return Resolve(Plugin.Instance.Configuration, deviceId);
    }

    public static Configuration Resolve(Configuration configuration, string? deviceId)
    {
        Policy.Validate(configuration);
        var copy = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(configuration))!;
        var device = copy.DeviceOverrides.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
        if (device is not null)
        {
            copy.Enabled &= device.Enabled;
            copy.DefaultMode = device.Mode;
            copy.DialogueBoostDb = device.DialogueBoostDb;
        }
        return copy;
    }

    public static string Mode(Configuration configuration, DateTimeOffset now)
    {
        var policy = Policy.Evaluate(configuration, now);
        return !configuration.Enabled || !configuration.ServerProcessingEnabled ? "Off"
            : configuration.DefaultMode == "Auto" ? policy.EffectiveAutoMode : configuration.DefaultMode;
    }

    public static string Fingerprint(string? deviceId, DateTimeOffset now)
    {
        var config = Resolve(deviceId);
        return Fingerprint(config, now);
    }

    public static string Fingerprint(Configuration config, DateTimeOffset now)
    {
        var filter = ServerAudio.Filter(config, now);
        if (filter is null) return "off";
        return filter + "|dialogue=" + config.DialogueBoostDb.ToString(CultureInfo.InvariantCulture)
            + "|analysis=" + config.LoudnessAnalysisEnabled + ":" + config.AnalysisTargetLufs.ToString(CultureInfo.InvariantCulture);
    }
}
