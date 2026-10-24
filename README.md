# Volume Guard — experimental preview 0.1

Volume leveling and automatic night listening for **Jellyfin Server 12.2** and a **Chromecast with Google TV** using its remote. This project contains a server plugin and a separate Android TV companion app based on Jellyfin Android TV v0.19.10 (commit `984181a`). Both are built; testing on a physical Chromecast is still required.

## Install

1. In **Dashboard → Plugins → Repositories**, add a repository named **Volume Guard** with this URL:

   ```text
   https://raw.githubusercontent.com/thehearthz/jellyfin-plugin-volume-guard/main/manifest.json
   ```

2. Open **Dashboard → Plugins → Catalog**, select **Volume Guard**, install version **0.1.0.0**, and restart Jellyfin.
3. As an administrator, open **Dashboard → Plugins → Volume Guard**. Set the time zone to **America/New_York** if appropriate, choose your night hours, and save. The initial server time zone is **UTC**; change it before relying on the schedule.
4. Download the [Chromecast companion APK](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases/download/v0.1.0.0/volume-guard-tv-0.1.0-debug.apk) and transfer it to your Chromecast using your preferred file-transfer or sideloading tool. Allow that tool to install unknown apps when Google TV requests it, then install the APK.
5. Open **Volume Guard TV (Preview)** and sign in to your Jellyfin server. It installs alongside the regular Jellyfin app with package ID `org.jellyfin.androidtv.volumeguard`; use the preview app for processed playback.
6. Play a movie, show, or Live TV channel with the built-in player. Show the player controls with the remote and select the **Volume Guard** audio icon in the secondary controls. Choose **Off**, **Balanced**, **Night**, or **Auto**.

The stock Jellyfin app does not gain a button from the server plugin. The APK is an experimental, locally debug-signed build, not an official Jellyfin release. The Jellyfin catalog installs only the server DLL; install the companion APK separately. No server or device was modified during development.

Manual server installation is also available: stop Jellyfin, extract the [server plugin ZIP](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases/download/v0.1.0.0/volume-guard-server-0.1.0.0.zip) into a `Volume Guard` folder inside the server's configured plugins directory, and restart Jellyfin. [Releases and checksums](https://github.com/thehearthz/jellyfin-plugin-volume-guard/releases).

## Listening controls

- **Off:** bypass gain processing. The companion still decodes audio to PCM so switching modes does not restart playback.
- **Balanced:** gently brings quiet and loud passages toward a target level, with capped gain and a final linked sample-peak limiter.
- **Night:** a quieter target and lower peak ceiling to reduce sudden loud passages.
- **Auto:** selects Night inside configured night windows and the configured daytime mode otherwise.
- **Settings…:** shows the selected/effective mode and policy connection status. You can optionally use a daily night schedule local to this TV instead of the server's weekly schedule.

Player selection is remembered separately for each server and Jellyfin user on the device. Selecting a manual mode overrides Auto until you select Auto again. Changing the server's default mode applies to devices without a saved selection. The server's master Enable setting overrides all client modes after the next policy refresh.

Weekly schedule days identify the **start day**: Friday 22:00–07:00 includes Saturday morning. Start is inclusive; end is exclusive. Overnight windows, multiple windows, and daylight-saving changes are supported. Auto evaluates every 15 seconds during playback; server policy refreshes every 60 seconds. If an Android-unrecognized server time-zone name is used, the client follows the server's evaluated mode on each policy refresh. Prefer an IANA name such as `America/New_York`.

If the server plugin is unavailable, the companion retains saved policy or uses local defaults: Balanced during the day and Night at 22:00–07:00 in the TV's time zone. The settings dialog reports the connection failure. A local daily schedule uses the TV's clock and time zone; keep those accurate. Offline clients cannot receive new server settings until reconnection.

## Audio behavior and limits

Audio processing runs on decoded PCM in the built-in TV player. It uses streaming RMS measurement, gradual linked-channel gain, a silence gate that stops gain chasing during quiet gaps, capped boost, and sample-peak protection. It does not rewrite media files, scan the entire library, or require a custom Jellyfin server binary. Movies, episodes and Live TV share this processing path, including library items that Direct Play to the companion.

