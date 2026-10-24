using System.Globalization;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed class PlaybackPolicy
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("serverTimeUtc")] public DateTimeOffset ServerTimeUtc { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("defaultMode")] public string DefaultMode { get; init; } = "Auto";
    [JsonPropertyName("effectiveAutoMode")] public string EffectiveAutoMode { get; init; } = "Off";
    [JsonPropertyName("scheduleActive")] public bool ScheduleActive { get; init; }
    [JsonPropertyName("configuration")] public Configuration Configuration { get; init; } = new();
}

public static class Policy
{
    public static void Validate(Configuration? config)
    {
        if (config is null) throw new ArgumentException("Configuration is required.");
        if (config.DefaultMode is not ("Off" or "Balanced" or "Night" or "Auto")) throw new ArgumentException("defaultMode must be Off, Balanced, Night or Auto.");
        if (config.DaytimeMode is not ("Off" or "Balanced" or "Night")) throw new ArgumentException("daytimeMode must be Off, Balanced or Night.");
        if (string.IsNullOrWhiteSpace(config.TimeZoneId) || config.TimeZoneId.Length > 128) throw new ArgumentException("timeZoneId is required (maximum 128 characters).");
        try { TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("timeZoneId must identify a timezone installed on the server.", ex); }
        if (config.NightWindows is null || config.NightWindows.Length > 32) throw new ArgumentException("nightWindows must contain at most 32 windows.");
        foreach (var window in config.NightWindows)
        {
            if (window is null || window.Days is null || window.Days.Length is < 1 or > 7 || window.Days.Any(d => d < 0 || d > 6) || window.Days.Distinct().Count() != window.Days.Length)
                throw new ArgumentException("Each window must have unique days numbered 0 (Sunday) to 6 (Saturday).");
            var start = ParseTime(window.Start);
            var end = ParseTime(window.End);
            if (start == end) throw new ArgumentException("Window start and end must differ; split an all-day schedule into two windows.");
        }
        ValidateProfile(config.Balanced, "balanced");
        ValidateProfile(config.Night, "night");
    }

    public static TimeOnly ParseTime(string? value)
    {
        if (value is null || value.Length != 5 || !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new ArgumentException("Schedule times must use 24-hour HH:mm format.");
        return result;
    }

    private static void ValidateProfile(AudioProfile? profile, string name)
    {
        if (profile is null) throw new ArgumentException($"{name} profile is required.");
        Range(profile.TargetRmsDb, -40, -6, name + ".targetRmsDb");
        Range(profile.MaxGainDb, 0, 24, name + ".maxGainDb");
        Range(profile.LimiterCeilingDb, -20, 0, name + ".limiterCeilingDb");
        Range(profile.AttackMs, 1, 200, name + ".attackMs");
        Range(profile.ReleaseMs, 20, 5000, name + ".releaseMs");
        if (profile.TargetRmsDb > profile.LimiterCeilingDb) throw new ArgumentException($"{name}.targetRmsDb must not exceed limiterCeilingDb.");
    }

    private static void Range(double value, double minimum, double maximum, string field)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentException($"{field} must be between {minimum} and {maximum}.");
    }

    public static PlaybackPolicy Evaluate(Configuration config, DateTimeOffset now)
    {
        Validate(config);
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId));
        var time = TimeOnly.FromDateTime(local.DateTime);
        var day = (int)local.DayOfWeek;
        // Evaluate wall-clock membership from an instant: both occurrences of a repeated DST
        // hour match; nonexistent spring-forward times never occur. End is exclusive.
        var active = config.ScheduleEnabled && config.NightWindows.Any(window =>
        {
            var start = ParseTime(window.Start);
            var end = ParseTime(window.End);
            return start < end
                ? window.Days.Contains(day) && time >= start && time < end
                : (window.Days.Contains(day) && time >= start) || (window.Days.Contains((day + 6) % 7) && time < end);
        });
        return new PlaybackPolicy { ServerTimeUtc = now.ToUniversalTime(), Enabled = config.Enabled, DefaultMode = config.DefaultMode,
            ScheduleActive = active, EffectiveAutoMode = !config.Enabled ? "Off" : active ? "Night" : config.DaytimeMode, Configuration = config };
    }
}
