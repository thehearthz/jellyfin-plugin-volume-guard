using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.VolumeGuard;

/// <summary>Fixed, validated DSP. No administrator-supplied FFmpeg expressions are executed.</summary>
public static class ServerAudio
{
    public static string? Filter(Configuration config, DateTimeOffset now)
    {
        var policy = Policy.Evaluate(config, now);
        if (!config.ServerProcessingEnabled || !config.Enabled) return null;
        var mode = config.DefaultMode == "Auto" ? policy.EffectiveAutoMode : config.DefaultMode;
        if (mode == "Off") return null;
        var p = mode == "Night" ? config.Night : config.Balanced;
        static string N(double n) => n.ToString("0.########", CultureInfo.InvariantCulture);
        static double Linear(double db) => Math.Pow(10, db / 20);
        // Linked channels maintain the stereo/surround image. Silence gating avoids raising
        // the noise floor. alimiter's automatic level compensation MUST stay disabled.
        return "dynaudnorm=f=150:g=15:p=" + N(Linear(p.LimiterCeilingDb))
            + ":m=" + N(Linear(p.MaxGainDb)) + ":r=" + N(Linear(p.TargetRmsDb)) + ":n=1:c=1:t=0.003"
            + ",acompressor=threshold=" + N(Linear(p.TargetRmsDb)) + ":ratio=" + (mode == "Night" ? "6" : "3")
            + ":attack=" + N(p.AttackMs) + ":release=" + N(p.ReleaseMs) + ":makeup=1:link=maximum"
            + ",alimiter=limit=" + N(Linear(p.LimiterCeilingDb)) + ":attack=5:release=50:level=0:latency=1";
    }

    // Jellyfin emits a simple quoted -af graph. Support equivalent stream selectors too.
    // Reject unusual/ambiguous audio graph layouts instead of corrupting FFmpeg arguments.
    private static readonly Regex AudioFilter = new(@"(?<!\S)(?:-af|-filter:a(?::0)?|-filter:a:0)\s+(?<value>""(?:\\.|[^""\\])*""|'[^']*'|[^\s]+)", RegexOptions.CultureInvariant);
    private static readonly Regex AudioCodec = new(@"(?<!\S)(?:-acodec|-codec:a(?::0)?|-c:a(?::0)?)\s+(?<codec>[^\s]+)", RegexOptions.CultureInvariant);

    public static string AppendFilter(string arguments, string filter)
    {
        foreach (Match codec in AudioCodec.Matches(arguments))
            if (codec.Groups["codec"].Value.Trim('"', '\'').Equals("copy", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Volume Guard requires audio encoding; FFmpeg requested audio stream copy.");
        var matches = AudioFilter.Matches(arguments);
        if (matches.Count > 1) throw new InvalidOperationException("Multiple audio filter options are not supported.");
        if (matches.Count == 1)
        {
            var value = matches[0].Groups["value"];
            var old = value.Value;
            var quote = old[0] is '"' or '\'' ? old[0] : '"';
            var body = old[0] is '"' or '\'' ? old[1..^1] : old;
            return arguments[..value.Index] + quote + body + "," + filter + quote + arguments[(value.Index + value.Length)..];
        }
        // Inserting before all input options is unsafe: -af is an output option. Jellyfin
        // places its output URL last, which can be a quoted path containing whitespace.
        var output = Regex.Match(arguments, @"(?<output>""(?:\\.|[^""\\])*""|'[^']*'|[^\s]+)\s*$", RegexOptions.CultureInvariant);
        if (!output.Success) throw new InvalidOperationException("FFmpeg output argument was not found.");
        return arguments.Insert(output.Index, "-af \"" + filter + "\" ");
    }

    public static string PrependFilter(string arguments, string filter)
    {
        var matches = AudioFilter.Matches(arguments);
        if (matches.Count > 1) throw new InvalidOperationException("Multiple audio filter options are not supported.");
        if (matches.Count == 0) return AppendFilter(arguments, filter);
        var value = matches[0].Groups["value"];
        var old = value.Value;
        var quote = old[0] is '"' or '\'' ? old[0] : '"';
        var body = old[0] is '"' or '\'' ? old[1..^1] : old;
        return arguments[..value.Index] + quote + filter + "," + body + quote + arguments[(value.Index + value.Length)..];
    }
}
