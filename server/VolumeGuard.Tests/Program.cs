using System.Text.Json;
using System.Diagnostics;
using Jellyfin.Plugin.VolumeGuard;

var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
void Invalid(Action<Configuration> mutate, string name)
{
    var config = new Configuration(); mutate(config);
    try { Policy.Validate(config); throw new Exception("Accepted invalid " + name); }
    catch (ArgumentException) { passed++; }
}
var weekly = new Configuration { NightWindows = [new() { Days = [1], Start = "22:00", End = "07:00" }] };
bool Active(string utc, Configuration? config = null) => Policy.Evaluate(config ?? weekly, DateTimeOffset.Parse(utc)).ScheduleActive;
Check(!Active("2026-10-19T21:59:59Z"), "Monday before start");
Check(Active("2026-10-19T22:00:00Z"), "Start inclusive");
Check(Active("2026-10-20T06:59:59Z"), "Tuesday morning attributed to Monday");
Check(!Active("2026-10-20T07:00:00Z"), "End exclusive");
Check(!Active("2026-10-19T06:00:00Z"), "Monday morning is not Monday start day");
var sunday = new Configuration { NightWindows = [new() { Days = [6], Start = "23:00", End = "02:00" }] };
Check(Active("2026-10-18T01:00:00Z", sunday), "Saturday wraps to Sunday");
var sameDay = new Configuration { NightWindows = [new() { Days = [1], Start = "08:00", End = "17:00" }] };
Check(Active("2026-10-19T08:00:00Z", sameDay), "Same-day start");
Check(!Active("2026-10-20T09:00:00Z", sameDay), "Same-day selected day only");
Check(!Active("2026-10-19T17:00:00Z", sameDay), "Same-day end exclusive");
var eastern = new Configuration { TimeZoneId = "America/New_York", NightWindows = [new() { Days = [0], Start = "01:00", End = "03:00" }] };
Check(Active("2026-11-01T05:30:00Z", eastern), "DST repeated hour first occurrence");
Check(Active("2026-11-01T06:30:00Z", eastern), "DST repeated hour second occurrence");
Check(!Active("2026-11-01T08:00:00Z", eastern), "DST winter offset end");
Check(Active("2026-03-08T06:59:59Z", eastern), "Spring forward before jump");
Check(!Active("2026-03-08T07:00:00Z", eastern), "Spring forward lands at exclusive end");
var india = new Configuration { TimeZoneId = "Asia/Kolkata", NightWindows = [new() { Days = [2], Start = "00:00", End = "01:00" }] };
Check(Active("2026-10-19T18:30:00Z", india), "Half-hour timezone and local day");
weekly.ScheduleEnabled = false;
Check(!Active("2026-10-19T22:00:00Z"), "Schedule disabled");
Check(Policy.Evaluate(weekly, DateTimeOffset.UtcNow).EffectiveAutoMode == "Balanced", "Disabled schedule daytime profile");
weekly.Enabled = false;
Check(Policy.Evaluate(weekly, DateTimeOffset.UtcNow).EffectiveAutoMode == "Off", "Global disable");
Invalid(c => c.DefaultMode = "night", "case-sensitive mode");
Invalid(c => c.DaytimeMode = "Auto", "recursive automatic mode");
Invalid(c => c.TimeZoneId = "Invalid/Nowhere", "timezone");
Invalid(c => c.NightWindows = null!, "null windows");
Invalid(c => c.NightWindows = Enumerable.Range(0, 33).Select(_ => new NightWindow()).ToArray(), "too many windows");
Invalid(c => c.NightWindows[0].Days = [1, 1], "duplicate day");
Invalid(c => c.NightWindows[0].Days = [-1], "negative day");
Invalid(c => c.NightWindows[0].Days = [7], "day too large");
Invalid(c => c.NightWindows[0].Days = [], "empty days");
Invalid(c => c.NightWindows[0].Start = "9:00", "strict time width");
Invalid(c => c.NightWindows[0].Start = "24:00", "invalid hour");
Invalid(c => c.NightWindows[0].End = "22:00", "equal endpoints");
Invalid(c => c.Balanced = null!, "null profile");
Invalid(c => c.Balanced.TargetRmsDb = double.NaN, "NaN");
Invalid(c => c.Night.MaxGainDb = double.PositiveInfinity, "infinity");
Invalid(c => c.Night.MaxGainDb = 25, "unsafe gain");
Invalid(c => c.Night.LimiterCeilingDb = 1, "positive ceiling");
Invalid(c => c.Night.AttackMs = 0, "zero attack");
Invalid(c => c.Night.ReleaseMs = 5001, "release maximum");
Invalid(c => { c.Night.TargetRmsDb = -6; c.Night.LimiterCeilingDb = -20; }, "target above ceiling");
var serialized = JsonSerializer.Serialize(Policy.Evaluate(new(), DateTimeOffset.Parse("2026-10-23T03:00:00Z")));
using var document = JsonDocument.Parse(serialized);
var root = document.RootElement;
Check(root.GetProperty("schemaVersion").GetInt32() == 1, "Contract schema version");
Check(root.GetProperty("effectiveAutoMode").GetString() == "Night", "Contract effective mode");
Check(root.GetProperty("configuration").GetProperty("night").GetProperty("targetRmsDb").GetDouble() == -26, "Contract nested camel case");
Check(root.GetProperty("configuration").GetProperty("nightWindows")[0].GetProperty("days").GetArrayLength() == 7, "Contract weekday array");
var roundTrip = JsonSerializer.Deserialize<PlaybackPolicy>(serialized)!;
Check(roundTrip.Configuration.Night.AttackMs == 5 && roundTrip.ServerTimeUtc.Offset == TimeSpan.Zero, "JSON round trip");
var server = new Configuration { ServerProcessingEnabled = true, DefaultMode = "Balanced" };
var filter = ServerAudio.Filter(server, DateTimeOffset.UtcNow)!;
Check(filter.Contains("level=0") && filter.Contains("link=maximum"), "Limiter makeup disabled and channels linked");
Check(ServerAudio.Filter(new Configuration(), DateTimeOffset.UtcNow) is null, "Legacy configuration opts out of server DSP");
server.Enabled = false;
Check(ServerAudio.Filter(server, DateTimeOffset.UtcNow) is null, "Master disabled bypasses DSP");
server.Enabled = true; server.DefaultMode = "Off";
Check(ServerAudio.Filter(server, DateTimeOffset.UtcNow) is null, "Off mode bypasses DSP");
server.DefaultMode = "Auto"; server.DaytimeMode = "Off";
Check(ServerAudio.Filter(server, DateTimeOffset.Parse("2026-10-23T12:00:00Z")) is null, "Scheduled day off");
Check(ServerAudio.Filter(server, DateTimeOffset.Parse("2026-10-23T23:00:00Z"))!.Contains("ratio=6"), "Scheduled night compression");
var argsOriginal = "-i \"input with spaces.mkv\" -c:v copy -c:a aac -af \"aresample=48000,volume=2\" \"output with spaces.ts\"";
var appended = ServerAudio.AppendFilter(argsOriginal, filter);
Check(appended.Contains("aresample=48000,volume=2," + filter), "Retains resampling/downmix/base volume filters");
Check(appended.Contains("-c:v copy") && appended.EndsWith("\"output with spaces.ts\""), "Video copy and output path retained");
Check(ServerAudio.AppendFilter("-i input -c:a aac output.ts", filter).EndsWith("\" output.ts"), "Audio graph is an output option");
Check(ServerAudio.AppendFilter("-i input -c:a aac -filter:a:0 volume=1 output.ts", filter).Contains("volume=1,"), "Alternate filter selector");
try { ServerAudio.AppendFilter("-i in -c:a copy out", filter); throw new Exception("Accepted copied audio"); } catch (InvalidOperationException) { passed++; }
try { ServerAudio.AppendFilter("-i in -af volume=1 -af volume=2 out", filter); throw new Exception("Accepted ambiguous graph"); } catch (InvalidOperationException) { passed++; }
if (args.Length > 0)
{
    float[] Render(string source, string? graph)
    {
        var start = new ProcessStartInfo(args[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", source, "-t", "12" }) start.ArgumentList.Add(arg);
        if (graph is not null) { start.ArgumentList.Add("-af"); start.ArgumentList.Add(graph); }
        foreach (var arg in new[] { "-f", "f32le", "-acodec", "pcm_f32le", "pipe:1" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(errors.GetAwaiter().GetResult());
        var bytes = output.ToArray(); var floats = new float[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length); return floats;
    }
    static double Rms(float[] samples) => Math.Sqrt(samples.Skip(48000 * 3).Take(48000 * 6).Average(sample => (double)sample * sample));
    var quietRaw = Render("sine=frequency=440:sample_rate=48000,volume=0.08", null);
    var quietLeveled = Render("sine=frequency=440:sample_rate=48000,volume=0.08", filter);
    var gain = Rms(quietLeveled) / Rms(quietRaw);
    Check(gain > 1.4 && gain < 2.05, "Real FFmpeg quiet audio gain respects profile cap");
    var loud = Render("sine=frequency=440:sample_rate=48000,volume=8", filter);
    var max = loud.Max(sample => Math.Abs(sample));
    Check(max <= Math.Pow(10, -1.0 / 20) + 0.0001 && loud.All(float.IsFinite), "Real FFmpeg loud PCM respects peak ceiling");
    var nightGraph = ServerAudio.Filter(new Configuration { ServerProcessingEnabled = true, DefaultMode = "Night" }, DateTimeOffset.UtcNow)!;
    var nightLoud = Render("sine=frequency=440:sample_rate=48000,volume=8", nightGraph);
    Check(Rms(nightLoud) < Rms(loud), "Real FFmpeg Night reduces loud material more strongly");
    var silence = Render("anullsrc=r=48000:cl=stereo", filter);
    Check(silence.All(sample => sample == 0), "Real FFmpeg silence stays silent across linked channels");
    Console.WriteLine($"FFmpeg PCM evidence: quiet gain {20 * Math.Log10(gain):F2} dB; loud peak {20 * Math.Log10(max):F2} dBFS; Night RMS {20 * Math.Log10(Rms(nightLoud)):F2} dBFS.");
}
Console.WriteLine($"PASS: {passed} policy, validation, timezone, DST, DSP composition and JSON contract checks.");
