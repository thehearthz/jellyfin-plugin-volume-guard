using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.VolumeGuard;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
Check(DialogueAudio.Filter(0, "5.1") is null, "Zero boost must bypass.");
Check(DialogueAudio.Filter(6, "5.1")!.Contains("FC=1.99526231*FC", StringComparison.Ordinal), "Known layout center gain.");
Check(DialogueAudio.Filter(6, "stereo")!.StartsWith("equalizer=", StringComparison.Ordinal), "Stereo uses honest speech EQ.");
Check(DialogueAudio.Filter(6, "5.1|evil")!.StartsWith("equalizer=", StringComparison.Ordinal), "Layout metadata cannot inject expressions.");
try { DialogueAudio.Filter(double.NaN, "stereo"); throw new Exception("Invalid boost accepted"); } catch (ArgumentOutOfRangeException) { checks++; }
Check(AnalysisAudio.Parse("noise {\"input_i\":\"-24.5\",\"input_tp\":\"-5.0\",\"input_lra\":\"3.0\"}")?.IntegratedLufs == -24.5, "JSON summary parser.");
Check(AnalysisAudio.Parse("{\"input_i\":\"-inf\",\"input_tp\":\"-inf\",\"input_lra\":\"0\"}") is null, "Silence cannot create infinite gain.");
Check(AnalysisAudio.Parse("{broken}") is null, "Malformed summary rejected.");
Check(AnalysisAudio.Gain(new(-40, -20, 1), -20, 6, -1) == 6, "Gain cap.");
Check(AnalysisAudio.Gain(new(-25, -2, 1), -20, 6, -1) == 1, "True peak headroom cap.");
Check(AnalysisAudio.Gain(new(-10, -1, 1), -26, 6, -4) == -16, "Loud title attenuated.");
var encoder = Environment.GetEnvironmentVariable("VOLUMEGUARD_FFMPEG");
if (string.IsNullOrWhiteSpace(encoder) || !File.Exists(encoder))
{
    Console.WriteLine($"PASS: {checks} dialogue/analysis checks. Real FFmpeg tests skipped: set VOLUMEGUARD_FFMPEG.");
    return;
}
var directory = Path.Combine(Path.GetTempPath(), "volume-guard-analysis-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
async Task<string> Run(params string[] arguments)
{
    var start = new ProcessStartInfo(encoder) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var arg in arguments) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    await stdout;
    var output = await stderr;
    if (process.ExitCode != 0) throw new Exception("FFmpeg test failed: " + output);
    return output;
}
try
{
    var quiet = Path.Combine(directory, "quiet space & title.wav");
    var loud = Path.Combine(directory, "loud.wav");
    foreach (var pair in new[] { (quiet, "0.02"), (loud, "0.1") })
        await Run("-hide_banner", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=4:sample_rate=48000", "-af", "volume=" + pair.Item2, "-c:a", "pcm_f32le", pair.Item1);
    var cache = Path.Combine(directory, "cache");
    using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
    var service = new AnalysisService(cache, encoder, loggerFactory.CreateLogger<AnalysisService>());
    var startupPaths = DispatchProxy.Create<IApplicationPaths, AnalysisPathProxy>();
    ((AnalysisPathProxy)(object)startupPaths).Folder = Path.Combine(directory, "startup-data");
    var startupEncoder = DispatchProxy.Create<IMediaEncoder, AnalysisEncoderProxy>();
    var startupService = new AnalysisService(startupPaths, startupEncoder, NullLogger<AnalysisService>.Instance);
    Check(!await startupService.AnalyzeAsync(quiet, 0, default), "Analysis before encoder initialization safely defers.");
    ((AnalysisEncoderProxy)(object)startupEncoder).EncoderPath = encoder;
    Check(await startupService.AnalyzeAsync(quiet, 0, default), "Service constructed before startup uses encoder path configured later.");
    Check(startupService.TryGetGain(quiet, 0, -33, 24, -1).HasValue, "Late-bound encoder measurement becomes available to playback.");
    Check(service.TryGetGain("https://example.invalid/live", 0, -33, 24, -1) is null, "Remote streams excluded.");
    Check(await service.AnalyzeAsync(quiet, 0, default), "Analyze complete quiet title.");
    Check(await service.AnalyzeAsync(loud, 0, default), "Analyze complete loud title.");
    Check(service.Snapshot().CachedTracks == 2, "Track cache count.");
    var quietGain = service.TryGetGain(quiet, 0, -33, 24, -1)!.Value;
    var loudGain = service.TryGetGain(loud, 0, -33, 24, -1)!.Value;
    Check(Math.Abs((quietGain - loudGain) - 13.98) < 0.3, "Title gain compensates measured loudness difference.");
    async Task<AnalysisMeasurement> MeasureGain(string path, double gain) => AnalysisAudio.Parse(await Run("-hide_banner", "-i", path, "-af", "volume=" + gain.ToString(CultureInfo.InvariantCulture) + "dB,loudnorm=print_format=json", "-f", "null", "-"))!;
    var correctedQuiet = await MeasureGain(quiet, quietGain);
    var correctedLoud = await MeasureGain(loud, loudGain);
    Check(Math.Abs(correctedQuiet.IntegratedLufs - correctedLoud.IntegratedLufs) < 0.3, "Measured corrected titles converge.");
    var reloaded = new AnalysisService(cache, encoder, NullLogger<AnalysisService>.Instance);
    Check(reloaded.TryGetGain(quiet, 0, -33, 24, -1) == quietGain, "Persistent cache survives service restart.");
    Check(reloaded.TryGetGain(quiet, 1, -33, 24, -1) is null, "Wrong audio track cannot reuse measurement.");
    File.SetLastWriteTimeUtc(quiet, DateTime.UtcNow.AddMinutes(1));
    Check(reloaded.TryGetGain(quiet, 0, -33, 24, -1) is null, "File modification invalidates cache.");
    Check(!await service.AnalyzeAsync(loud, 9, default), "Invalid audio index fails without cached result.");
    Check(await service.AnalyzeAsync(quiet, 0, default), "Changed file reanalyzed.");
    var surround = Path.Combine(directory, "surround.wav");
    await Run("-hide_banner", "-y", "-f", "lavfi", "-i", "aevalsrc=0|0|0.05*sin(2*PI*440*t)|0|0|0:d=2:s=48000:c=5.1", "-c:a", "pcm_f32le", surround);
    async Task<double> CenterRms(bool boost)
    {
        var output = Path.Combine(directory, boost ? "boost.raw" : "base.raw");
        var graph = (boost ? DialogueAudio.Filter(6, "5.1") + "," : "") + "pan=stereo|FL=FL+0.707*FC|FR=FR+0.707*FC,alimiter=limit=0.891:level=0:latency=1";
        await Run("-hide_banner", "-y", "-i", surround, "-af", graph, "-c:a", "pcm_f32le", "-f", "f32le", output);
        var bytes = await File.ReadAllBytesAsync(output);
        double sum = 0;
        for (var n = 0; n < bytes.Length; n += 4) { var value = BitConverter.ToSingle(bytes, n); sum += value * value; }
        return Math.Sqrt(sum / (bytes.Length / 4));
    }
    var baseline = await CenterRms(false); var emphasized = await CenterRms(true);
    Check(Math.Abs(20 * Math.Log10(emphasized / baseline) - 6) < 0.1, "Center boost survives Jellyfin-style stereo downmix.");
    // Exercise the real manager decoration with a measurement produced by FFmpeg,
    // rather than mirroring its graph construction in a unit helper.
    Check(await service.AnalyzeAsync(surround, 0, default), "Surround source analysis ready for real launch hook.");
    var applicationPaths = DispatchProxy.Create<IApplicationPaths, AnalysisPathProxy>();
    ((AnalysisPathProxy)(object)applicationPaths).Folder = directory;
    var plugin = new Plugin(applicationPaths, DispatchProxy.Create<IXmlSerializer, AnalysisXmlProxy>());
    plugin.Configuration.ServerProcessingEnabled = true;
    plugin.Configuration.DefaultMode = "Balanced";
    plugin.Configuration.LoudnessAnalysisEnabled = true;
    plugin.Configuration.AnalysisTargetLufs = -10;
    plugin.Configuration.DialogueBoostDb = 6;
    plugin.Configuration.Balanced.MaxGainDb = 6;
    var originalManager = DispatchProxy.Create<ITranscodeManager, AnalysisTranscodeProxy>();
    var wrapper = new VolumeGuardTranscodeManager(originalManager, NullLogger<VolumeGuardTranscodeManager>.Instance, service);
    var state = new StreamState(null!, TranscodingJobType.Hls, originalManager)
    {
        Request = new VideoRequestDto { DeviceId = "analysis-test", PlaySessionId = "analysis-test-session" },
        AudioStream = new MediaStream { Index = 0, ChannelLayout = "5.1" },
        MediaSource = new MediaSourceInfo { Path = surround }, OutputAudioCodec = "aac"
    };
    using var launchCancellation = new CancellationTokenSource();
    await wrapper.StartFfMpeg(state, "test-output", "-i input -c:a aac -af \"pan=stereo|FL=FL+0.707*FC|FR=FR+0.707*FC\" output", Guid.Empty, TranscodingJobType.Hls, launchCancellation);
    var captured = ((AnalysisTranscodeProxy)(object)originalManager).Arguments!;
    Check(captured.Contains("volume=6dB,dynaudnorm=", StringComparison.Ordinal), "Actual launch hook consumes measured gain capped at6dB.");
    Check(captured.Contains(":m=1:r=", StringComparison.Ordinal), "Actual launch hook leaves zero extra adaptive boost after full fixed gain.");
    Check(captured.IndexOf("pan=5.1", StringComparison.Ordinal) < captured.IndexOf("pan=stereo", StringComparison.Ordinal)
        && captured.IndexOf("pan=stereo", StringComparison.Ordinal) < captured.IndexOf("volume=6dB", StringComparison.Ordinal)
        && captured.IndexOf("dynaudnorm=", StringComparison.Ordinal) < captured.IndexOf("alimiter=", StringComparison.Ordinal), "Actual launch hook orders center emphasis, downmix, measured gain, dynamics, limiter.");
    Check(plugin.Configuration.Balanced.MaxGainDb == 6, "Playback budget adjustment never mutates stored configuration.");
    state.AudioStream.Index = 1;
    await wrapper.StartFfMpeg(state, "test-output", "-i input -c:a aac output", Guid.Empty, TranscodingJobType.Hls, launchCancellation);
    captured = ((AnalysisTranscodeProxy)(object)originalManager).Arguments!;
    Check(!captured.Contains("volume=6dB", StringComparison.Ordinal) && captured.Contains(":m=1.99526231:r=", StringComparison.Ordinal), "Unmeasured track falls back to configured adaptive boost budget.");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await service.AnalyzeAsync(loud, 0, canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
    Check(service.TryBeginScan() && !service.TryBeginScan(), "Overlapping scans rejected.");
    await service.AnalyzeAsync(loud, 0, default);
    service.EndScan(completed: true);
    Check(service.Snapshot().CachedTracks == 1, "Completed scan prunes unseen old cache records.");
    Console.WriteLine($"PASS: {checks} dialogue and loudness analysis checks, including real FFmpeg decode, fixed-gain LUFS convergence, persistent freshness and 5.1 dialogue downmix. Quiet gain={quietGain:0.00}dB, loud gain={loudGain:0.00}dB; corrected difference={Math.Abs(correctedQuiet.IntegratedLufs - correctedLoud.IntegratedLufs):0.00}LU.");
}
finally { Directory.Delete(directory, recursive: true); }

public class AnalysisPathProxy : DispatchProxy
{
    public string Folder { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name.StartsWith("get_", StringComparison.Ordinal) ? Folder : null;
}
public class AnalysisXmlProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "DeserializeFromFile")
        {
            using var stream = File.OpenRead((string)args![1]!);
            return new XmlSerializer((Type)args[0]!).Deserialize(stream);
        }
        return null;
    }
}
public class AnalysisTranscodeProxy : DispatchProxy
{
    public string? Arguments { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "StartFfMpeg") return null;
        Arguments = (string)args![2]!;
        return Task.FromResult<TranscodingJob>(null!);
    }
}
public class AnalysisEncoderProxy : DispatchProxy
{
    public string EncoderPath { get; set; } = "";
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.Name == "get_EncoderPath" ? EncoderPath : throw new NotSupportedException(method.Name);
}

