using Jellyfin.Plugin.VolumeGuard;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;

var count = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    count++;
}

var tracker = new PlaybackSwitchTracker();
var item = Guid.NewGuid();
Check(!tracker.HasChange("s", item, "balanced"), "First observation establishes a baseline without interruption.");
Check(!tracker.HasChange("s", item, "balanced"), "Unchanged profile has no command.");
Check(tracker.HasChange("s", item, "night"), "A scheduled profile change is detected.");
Check(tracker.HasChange("s", item, "night"), "Deferred paused playback retains pending change.");
tracker.MarkRequested("s", item, "night");
Check(!tracker.HasChange("s", item, "night"), "Delivered or uncertain command is never blindly replayed.");
Check(tracker.HasChange("s", item, "off"), "Turning processing off also reconnects.");
Check(!tracker.HasChange("s", Guid.NewGuid(), "off"), "New title gets its own baseline.");
tracker.Forget("s");
Check(!tracker.HasChange("s", item, "balanced"), "New playback start resets old command state.");
tracker.Prune([]);
Check(!tracker.HasChange("s", item, "off"), "Ended sessions are pruned.");
Check(!tracker.HasChange("other", item, "night"), "Device sessions remain isolated.");
Check(tracker.HasChange("s", item, "night"), "One session cannot change another session's baseline.");
Check(tracker.HasChange("boundary", item, "night", "balanced"), "First poll after a boundary compares the confirmed encoder profile.");
tracker.MarkRequested("boundary", item, "night");
Check(!tracker.HasChange("boundary", item, "night", "balanced"), "An old applied record cannot replay an already requested transition.");
tracker.Forget("boundary");
tracker.SetBaseline("boundary", item, "balanced");
Check(tracker.HasAlreadyRequested("boundary", item, "night"), "Stop/start callbacks cannot replay a non-idempotent command against an unchanged old encoder.");
Check(!tracker.HasAlreadyRequested("boundary", item, "off"), "A subsequent intentional profile change remains eligible.");
tracker.HasChange("boundary", item, "balanced");
Check(!tracker.HasAlreadyRequested("boundary", item, "night"), "A different desired profile clears deduplication for the next night's transition.");
tracker.Prune(["s", "other"]);
Check(!tracker.HasAlreadyRequested("boundary", item, "night"), "Leaving the server clears per-session command deduplication.");
var applied = new ProcessingRecord("device", "play-current", "Balanced", "Off", "Off", "balanced", DateTimeOffset.UtcNow, "launched", "", item);
Check(PlaybackSwitchingService.MatchingAppliedFingerprint(applied, item, "play-current") == "balanced", "Applied baseline requires the current playback token.");
Check(PlaybackSwitchingService.MatchingAppliedFingerprint(applied, item, "play-old") is null, "A same-title record from another playback cannot seed a baseline.");
Check(PlaybackSwitchingService.MatchingAppliedFingerprint(applied, item, null) is null, "Missing playback tokens do not imply confirmation.");
Check(PlaybackSwitchingService.MatchingAppliedFingerprint(applied with { State = "starting" }, item, "play-current") is null, "An unconfirmed encoder launch cannot seed an applied baseline.");

var session = new SessionInfo(null!, NullLogger<SessionInfo>.Instance)
{
    Id = "session", UserId = Guid.NewGuid(),
    NowPlayingItem = new BaseItemDto { Id = item, Type = BaseItemKind.Movie },
    PlayState = new PlayerStateInfo
    {
        PositionTicks = 123456789, CanSeek = true, AudioStreamIndex = 2,
        SubtitleStreamIndex = -1, MediaSourceId = "version-two"
    }
};
var request = PlaybackSwitchingService.CreateRestartRequest(session)!;
session.PlayState.IsPaused = true;
Check(PlaybackSwitchingService.RestartDeferral(session, false, DateTimeOffset.UtcNow)!.Contains("resumes"), "Paused playback is never forcibly unpaused.");
session.PlayState.IsPaused = false;
Check(PlaybackSwitchingService.RestartDeferral(session, true, DateTimeOffset.UtcNow)!.Contains("SyncPlay"), "Group playback defers independent restarts.");
Check(PlaybackSwitchingService.RestartDeferral(session, false, DateTimeOffset.UtcNow)!.Contains("remote"), "Clients without remote playback support cannot be restarted.");
Check(request.PlayCommand == PlayCommand.PlayNow && request.ItemIds.SequenceEqual([item]), "Native PlayNow targets the current item.");
Check(request.StartPositionTicks == 123456789, "Resume position is preserved in server ticks.");
Check(request.AudioStreamIndex == 2 && request.SubtitleStreamIndex == -1 && request.MediaSourceId == "version-two", "Audio/subtitle/version selections survive reconnect.");
Check(request.ControllingUserId == session.UserId, "The same authenticated viewing user remains the playback owner.");
var previous = Guid.NewGuid();
var next = Guid.NewGuid();
session.NowPlayingQueue = [new() { Id = previous }, new() { Id = item, PlaylistItemId = "first" }, new() { Id = item, PlaylistItemId = "second" }, new() { Id = next }];
session.PlaylistItemId = "second";
request = PlaybackSwitchingService.CreateRestartRequest(session)!;
Check(request.ItemIds.SequenceEqual([previous, item, item, next]) && request.StartIndex == 2, "Queue order and repeated-item occurrence survive.");
session.PlayState.CanSeek = false;
Check(PlaybackSwitchingService.CreateRestartRequest(session) is null, "Unseekable files do not silently restart at zero.");
session.NowPlayingItem.Type = BaseItemKind.TvChannel;
request = PlaybackSwitchingService.CreateRestartRequest(session)!;
Check(request.StartPositionTicks == 0 && request.MediaSourceId is null, "Live TV reconnects to a new managed live source at its live edge.");
session.NowPlayingItem.Type = BaseItemKind.Movie;
session.PlayState.LiveStreamId = "managed-live";
request = PlaybackSwitchingService.CreateRestartRequest(session)!;
Check(request.StartPositionTicks == 0 && request.MediaSourceId is null, "Managed Live TV is recognized even when item kind is not a channel.");
session.PlayState.LiveStreamId = null!;
session.PlayState.CanSeek = true;
session.PlayState.PositionTicks = null;
Check(PlaybackSwitchingService.CreateRestartRequest(session) is null, "Missing resume positions defer rather than lose movie progress.");
session.NowPlayingItem = null!;
Check(PlaybackSwitchingService.CreateRestartRequest(session) is null, "Stopped playback is never restarted.");

Console.WriteLine($"PASS: {count} active switching checks (baseline, deferred changes, no duplicate replay, queue, resume, streams, live TV).");
