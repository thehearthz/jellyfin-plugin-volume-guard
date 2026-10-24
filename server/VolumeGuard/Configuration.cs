using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed class Configuration : BasePluginConfiguration
{
    [JsonPropertyName("serverProcessingEnabled")] public bool ServerProcessingEnabled { get; set; }
    [JsonPropertyName("switchDuringPlayback")] public bool AutomaticPlaybackSwitchingEnabled { get; set; }
    [JsonPropertyName("dialogueBoostDb")] public double DialogueBoostDb { get; set; }
    [JsonPropertyName("loudnessAnalysisEnabled")] public bool LoudnessAnalysisEnabled { get; set; }
    [JsonPropertyName("analysisTargetLufs")] public double AnalysisTargetLufs { get; set; } = -20;
    [JsonPropertyName("deviceOverrides")] public DeviceOverride[] DeviceOverrides { get; set; } = [];
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("defaultMode")] public string DefaultMode { get; set; } = "Auto";
    [JsonPropertyName("daytimeMode")] public string DaytimeMode { get; set; } = "Balanced";
    [JsonPropertyName("timeZoneId")] public string TimeZoneId { get; set; } = "UTC";
    [JsonPropertyName("scheduleEnabled")] public bool ScheduleEnabled { get; set; } = true;
    [JsonPropertyName("nightWindows")] public NightWindow[] NightWindows { get; set; } = [new()];
    [JsonPropertyName("balanced")] public AudioProfile Balanced { get; set; } = new();
    [JsonPropertyName("night")] public AudioProfile Night { get; set; } = new() { TargetRmsDb = -26, MaxGainDb = 9, LimiterCeilingDb = -4, AttackMs = 5, ReleaseMs = 800 };
}

public sealed class DeviceOverride
{
    [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = "";
    [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("mode")] public string Mode { get; set; } = "Auto";
    [JsonPropertyName("dialogueBoostDb")] public double DialogueBoostDb { get; set; }
}

public sealed class NightWindow
{
    // Days name the day on which the window STARTS. Sunday=0, Saturday=6.
    [JsonPropertyName("days")] public int[] Days { get; set; } = [0, 1, 2, 3, 4, 5, 6];
    [JsonPropertyName("start")] public string Start { get; set; } = "22:00";
    [JsonPropertyName("end")] public string End { get; set; } = "07:00";
}

public sealed class AudioProfile
{
    [JsonPropertyName("targetRmsDb")] public double TargetRmsDb { get; set; } = -20;
    [JsonPropertyName("maxGainDb")] public double MaxGainDb { get; set; } = 6;
    [JsonPropertyName("limiterCeilingDb")] public double LimiterCeilingDb { get; set; } = -1;
    [JsonPropertyName("attackMs")] public double AttackMs { get; set; } = 10;
    [JsonPropertyName("releaseMs")] public double ReleaseMs { get; set; } = 500;
}