**Audio passthrough and offload are disabled throughout the companion, including Off mode.** Atmos/DTS bitstream passthrough and its object metadata are therefore unavailable. Surround PCM support depends on the Chromecast, display and receiver. Video follows the normal Jellyfin playback negotiation; the audio processor itself does not force video transcoding. Unsupported codecs may still need normal server transcoding.

The DSP works on 16-bit PCM. Non-16-bit decoded audio uses the player's normal conversion before the custom processor; float output is disabled. External players, casting, the stock TV app, other apps and the newer experimental playback backend are outside this integration. Use the built-in player in the preview.

Targets are **RMS dBFS, not measured LUFS**. Streaming leveling takes time to adapt, so it cannot make all content perceptually identical. The limiter caps digital sample peaks, not intersample true peaks or physical room volume. Volume at the TV/receiver still matters. No separate dialogue enhancement or whole-file loudness analysis is included in this preview.

Default profiles:

| Setting | Balanced | Night |
| --- | ---: | ---: |
| RMS target | −20 dBFS | −26 dBFS |
| Maximum boost | +6 dB | +9 dB |
| Sample-peak ceiling | −1 dBFS | −4 dBFS |
| Gain-reduction attack | 10 ms | 5 ms |
| Gain-increase release | 500 ms | 800 ms |

## Verification

- Server plugin compiles against the real Jellyfin 12.2 packages on .NET 10.
- 43 policy/validation/JSON checks cover timezone offsets, overnight windows, selected start days, DST, malformed times, and profile bounds.
- 17 HTTP checks use the actual compiled controller, a local test host and a test authentication handler: anonymous/regular-user/admin access, rejected writes, configuration persistence/reload, and invalid external configuration.
- JVM audio checks cover 8/48/192 kHz, stereo/six channels, first-frame and sudden-peak limiting, gain caps, linked channel balance, silence, nonfinite input and bypass. JVM schedule checks cover day attribution, end boundaries, week wrap and DST.
- A real local browser checks the embedded settings page using a mocked Jellyfin API, including save responses, reverse-proxy paths, failed authentication, unsaved edits, validation, and widths 1440/768/390.
- Android debug APK compilation and signature verification are recorded with the release artifacts.

**Not verified:** installation in a running Jellyfin server, audio routing/audibility on a physical Chromecast, remote focus behavior on the TV, or playback of a real Live TV feed. Before routine use, compare Off/Balanced/Night on familiar content at a low TV volume, test a channel change and seek, and confirm Auto changes at your configured boundary. These device checks remain necessary despite the successful automated checks.

## Build and source

`build.ps1` builds the DLL and debug APK and runs server and JVM tests. It uses .NET 10, Java 21, Android SDK 36, and the upstream Gradle wrapper. `-ServerOnly` builds just the plugin. The task-local `.build-tools` directory is a development convenience and is excluded from source distribution; on another machine configure `JAVA_HOME` and `ANDROID_HOME`. Dependency downloads require network access.

The server endpoints are authenticated `GET /VolumeGuard/policy` and administrator-only `GET/POST /VolumeGuard/configuration`. API paths are appended to the configured server address, preserving a reverse-proxy base path. Configuration is stored by Jellyfin's plugin XML persistence. No scheduled server worker, audio scan, background transcoder or device tracking is added.

The Android source retains upstream licensing and notices. Android TV fork changes are in `app/.../volumeguard`, the Volume Guard player action/icon, playback glue, renderer setup and preview app identity. Server source is under `server/`. SHA-256 hashes are in `artifacts/SHA256SUMS`.

Upstream references: [Jellyfin server 12.2](https://github.com/jellyfin/jellyfin/releases/tag/v12.2), [Jellyfin Android TV v0.19.10](https://github.com/jellyfin/jellyfin-androidtv/tree/v0.19.10), [Media3 AudioSink](https://github.com/androidx/media/blob/1.8.0/libraries/exoplayer/src/main/java/androidx/media3/exoplayer/audio/DefaultAudioSink.java).
