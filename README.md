# Volume Guard — experimental server plugin 0.3.1

Audio leveling, peak limiting and scheduled night listening for **Jellyfin Server 12.2**, designed to work with **standard Moonfin 2.6.0 on Chromecast with Google TV**. Version 0.3 processes audio on the server. No companion app is required.

The plugin builds against Jellyfin's real 12.2 packages and has automated negotiation, configuration and audio-filter checks. Installation and playback on a running Jellyfin server and physical Chromecast, including a real Live TV feed, remain unverified. Treat this release as an experimental preview.

## Install

1. In **Dashboard → Plugins → Repositories**, add a repository named **Volume Guard** with this URL:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-volume-guard/main/manifest.json
   ```

2. Open **Dashboard → Plugins → Catalog**, install **Volume Guard 0.3.1.0**, and restart Jellyfin. Existing users should update the plugin and restart.
3. As an administrator, open **Dashboard → Plugins → Volume Guard**. Turn on both **Enable Volume Guard** and **Process audio on the Jellyfin server**. Server processing starts disabled, so installation alone does not alter playback.
4. Choose **Balanced**, **Night**, or **Auto**. For Auto, set your time zone and night hours. The initial time zone is **UTC**; use **America/New_York** if appropriate.
5. Save, then start new playback in your existing Moonfin app. The Jellyfin user must have permission to transcode audio. For changes during playback, enable the optional automatic switching setting, or stop and start a new playback session.

To turn processing off before a movie or show, use the master switch or select **Off** in the plugin settings, save, then begin playback. These controls apply globally to users of this server. A Jellyfin plugin cannot add a button to stock Moonfin's player or show/movie preview screen; those controls would require a Moonfin app change.

Manual installation: stop Jellyfin, extract the [server ZIP](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases/download/v0.3.1.0/volume-guard-server-0.3.1.0.zip) into a `Volume Guard` folder in your server's configured plugins directory, and restart. [Releases and checksums](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases).

## Plugin Pages integration

With **Plugin Pages 3+** installed, Volume Guard registers a **Volume Guard** entry under Jellyfin web's **Plugin Settings** sidebar section. It opens the same listening controls, per-device settings, loudness analysis options and playback status as the dashboard page. Configuration and status still require a Jellyfin administrator account. This is a Jellyfin web integration; it does not inject a menu or button into the native Moonfin TV app.

Install Jellyfin-12.2-compatible versions of **Plugin Pages** and its **File Transformation** dependency from the [upstream repository and installation guide](https://github.com/IAmParadox27/jellyfin-plugin-pages#installation), update Volume Guard to **0.3.1.0**, then restart Jellyfin and reload Jellyfin web. No manual page registration or config.json edit is needed. The integration is tested against the Plugin Pages **3.0.2.0** release for Jellyfin **12.2**.

Plugin Pages is optional: **Dashboard → Plugins → Volume Guard** remains available without it. Playback status reports whether sidebar registration succeeded; this confirms registration, not that File Transformation successfully patched your web client. Actual deployment on a running server still needs validation.
## Listening modes and schedule

- **Off:** normal Jellyfin playback negotiation, without the server audio filters.
- **Balanced:** streaming volume leveling, capped boost, compression and sample-peak limiting.
- **Night:** a quieter target, stronger compression and a lower peak ceiling.
- **Auto:** selects Night inside your weekly night windows, and your selected daytime mode otherwise.

Schedule days identify the **start day**: Friday 22:00–07:00 includes Saturday morning. Start is inclusive; end is exclusive. Multiple windows, overnight hours and daylight-saving changes are supported.

The mode is selected when an FFmpeg conversion starts. With **Switch profiles during playback** enabled, the plugin checks every 10 seconds and asks a compatible client to reconnect when the effective audio settings change. This briefly interrupts playback. Movies resume at the reported position with the queue and selected tracks; Live TV reconnects at the live edge, losing any time-shift position. Paused playback and SyncPlay defer changes, as do stale positions and unsupported remote-control clients. Check playback status if a reconnect is unconfirmed; no repeated blind retries are sent. Without this opt-in, stop and begin a new playback session after changes; seeking alone may reuse the conversion.

Default profiles are editable in the plugin settings:

| Setting | Balanced | Night |
| --- | ---: | ---: |
| RMS target | −20 dBFS | −26 dBFS |
| Maximum boost | +6 dB | +9 dB |
| Filter sample-peak ceiling | −1 dBFS | −4 dBFS |
| Compressor attack | 10 ms | 5 ms |
| Compressor release | 500 ms | 800 ms |

## Added in 0.3

**Dialogue boost:** Set 0–12 dB of emphasis globally or per device. Recognized surround layouts boost the centre channel before Jellyfin downmixes audio; stereo, mono and unknown layouts use a speech-frequency EQ. This is speech emphasis, not voice isolation. Music and effects in the centre channel or speech band can also become louder. Compression and limiting follow the boost. Start with 3 dB.

**Loudness analysis:** Enable measured loudness, save, then run **Dashboard → Scheduled Tasks → Volume Guard loudness analysis**. The task also has a default daily 03:00 schedule in Jellyfin's scheduler (server time, separate from the plugin's night-profile time zone). It reads complete local movie/episode audio tracks one at a time using the server's FFmpeg, without changing files. Scanning costs CPU and disk I/O; cancel it through Scheduled Tasks. Changed files and tracks are remeasured; existing valid measurements are reused. Remote streams, Live TV, external audio tracks and unmeasured/silent tracks retain real-time leveling.

Measured integrated LUFS and true-peak headroom determine a fixed gain for each analyzed track. New measurements apply when a new conversion starts. That correction and real-time leveling share the profile's maximum boost budget; dialogue emphasis is a separate boost. The LUFS target guides the initial title gain; subsequent dynamics, dialogue emphasis and encoding affect the final output. This is not a guarantee of identical output LUFS. Cached measurements are stored under the server data directory in `volume-guard/loudness` and are pruned after complete scans.

**Per-device settings:** Add an override using a connected device from playback status or its Jellyfin device ID. Choose its mode, enable/disable it and set its dialogue boost. Devices without overrides inherit global values; Auto follows the global night schedule. A device override cannot enable processing while the global master or server processing switch is off. These are device settings shared by users on that device.

**Playback status:** Refresh to see connected/overridden devices, requested mode, confirmed encoder launch, dialogue handling, use of measured gain, and reconnect/deferred reasons. Confirmation requires matching playback-session and encoding-job records; it is not a live gain meter or proof of sound at the speakers. Status is administrator-only. Analysis status shows cached-track count, scan progress and errors; counts can include stale records until the next completed scan.
## How processing works

During authenticated playback negotiation, the plugin requests audio transcoding and removes direct-play choices from the response. It adds FFmpeg `dynaudnorm`, `acompressor` and `alimiter` filters when the conversion starts. Video can be copied when compatible; normal codec and subtitle requirements may still cause video transcoding. Media files are not modified, and no library scan is required.

Library playback and Live TV use the same server conversion hook. Live TV negotiation suppresses the raw source path in the response so Moonfin can use the processed URL. When no supported transcoding route is available, negotiation returns an error rather than offering an unprocessed direct-play route. This behavior still needs real-device and tuner testing.

Processing consumes server CPU and can add about a second of buffering. Encoded audio replaces the original bitstream, so original Atmos/DTS object metadata is not preserved. Channel support follows the negotiated output codec and device capabilities. Audio transcoding permission is required.

Real-time profile targets are **RMS dBFS**; optional library analysis measures integrated LUFS separately. Streaming gain adapts over time and cannot make all programs perceptually identical. The limiter controls sample peaks before encoding; lossy encoding can produce output peaks above that ceiling. It does not limit intersample true peaks or physical room volume.

Clients or external players that bypass Jellyfin playback negotiation, offline files and raw/static media URLs are outside this mechanism. Do not combine server processing with the old Volume Guard companion's DSP: that would process audio twice.

## Verification and source

Automated checks cover schedule boundaries/DST, configuration validation, per-device inheritance/master overrides, administrator status access, persistence, playback and Live TV response rewriting, DI decoration, FFmpeg command interception, dialogue/downmix order, restart queue/position contracts and filter construction. Real FFmpeg checks verify capped quiet-content boost, peak reduction, silence preservation, cached whole-track measurements/freshness, measured title-gain convergence and centre boost through a stereo downmix. The settings page is checked in a real browser with a mocked Jellyfin API at desktop and mobile widths. Physical server/client playback is still required to establish compatibility.

Build the server plugin and run its tests with .NET 10:

```powershell
./build.ps1 -ServerOnly
```

The ZIP contains `Jellyfin.Plugin.VolumeGuard.dll` at its root. Jellyfin dependencies are supplied by the server. `scripts/update-manifest.ps1` updates the catalog checksum after a build; `package-source.ps1` creates a source archive. Release assets include SHA-256 checksums; Jellyfin's catalog uses MD5.

Server source is under `server/`; see [server API and integration details](server/README.md). Historical Android TV companion source and the Moonfin patch experiment are retained for reference. Neither is required or distributed as part of the 0.3 server installation. The default build produces only the server; `-LegacyCompanion` explicitly builds the old Android preview.

Upstream sources: [Jellyfin 12.2](https://github.com/jellyfin/jellyfin/tree/v12.2), [FFmpeg audio filters](https://ffmpeg.org/ffmpeg-filters.html).
