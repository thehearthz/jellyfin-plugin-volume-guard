using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        DecorateTranscodeManager(services);
        services.Configure<MvcOptions>(options => options.Filters.Add<VolumeGuardPlaybackFilter>());
    }

    public static void DecorateTranscodeManager(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(ITranscodeManager))
            ?? throw new InvalidOperationException("Jellyfin ITranscodeManager is unavailable. Volume Guard requires Jellyfin 12.2.");
        if (descriptor.Lifetime != ServiceLifetime.Singleton)
            throw new InvalidOperationException("Unexpected Jellyfin ITranscodeManager lifetime.");
        services.Remove(descriptor);
        // Register the original separately so DI tracks and disposes its singleton exactly once.
        services.AddSingleton(provider => new OriginalTranscodeManager(
            (ITranscodeManager)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)),
            descriptor.ImplementationInstance is null));
        services.AddSingleton<ITranscodeManager>(provider => new VolumeGuardTranscodeManager(
            provider.GetRequiredService<OriginalTranscodeManager>().Value,
            provider.GetRequiredService<ILogger<VolumeGuardTranscodeManager>>()));
    }

    private sealed class OriginalTranscodeManager(ITranscodeManager value, bool owns) : IDisposable
    {
        public ITranscodeManager Value { get; } = value;
        public void Dispose() { if (owns && Value is IDisposable disposable) disposable.Dispose(); }
    }
}

public sealed class VolumeGuardPlaybackFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action
            || action.ControllerName != "MediaInfo"
            || action.ActionName is not ("GetPostedPlaybackInfo" or "OpenLiveStream")
            || context.HttpContext.User.Identity?.IsAuthenticated != true
            || !ProcessingActive())
        {
            await next().ConfigureAwait(false);
            return;
        }
        // Query values take precedence over request DTO values in Jellyfin. Overwrite bound
        // arguments, preserving codec/video choices and all authorization/user policy checks.
        context.ActionArguments["enableDirectPlay"] = (bool?)false;
        context.ActionArguments["enableDirectStream"] = (bool?)false;
        if (action.ActionName == "GetPostedPlaybackInfo") context.ActionArguments["allowAudioStreamCopy"] = (bool?)false;
        var executed = await next().ConfigureAwait(false);
        if (executed.Result is not ObjectResult result || result.StatusCode is >= 400) return;
        if (result.Value is PlaybackInfoResponse playback)
        {
            var sources = playback.MediaSources.Where(CanProcess).Select(ProcessingSource).ToArray();
            if (playback.ErrorCode is null && sources.Length == 0)
            {
                executed.Result = NoProcessingRoute();
                return;
            }
            result.Value = new PlaybackInfoResponse
            {
                PlaySessionId = playback.PlaySessionId,
                ErrorCode = playback.ErrorCode,
                MediaSources = sources
            };
        }
        else if (result.Value is LiveStreamResponse live)
        {
            if (!CanProcess(live.MediaSource)) { executed.Result = NoProcessingRoute(); return; }
            result.Value = new LiveStreamResponse(ProcessingSource(live.MediaSource));
        }
    }

    private static bool CanProcess(MediaSourceInfo source) => source.SupportsTranscoding && !string.IsNullOrWhiteSpace(source.TranscodingUrl);
    private static ObjectResult NoProcessingRoute() => new(new ProblemDetails
    {
        Status = 409, Title = "Volume Guard requires an audio transcoding route.",
        Detail = "Enable audio transcoding for this user and client, or turn off Volume Guard server processing."
    }) { StatusCode = 409 };

    public static bool ProcessingActive()
    {
        lock (Plugin.ConfigurationLock)
        {
            try { return ServerAudio.Filter(Plugin.Instance.Configuration, DateTimeOffset.UtcNow) is not null; }
            catch (ArgumentException) { return false; }
        }
    }

    public static MediaSourceInfo ProcessingSource(MediaSourceInfo source)
    {
        // MediaSourceInfo may be cached/shared across viewers. Clone before changing any
        // client-facing field; the server retains its real source path for FFmpeg.
        var copy = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.Serialize(source))!;
        copy.SupportsDirectPlay = false;
        copy.SupportsDirectStream = false;
        if (copy.SupportsTranscoding && !string.IsNullOrWhiteSpace(copy.TranscodingUrl))
        {
            var url = QueryHelpers.ParseQuery(new Uri("http://localhost" + (copy.TranscodingUrl.StartsWith('/') ? "" : "/") + copy.TranscodingUrl).Query);
            var baseUrl = copy.TranscodingUrl.Split('?', 2)[0];
            var query = url.ToDictionary(pair => pair.Key, pair => (string?)pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            query["allowAudioStreamCopy"] = "false";
            copy.TranscodingUrl = QueryHelpers.AddQueryString(baseUrl, query);
            // Moonfin 2.6 otherwise ignores SupportsDirectPlay for managed Live TV, choosing
            // the raw Path before inspecting TranscodingUrl. Suppress only the response path.
            copy.Path = null;
        }
        else
        {
            // Do not hand Moonfin an unprocessed Live TV URL when no permitted encoding
            // route exists. Jellyfin remains responsible for deciding if transcoding is allowed.
            copy.Path = null;
        }
        return copy;
    }
}

public sealed class VolumeGuardTranscodeManager(ITranscodeManager inner, ILogger<VolumeGuardTranscodeManager> logger) : ITranscodeManager
{
    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments,
        Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
    {
        string? filter;
        lock (Plugin.ConfigurationLock)
        {
            try { filter = ServerAudio.Filter(Plugin.Instance.Configuration, DateTimeOffset.UtcNow); }
            catch (ArgumentException) { filter = null; logger.LogWarning("Volume Guard configuration is invalid; server audio processing is disabled."); }
        }
        if (filter is not null && state.AudioStream is not null)
        {
            // Never silently advertise processing while passing copied audio through.
            if (string.Equals(state.OutputAudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Volume Guard cannot process copied audio. Start playback again with audio transcoding enabled.");
            commandLineArguments = ServerAudio.AppendFilter(commandLineArguments, filter);
            logger.LogInformation("Volume Guard is processing encoded audio for playback session {PlaySessionId}.", state.Request.PlaySessionId);
        }
        return inner.StartFfMpeg(state, outputPath, commandLineArguments, userId, transcodingJobType, cancellationTokenSource, workingDirectory);
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId) => inner.GetTranscodingJob(playSessionId);
    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => inner.GetTranscodingJob(path, type);
    public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => inner.PingTranscodingJob(playSessionId, isUserPaused);
    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => inner.KillTranscodingJobs(deviceId, playSessionId, deleteFiles);
    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? position, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
        => inner.ReportTranscodingProgress(job, state, position, framerate, percentComplete, bytesTranscoded, bitRate);
    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type) => inner.OnTranscodeBeginRequest(path, type);
    public void OnTranscodeEndRequest(TranscodingJob job) => inner.OnTranscodeEndRequest(job);
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken) => inner.LockAsync(outputPath, cancellationToken);
}
