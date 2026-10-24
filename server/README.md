# Volume Guard server plugin

Targets Jellyfin 12.2 / .NET 10. Version 0.2 adds opt-in audio processing during server transcoding for standard clients such as Moonfin. No companion is required. Controls are global administrator settings in Dashboard → Plugins → Volume Guard; the plugin does not add controls to a client app.

## Integration

`ServiceRegistrator` decorates Jellyfin's singleton `ITranscodeManager` through dependency injection, preserving lifecycle calls. A global MVC action filter handles authenticated `MediaInfo.GetPostedPlaybackInfo` and `OpenLiveStream` calls when server processing is active. It overrides bound direct-play/direct-stream/audio-copy options and returns cloned media-source DTOs with only supported encoding routes. The server's cached source paths remain intact; the client-facing path is suppressed to avoid Moonfin 2.6's raw Live TV URL preference. Missing encoding routes return HTTP 409. User authorization and transcoding permissions remain Jellyfin's responsibility.

At `StartFfMpeg`, the decorator selects the current profile and appends validated, invariant numeric `dynaudnorm`, `acompressor` and `alimiter` filters. Audio copy requests are rejected while active. Normal video encoding/copy decisions remain Jellyfin's. Configuration changes and schedules apply when a new conversion starts; existing sessions may reuse a conversion. Stop and start new playback after changing modes.

This relies on Jellyfin 12.2 registration order and MVC action/model contracts, so test against each future server release. Raw/static URLs and clients that bypass playback negotiation are outside this mechanism. Installation in a real server, physical Chromecast playback and actual Live TV tuner behavior remain unverified.

## API contract v1

All routes use Jellyfin authentication. Configuration routes additionally require the `RequiresElevation` administrator policy. No route accepts user/device identity as authorization.

- `GET /VolumeGuard/policy` returns schemaVersion, serverTimeUtc, enabled, defaultMode, effectiveAutoMode, scheduleActive and configuration.
- `GET /VolumeGuard/configuration` returns the configuration object.
- `POST /VolumeGuard/configuration` validates and replaces the object through Jellyfin's XML plugin store, returning HTTP 204. The request limit is 32 KiB. Invalid input returns HTTP 400 ProblemDetails; invalid persisted policy returns HTTP 503.

Default configuration:

```json
{
  "enabled": true,
  "serverProcessingEnabled": false,
  "defaultMode": "Auto",
  "daytimeMode": "Balanced",
  "timeZoneId": "UTC",
  "scheduleEnabled": true,
  "nightWindows": [{"days":[0,1,2,3,4,5,6],"start":"22:00","end":"07:00"}],
  "balanced": {"targetRmsDb":-20,"maxGainDb":6,"limiterCeilingDb":-1,"attackMs":10,"releaseMs":500},
  "night": {"targetRmsDb":-26,"maxGainDb":9,"limiterCeilingDb":-4,"attackMs":5,"releaseMs":800}
}
```

`serverProcessingEnabled=true` opts into the server route. `enabled=false` or `defaultMode=Off` bypasses processing. Modes are Off, Balanced, Night and Auto; daytimeMode accepts Off, Balanced and Night. Auto evaluates the weekly schedule in the configured server time zone. No per-user/device override or playback history is stored. The policy endpoint remains available to historical companion builds; combining their DSP with server DSP processes audio twice.

Schedule days are Sunday=0 through Saturday=6 and identify the window's start day. Monday 22:00–07:00 includes Tuesday morning. Start is inclusive; end is exclusive. Equal endpoints are rejected. Up to 32 windows can overlap; any match activates Night. Each needs one to seven unique days and exact HH:mm times. Repeated DST hours match twice; nonexistent spring-forward times never match. The initial UTC time zone should be changed as appropriate.

Profile bounds: targetRmsDb −40..−6, maxGainDb 0..24, limiterCeilingDb −20..0, attackMs 1..200, releaseMs 20..5000. Values must be finite; target cannot exceed ceiling. These are signal measurements, not calibrated room-volume limits. The final encoder can overshoot the filter's sample-peak ceiling.

## Build and checks

From the repository root, with .NET 10:

```powershell
./build.ps1 -ServerOnly
```

Policy/DSP tests link the actual policy/filter sources. HTTP tests host actual plugin controllers, authentication and DI decoration with a fixture MediaInfo controller; they do not host the complete Jellyfin server. To additionally execute real FFmpeg PCM tests, pass a compatible FFmpeg executable to the compiled test DLL:

```powershell
dotnet server/VolumeGuard.Tests/bin/Release/net10.0/VolumeGuard.Tests.dll /path/to/ffmpeg
```

Output: `artifacts/volume-guard-server-0.2.0.0.zip`, containing only `Jellyfin.Plugin.VolumeGuard.dll` at its root. The settings HTML is embedded in the DLL. The build disables online NuGet vulnerability retrieval for reproducibility; dependency auditing is separate.
