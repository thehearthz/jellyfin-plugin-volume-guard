using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.VolumeGuard;

/// <summary>
/// Restarts remotely controllable playback when an administrator opts into active
/// profile switching. A delivered Play command is a request, never proof of DSP.
/// No retry is issued after an uncertain delivery: PlayNow is not idempotent.
/// </summary>
public sealed class PlaybackSwitchingService(
    ISessionManager sessions,
    ISyncPlayManager syncPlay,
    ILogger<PlaybackSwitchingService> logger) : BackgroundService
{
    private readonly PlaybackSwitchTracker tracker = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        sessions.PlaybackStart += PlaybackStarted;
        sessions.PlaybackProgress += PlaybackProgressed;
        sessions.PlaybackStopped += PlaybackStopped;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                try { await PollAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) { logger.LogWarning(error, "Volume Guard could not check active playback profiles."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            sessions.PlaybackStart -= PlaybackStarted;
            sessions.PlaybackProgress -= PlaybackProgressed;
            sessions.PlaybackStopped -= PlaybackStopped;
        }
    }

    private void PlaybackStarted(object? sender, PlaybackProgressEventArgs args)
    {
        if (args.Session is null) return;
        ProcessingRegistry.ObservePlayback(args.Session.Id, args.PlaySessionId);
        var itemId = args.MediaInfo?.Id ?? args.Session.NowPlayingItem?.Id;
        if (!itemId.HasValue) { tracker.Forget(args.Session.Id); return; }
        try
        {
            var signature = PlaybackSettings.Fingerprint(args.Session.DeviceId, DateTimeOffset.UtcNow);
            var applied = ProcessingRegistry.Get(args.Session.DeviceId);
            if (MatchingAppliedFingerprint(applied, itemId.Value, args.PlaySessionId) is { } appliedSignature) signature = appliedSignature;
            tracker.SetBaseline(args.Session.Id, itemId.Value, signature);
        }
        catch (ArgumentException) { tracker.Forget(args.Session.Id); }
    }

    private static void PlaybackProgressed(object? sender, PlaybackProgressEventArgs args)
    {
        if (args.Session is not null) ProcessingRegistry.ObservePlayback(args.Session.Id, args.PlaySessionId);
    }

    private void PlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        if (args.Session is null) return;
        ProcessingRegistry.StopPlayback(args.Session.Id, args.PlaySessionId);
        // An older stream's delayed stop must not erase the new stream's baseline.
        if (ProcessingRegistry.GetPlaybackSession(args.Session.Id) is null) tracker.Forget(args.Session.Id);
    }

    public static string? MatchingAppliedFingerprint(ProcessingRecord? record, Guid itemId, string? playSessionId)
        => record is not null && record.ItemId == itemId
            && record.State is "launched" or "bypassed"
            && !string.IsNullOrEmpty(playSessionId) && record.PlaySessionId == playSessionId
            && !string.IsNullOrEmpty(record.Fingerprint) ? record.Fingerprint : null;

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = sessions.Sessions.ToArray();
        tracker.Prune(snapshot.Select(session => session.Id));
        foreach (var session in snapshot)
        {
            if (session.NowPlayingItem is null || session.UserId == Guid.Empty) continue;
            Configuration configuration;
            string signature;
            try
            {
                configuration = PlaybackSettings.Resolve(session.DeviceId);
                signature = PlaybackSettings.Fingerprint(configuration, now);
            }
            catch (ArgumentException)
            {
                ProcessingRegistry.SetSwitch(session.DeviceId, "Invalid settings; automatic restart deferred.");
                continue;
            }

            // Always establish a baseline, even when opted out, so enabling the option
            // can apply a simultaneous profile change to already playing sessions.
            var appliedSignature = MatchingAppliedFingerprint(ProcessingRegistry.Get(session.DeviceId),
                session.NowPlayingItem.Id, ProcessingRegistry.GetPlaybackSession(session.Id));
            var changed = tracker.HasChange(session.Id, session.NowPlayingItem.Id, signature, appliedSignature);
            if (!configuration.AutomaticPlaybackSwitchingEnabled || !changed) continue;
            var deferred = RestartDeferral(session, syncPlay.IsUserActive(session.UserId), now);

            var request = CreateRestartRequest(session);
            if (request is null) deferred ??= "Profile change requires a fresh playback start: resume position or queue is unavailable.";
            if (deferred is not null)
            {
                ProcessingRegistry.SetSwitch(session.DeviceId, deferred);
                continue;
            }

            // Recheck the item immediately before dispatch; never restart a different
            // title using a snapshot of the previous title's position.
            if (session.PlayState.IsPaused || session.NowPlayingItem?.Id != request!.ItemIds[request.StartIndex ?? 0]) continue;
            // A client may report stop/start while reusing its old stream. Those
            // callbacks cannot make a non-idempotent PlayNow safe to replay.
            if (tracker.HasAlreadyRequested(session.Id, session.NowPlayingItem.Id, signature)) continue;
            tracker.MarkRequested(session.Id, session.NowPlayingItem.Id, signature);
            ProcessingRegistry.SetSwitch(session.DeviceId, "Playback reconnect requested; waiting for a new confirmed audio encoding process.");
            try
            {
                using var delivery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                delivery.CancelAfter(TimeSpan.FromSeconds(5));
                await sessions.SendPlayCommand(session.Id, session.Id, request, delivery.Token).ConfigureAwait(false);
                logger.LogInformation("Volume Guard requested a playback reconnect for device {DeviceId} after an audio profile change.", session.DeviceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                ProcessingRegistry.SetSwitch(session.DeviceId, "Playback reconnect could not be confirmed; start playback again to apply changes. No automatic retry will interrupt playback.");
                logger.LogWarning(error, "Volume Guard playback reconnect could not be confirmed for device {DeviceId}.", session.DeviceId);
            }
        }
    }

    public static string? RestartDeferral(SessionInfo session, bool syncPlayActive, DateTimeOffset now)
    {
        if (session.PlayState.IsPaused) return "Profile change deferred until playback resumes.";
        if (syncPlayActive) return "Profile change deferred during SyncPlay to preserve group playback.";
        if (!session.IsActive || !session.SupportsRemoteControl) return "Client does not advertise active remote playback control; start playback again to apply changes.";
        if (now.UtcDateTime - session.LastPlaybackCheckIn > TimeSpan.FromSeconds(90)) return "Profile change deferred: playback position is stale.";
        return null;
    }

    public static PlayRequest? CreateRestartRequest(SessionInfo session)
    {
        var item = session.NowPlayingItem;
        if (item is null) return null;
        var live = !string.IsNullOrEmpty(session.PlayState.LiveStreamId)
            || item.Type.ToString() is "TvChannel" or "LiveTvChannel" or "Program";
        if (!live && (!session.PlayState.CanSeek || session.PlayState.PositionTicks is null or < 0)) return null;
        if (session.NowPlayingQueue.Count > 1000) return null;
        var queue = session.NowPlayingQueue.ToArray();
        var index = !string.IsNullOrEmpty(session.PlaylistItemId)
            ? Array.FindIndex(queue, entry => entry.Id == item.Id && entry.PlaylistItemId == session.PlaylistItemId)
            : Array.FindIndex(queue, entry => entry.Id == item.Id);
        var itemIds = index >= 0 ? queue.Select(entry => entry.Id).ToArray() : [item.Id];
        return new PlayRequest
        {
            ItemIds = itemIds,
            StartIndex = Math.Max(0, index),
            StartPositionTicks = live ? 0 : session.PlayState.PositionTicks,
            PlayCommand = PlayCommand.PlayNow,
            ControllingUserId = session.UserId,
            AudioStreamIndex = session.PlayState.AudioStreamIndex,
            SubtitleStreamIndex = session.PlayState.SubtitleStreamIndex,
            // Managed Live TV sources can expire; reconnect the channel at its live edge.
            MediaSourceId = live ? null! : session.PlayState.MediaSourceId
        };
    }
}

