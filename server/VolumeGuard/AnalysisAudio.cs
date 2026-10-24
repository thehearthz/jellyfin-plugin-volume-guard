using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed record AnalysisMeasurement(double IntegratedLufs, double TruePeakDb, double LoudnessRange);

public static class AnalysisAudio
{
    public static AnalysisMeasurement? Parse(string output)
    {
        var end = output.LastIndexOf('}');
        var start = end < 0 ? -1 : output.LastIndexOf('{', end);
        if (start < 0) return null;
        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)]);
            static double Number(JsonElement root, string field) => double.Parse(root.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);
            var m = new AnalysisMeasurement(Number(json.RootElement, "input_i"), Number(json.RootElement, "input_tp"), Number(json.RootElement, "input_lra"));
            return double.IsFinite(m.IntegratedLufs) && m.IntegratedLufs >= -100 && m.IntegratedLufs <= 10
                && double.IsFinite(m.TruePeakDb) && m.TruePeakDb >= -150 && m.TruePeakDb <= 30
                && double.IsFinite(m.LoudnessRange) && m.LoudnessRange >= 0 && m.LoudnessRange <= 100 ? m : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentNullException) { return null; }
    }

    public static double Gain(AnalysisMeasurement measurement, double targetLufs, double maxGainDb, double ceilingDb)
    {
        if (!double.IsFinite(targetLufs) || targetLufs < -36 || targetLufs > -10
            || !double.IsFinite(maxGainDb) || maxGainDb < 0 || maxGainDb > 24
            || !double.IsFinite(ceilingDb) || ceilingDb < -20 || ceilingDb > 0)
            throw new ArgumentOutOfRangeException(nameof(targetLufs));
        // Fixed title gain is bounded by BOTH allowed boost and the measured true peak.
        // Subsequent dynamics/EQ and lossy encoding can still change the final peak.
        return Math.Min(Math.Min(targetLufs - measurement.IntegratedLufs, maxGainDb), ceilingDb - measurement.TruePeakDb);
    }
}
