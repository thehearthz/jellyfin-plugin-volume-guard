using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed record AnalysisStatus(int CachedTracks, bool Running, string? LastError, DateTimeOffset? LastScanUtc, double Progress);
public sealed record AnalysisCacheEntry(int Schema, long Length, long LastWriteTicks, int AudioStreamIndex, AnalysisMeasurement Measurement);

/// <summary>One local decode at a time, cancellation kills the child process, cache files are atomic.</summary>
public sealed class AnalysisService
{
    private readonly string _directory;
    private readonly Func<string> _encoderPath;
    private readonly ILogger<AnalysisService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, AnalysisCacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _decode = new(1, 1);
    private bool _running;
    private string? _lastError;
    private DateTimeOffset? _lastScanUtc;
    private double _progress;
    private HashSet<string>? _seenKeys;

    public AnalysisService(IApplicationPaths paths, IMediaEncoder encoder, ILogger<AnalysisService> logger)
        : this(Path.Combine(paths.DataPath, "volume-guard", "loudness"), () => encoder.EncoderPath, logger) { }

    // Explicit directory/encoder overload also permits real process integration tests.
    public AnalysisService(string directory, string encoder, ILogger<AnalysisService> logger)
        : this(directory, () => encoder, logger) { }

    private AnalysisService(string directory, Func<string> encoderPath, ILogger<AnalysisService> logger)
    {
        _directory = directory;
        // Jellyfin discovers scheduled tasks before SetFFmpegPath initializes the encoder.
        // Resolve the configured executable at each decode, never during plugin startup.
        _encoderPath = encoderPath;
        _logger = logger;
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length > 4096) continue;
                    var entry = JsonSerializer.Deserialize<AnalysisCacheEntry>(File.ReadAllText(file));
                    if (entry is { Schema: 1 } && Valid(entry.Measurement)) _entries[Path.GetFileNameWithoutExtension(file)] = entry;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _lastError = "Analysis cache could not be loaded. Check the server data directory permissions.";
            _logger.LogWarning(e, "Volume Guard could not load loudness cache");
        }
    }

    private static bool Valid(AnalysisMeasurement? m) => m is not null && double.IsFinite(m.IntegratedLufs) && m.IntegratedLufs is >= -100 and <= 10
        && double.IsFinite(m.TruePeakDb) && m.TruePeakDb is >= -150 and <= 30 && double.IsFinite(m.LoudnessRange) && m.LoudnessRange is >= 0 and <= 100;

    private static (string Key, long Length, long Ticks)? Fingerprint(string? path, int index)
    {
        if (string.IsNullOrWhiteSpace(path) || index < 0 || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0) return null;
            var canonical = file.FullName;
            if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "\n" + index)));
            return (key, file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public AnalysisStatus Snapshot() { lock (_sync) return new(_entries.Count, _running, _lastError, _lastScanUtc, _progress); }
    public bool TryBeginScan()
    {
        lock (_sync)
        {
            if (_running) return false;
            _running = true; _lastError = null; _progress = 0; _seenKeys = new(StringComparer.Ordinal);
            return true;
        }
    }
    public void ReportProgress(double progress) { lock (_sync) _progress = Math.Clamp(progress, 0, 100); }
    public void EndScan(bool completed = false)
    {
        string[] obsolete;
        lock (_sync)
        {
            obsolete = completed && _seenKeys is not null ? _entries.Keys.Where(k => !_seenKeys.Contains(k)).ToArray() : [];
            foreach (var key in obsolete) _entries.Remove(key);
            _seenKeys = null; _running = false; _lastScanUtc = DateTimeOffset.UtcNow;
        }
        foreach (var key in obsolete)
        {
            try { File.Delete(Path.Combine(_directory, key + ".json")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _logger.LogDebug(e, "Volume Guard could not prune obsolete analysis cache"); }
        }
    }
    public void ReportFailure(string message) { lock (_sync) _lastError = message; }

    public bool IsFresh(string? path, int index)
    {
        var fingerprint = Fingerprint(path, index);
        if (fingerprint is not { } f) return false;
        lock (_sync) return _entries.TryGetValue(f.Key, out var entry) && entry.Length == f.Length && entry.LastWriteTicks == f.Ticks && entry.AudioStreamIndex == index;
    }

    public double? TryGetGain(string? path, int audioStreamIndex, double targetLufs, double maxGainDb, double ceilingDb)
    {
        var fingerprint = Fingerprint(path, audioStreamIndex);
        if (fingerprint is not { } f) return null;
        lock (_sync)
            return _entries.TryGetValue(f.Key, out var entry) && entry.Length == f.Length && entry.LastWriteTicks == f.Ticks && entry.AudioStreamIndex == audioStreamIndex
                ? AnalysisAudio.Gain(entry.Measurement, targetLufs, maxGainDb, ceilingDb) : null;
    }

    public async Task<bool> AnalyzeAsync(string path, int audioStreamIndex, CancellationToken cancellationToken)
    {
        await _decode.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = Fingerprint(path, audioStreamIndex);
            if (before is not { } fingerprint) return false;
            lock (_sync) _seenKeys?.Add(fingerprint.Key);
            if (IsFresh(path, audioStreamIndex)) return true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromHours(6));
            var encoderPath = _encoderPath();
            if (string.IsNullOrWhiteSpace(encoderPath))
            {
                ReportFailure("FFmpeg has not been configured by Jellyfin yet. Retry analysis after server startup.");
                return false;
            }
            var start = new ProcessStartInfo(encoderPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "info", "-filter_threads", "1", "-threads", "1", "-i", path,
                "-map", "0:" + audioStreamIndex, "-vn", "-sn", "-dn", "-af", "loudnorm=I=-23:TP=-1:LRA=11:print_format=json", "-f", "null", "-" }) start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            process.Start();
            using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            // FFmpeg writes its summary to stderr. Keep only the bounded tail, drain stdout
            // too, and never block on a full child pipe for a long-running title.
            var stdout = DrainAsync(process.StandardOutput, timeout.Token);
            var stderr = TailAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await stdout.ConfigureAwait(false);
                var text = await stderr.ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                var measurement = process.ExitCode == 0 ? AnalysisAudio.Parse(text) : null;
                if (measurement is null)
                {
                    ReportFailure("An audio track could not be measured (unsupported, silent, or FFmpeg failure). See server logs.");
                    _logger.LogWarning("Volume Guard loudness measurement unavailable for audio index {AudioStreamIndex}; FFmpeg exit code {ExitCode}", audioStreamIndex, process.ExitCode);
                    return false;
                }
                var after = Fingerprint(path, audioStreamIndex);
                if (after != before) { ReportFailure("A source changed during analysis; it will be retried on the next scan."); return false; }
                var entry = new AnalysisCacheEntry(1, fingerprint.Length, fingerprint.Ticks, audioStreamIndex, measurement);
                Directory.CreateDirectory(_directory);
                var finalPath = Path.Combine(_directory, fingerprint.Key + ".json");
                var temporaryPath = finalPath + ".tmp";
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, finalPath, overwrite: true);
                lock (_sync) _entries[fingerprint.Key] = entry;
                return true;
            }
            finally
            {
                // Observe pipe task failures after cancellation without replacing the original exception.
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ReportFailure("An audio track exceeded the six-hour analysis time limit.");
            _logger.LogWarning("Volume Guard loudness analysis exceeded its time budget for audio index {AudioStreamIndex}", audioStreamIndex);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ReportFailure("Audio analysis failed. Check FFmpeg and the server data directory permissions.");
            _logger.LogWarning(e, "Volume Guard audio analysis failed for stream index {AudioStreamIndex}", audioStreamIndex);
            return false;
        }
        finally { _decode.Release(); }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) > 0) { }
    }
    private static async Task<string> TailAsync(StreamReader reader, CancellationToken token)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 32768) tail.Remove(0, tail.Length - 32768);
        }
        return tail.ToString();
    }
}
