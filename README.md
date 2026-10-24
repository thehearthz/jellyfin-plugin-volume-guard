# Volume Guard — experimental server plugin 0.2

Audio leveling, peak limiting and scheduled night listening for **Jellyfin Server 12.2**, designed to work with **standard Moonfin 2.6.0 on Chromecast with Google TV**. Version 0.2 processes audio on the server. No companion app is required.

The plugin builds against Jellyfin's real 12.2 packages and has automated negotiation, configuration and audio-filter checks. Installation and playback on a running Jellyfin server and physical Chromecast, including a real Live TV feed, remain unverified. Treat this release as an experimental preview.

## Install

1. In **Dashboard → Plugins → Repositories**, add a repository named **Volume Guard** with this URL:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-volume-guard/main/manifest.json
   ```

2. Open **Dashboard → Plugins → Catalog**, install **Volume Guard 0.2.0.0**, and restart Jellyfin. Existing 0.1 users should update the plugin and restart.
3. As an administrator, open **Dashboard → Plugins → Volume Guard**. Turn on both **Enable Volume Guard** and **Process audio on the Jellyfin server**. Server processing starts disabled, so installation alone does not alter playback.
4. Choose **Balanced**, **Night**, or **Auto**. For Auto, set your time zone and night hours. The initial time zone is **UTC**; use **America/New_York** if appropriate.
5. Save, then start new playback in your existing Moonfin app. The Jellyfin user must have permission to transcode audio. For changes during playback, stop playback completely and start a new playback session.

To turn processing off before a movie or show, use the master switch or select **Off** in the plugin settings, save, then begin playback. These controls apply globally to users of this server. A Jellyfin plugin cannot add a button to stock Moonfin's player or show/movie preview screen; those controls would require a Moonfin app change.

Manual installation: stop Jellyfin, extract the [server ZIP](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases/download/v0.2.0.0/volume-guard-server-0.2.0.0.zip) into a `Volume Guard` folder in your server's configured plugins directory, and restart. [Releases and checksums](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases).

## Listening modes and schedule

- **Off:** normal Jellyfin playback negotiation, without the server audio filters.
- **Balanced:** streaming volume leveling, capped boost, compression and sample-peak limiting.
- **Night:** a quieter target, stronger compression and a lower peak ceiling.
- **Auto:** selects Night inside your weekly night windows, and your selected daytime mode otherwise.

Schedule days identify the **start day**: Friday 22:00–07:00 includes Saturday morning. Start is inclusive; end is exclusive. Multiple windows, overnight hours and daylight-saving changes are supported.

The mode is selected when an FFmpeg conversion starts. An existing stream does **not** switch profiles at the schedule boundary or after a settings change. Stop and begin a new playback session to apply the current mode; seeking alone may reuse the existing conversion.

Default profiles are editable in the plugin settings:

| Setting | Balanced | Night |
| --- | ---: | ---: |
| RMS target | −20 dBFS | −26 dBFS |
| Maximum boost | +6 dB | +9 dB |
| Filter sample-peak ceiling | −1 dBFS | −4 dBFS |
| Compressor attack | 10 ms | 5 ms |
| Compressor release | 500 ms | 800 ms |

## How processing works

During authenticated playback negotiation, the plugin requests audio transcoding and removes direct-play choices from the response. It adds FFmpeg `dynaudnorm`, `acompressor` and `alimiter` filters when the conversion starts. Video can be copied when compatible; normal codec and subtitle requirements may still cause video transcoding. Media files are not modified, and no library scan is required.

Library playback and Live TV use the same server conversion hook. Live TV negotiation suppresses the raw source path in the response so Moonfin can use the processed URL. When no supported transcoding route is available, negotiation returns an error rather than offering an unprocessed direct-play route. This behavior still needs real-device and tuner testing.

Processing consumes server CPU and can add about a second of buffering. Encoded audio replaces the original bitstream, so original Atmos/DTS object metadata is not preserved. Channel support follows the negotiated output codec and device capabilities. Audio transcoding permission is required.

Targets are **RMS dBFS, not measured LUFS**. Streaming gain adapts over time and cannot make all programs perceptually identical. The limiter controls sample peaks before encoding; lossy encoding can produce output peaks above that ceiling. It does not limit intersample true peaks or physical room volume.

Clients or external players that bypass Jellyfin playback negotiation, offline files and raw/static media URLs are outside this mechanism. Do not combine server processing with the old Volume Guard companion's DSP: that would process audio twice.

## Verification and source

Automated checks cover schedule boundaries/DST, configuration validation, authenticated API access, persistence, playback and Live TV response rewriting, DI decoration, FFmpeg command interception, and filter construction. Real FFmpeg PCM checks verify capped quiet-content boost, peak reduction and silence preservation. The settings page is checked in a real browser with a mocked Jellyfin API at desktop and mobile widths. Physical server/client playback is still required to establish compatibility.

Build the server plugin and run its tests with .NET 10:

```powershell
./build.ps1 -ServerOnly
```

The ZIP contains `Jellyfin.Plugin.VolumeGuard.dll` at its root. Jellyfin dependencies are supplied by the server. `scripts/update-manifest.ps1` updates the catalog checksum after a build; `package-source.ps1` creates a source archive. Release assets include SHA-256 checksums; Jellyfin's catalog uses MD5.

Server source is under `server/`; see [server API and integration details](server/README.md). Historical Android TV companion source and the Moonfin patch experiment are retained for reference. Neither is required or distributed as part of the 0.2 server installation. The default build produces only the server; `-LegacyCompanion` explicitly builds the old Android preview.

Upstream sources: [Jellyfin 12.2](https://github.com/jellyfin/jellyfin/tree/v12.2), [FFmpeg audio filters](https://ffmpeg.org/ffmpeg-filters.html).
