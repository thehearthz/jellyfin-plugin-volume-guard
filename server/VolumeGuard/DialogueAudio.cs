using System.Globalization;

namespace Jellyfin.Plugin.VolumeGuard;

/// <summary>Speech emphasis, not voice isolation. The final limiter remains mandatory.</summary>
public static class DialogueAudio
{
    public static string? Filter(double boostDb, string? channelLayout)
    {
        if (!double.IsFinite(boostDb) || boostDb < 0 || boostDb > 12)
            throw new ArgumentOutOfRangeException(nameof(boostDb));
        if (boostDb == 0) return null;
        var gain = Math.Pow(10, boostDb / 20).ToString("0.########", CultureInfo.InvariantCulture);
        // Explicit allowlist prevents metadata from becoming an arbitrary FFmpeg graph.
        var channels = channelLayout switch
        {
            "3.0" => new[] { "FL", "FR", "FC" },
            "3.0(back)" => new[] { "FL", "FR", "BC" },
            "4.0" => new[] { "FL", "FR", "FC", "BC" },
            "5.0" => new[] { "FL", "FR", "FC", "BL", "BR" },
            "5.0(side)" => new[] { "FL", "FR", "FC", "SL", "SR" },
            "5.1" => new[] { "FL", "FR", "FC", "LFE", "BL", "BR" },
            "5.1(side)" => new[] { "FL", "FR", "FC", "LFE", "SL", "SR" },
            "7.1" => new[] { "FL", "FR", "FC", "LFE", "BL", "BR", "SL", "SR" },
            _ => null
        };
        if (channels is not null && channels.Contains("FC"))
            return "pan=" + channelLayout + "|" + string.Join('|', channels.Select(c => c + "=" + (c == "FC" ? gain + "*" : "") + c));
        // Stereo/mono/unknown layouts have no reliably isolated dialogue channel.
        return "equalizer=f=2000:t=q:w=0.7:g=" + boostDb.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
