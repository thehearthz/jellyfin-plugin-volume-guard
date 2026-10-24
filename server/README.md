# Volume Guard server plugin

Targets Jellyfin 12.2.0 / .NET 10. This plugin stores profiles and publishes a policy to the companion Android TV player. It does not intercept streams or change audio. Stock Jellyfin clients will not apply this policy.

## API contract v1

All routes use Jellyfin authentication. Supply the authenticated session through Jellyfin's normal authorization mechanism. Configuration routes additionally require Jellyfin's `RequiresElevation` administrator policy. No route accepts a user ID or device ID for authorization.

`GET /VolumeGuard/policy` returns:

```json
{
  "schemaVersion": 1,
  "serverTimeUtc": "2026-10-23T03:00:00+00:00",
  "enabled": true,
  "defaultMode": "Auto",
  "effectiveAutoMode": "Night",
  "scheduleActive": true,
  "configuration": {
    "enabled": true,
    "defaultMode": "Auto",
    "daytimeMode": "Balanced",
    "timeZoneId": "UTC",
    "scheduleEnabled": true,
    "nightWindows": [{"days":[0,1,2,3,4,5,6],"start":"22:00","end":"07:00"}],
    "balanced": {"targetRmsDb":-20,"maxGainDb":6,"limiterCeilingDb":-1,"attackMs":10,"releaseMs":500},
    "night": {"targetRmsDb":-26,"maxGainDb":9,"limiterCeilingDb":-4,"attackMs":5,"releaseMs":800}
  }
}
```

`GET /VolumeGuard/configuration` returns the configuration object above. `POST /VolumeGuard/configuration` replaces that object, validates and persists it using Jellyfin's plugin configuration store, returning HTTP 204. The body limit is 32 KiB. Invalid fields return HTTP 400 with ProblemDetails; invalid persisted configuration yields HTTP 503 from the policy endpoint so a client can fall back safely.

`defaultMode` accepts Off, Balanced, Night and Auto. `daytimeMode` accepts Off, Balanced and Night. An explicitly selected local client mode takes precedence over the default; `enabled=false` must bypass DSP regardless of local selection. Auto uses `effectiveAutoMode`. The client retains its device selection and temporary override; no server user/device override state is stored. Configuration contains no user identities, playback history or credentials.

Schedule days are Sunday=0 through Saturday=6 and identify the day on which a window starts. A Monday 22:00–07:00 window includes Tuesday morning. Start is inclusive and end exclusive. Empty schedules are allowed; equal endpoints are rejected. Up to 32 windows can overlap (any match activates Night). Each window requires one to seven unique days. Times must be exact 24-hour HH:mm. Repeated DST hours match twice; nonexistent spring-forward times never match. The timezone is explicit and must exist on the server; the default is UTC, which administrators should change as appropriate. Android clients should support the selected identifier or use refreshed server evaluation when unable to resolve it.

Profile limits: targetRmsDb -40..-6, maxGainDb 0..24, limiterCeilingDb -20..0, attackMs 1..200, releaseMs 20..5000. All numeric values must be finite; the RMS target cannot exceed the limiter ceiling. These describe signal levels and do not impose an absolute acoustic room loudness limit. A sample limiter is not a calibrated inter-sample true-peak limiter.

## Build and verification

Run from the workspace root in PowerShell (the SDK and cached packages already exist):

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'library-tv/.build-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'library-tv/.build-home/.nuget/packages'
& ./.dotnet/dotnet.exe build volume-guard/server/VolumeGuard/VolumeGuard.csproj -c Release --ignore-failed-sources -p:NuGetAudit=false
& ./.dotnet/dotnet.exe run --project volume-guard/server/VolumeGuard.Tests -c Release --ignore-failed-sources
```

NuGetAudit is disabled only for the offline build invocation; perform an online dependency audit before deployment. The tests link the actual policy/configuration sources and use a minimal Jellyfin base configuration stub, avoiding runtime server dependencies. They cover midnight/day attribution, weekly rollover, exclusive endpoints, schedule disable, DST transitions, half-hour offsets, invalid input and JSON field names. The DLL build verifies real Jellyfin package compatibility. Server endpoint authentication and plugin loading still require integration verification on a running Jellyfin 12.2 server.

Output: `VolumeGuard/bin/Release/net10.0/Jellyfin.Plugin.VolumeGuard.dll`.