/// <summary>Bounded by active sessions; failed/uncertain sends are not replayed.</summary>
public sealed class PlaybackSwitchTracker
{
    private readonly ConcurrentDictionary<string, (Guid Item, string Signature)> observed = new();
    private readonly ConcurrentDictionary<string, (Guid Item, string Signature)> requested = new();

    public bool HasChange(string sessionId, Guid itemId, string signature, string? appliedSignature = null)
    {
        if (requested.TryGetValue(sessionId, out var previousRequest)
            && (previousRequest.Item != itemId || !string.Equals(previousRequest.Signature, signature, StringComparison.Ordinal)))
            ((ICollection<KeyValuePair<string, (Guid Item, string Signature)>>)requested).Remove(new(sessionId, previousRequest));
        var baseline = observed.GetOrAdd(sessionId, (itemId, appliedSignature ?? signature));
        if (baseline.Item != itemId)
        {
            observed[sessionId] = (itemId, appliedSignature ?? signature);
            return appliedSignature is not null && !string.Equals(appliedSignature, signature, StringComparison.Ordinal);
        }
        return !string.Equals(baseline.Signature, signature, StringComparison.Ordinal);
    }

    public void SetBaseline(string sessionId, Guid itemId, string signature) => observed[sessionId] = (itemId, signature);
    public void MarkRequested(string sessionId, Guid itemId, string signature)
    {
        observed[sessionId] = (itemId, signature);
        requested[sessionId] = (itemId, signature);
    }
    public bool HasAlreadyRequested(string sessionId, Guid itemId, string signature)
        => requested.TryGetValue(sessionId, out var prior) && prior.Item == itemId
            && string.Equals(prior.Signature, signature, StringComparison.Ordinal);
    public void Forget(string sessionId) => observed.TryRemove(sessionId, out _);
    public void Prune(IEnumerable<string> activeSessionIds)
    {
        var active = activeSessionIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in observed.Keys) if (!active.Contains(id)) observed.TryRemove(id, out _);
        foreach (var id in requested.Keys) if (!active.Contains(id)) requested.TryRemove(id, out _);
    }
}
