# Moonfin 2.6.0 + Volume Guard preview

**Historical app experiment:** Version 0.2 provides server processing for standard Moonfin without this modified app. Follow the [main installation instructions](../../README.md). Do not combine this app's DSP with server processing.

This integration keeps Moonfin's interface and adds Volume Guard to the Android TV Media3 player. It targets **Moonfin 2.6.0**, exact upstream commit `b86bbe6021eff5befdf311134bf3e831c032f21f`. It is a modified preview, not an official Moonfin release; the unmodified app does not call the Volume Guard policy API.

The existing Volume Guard Jellyfin 12.2 plugin supplies profiles and schedules without a server-plugin upgrade. A Volume Guard button in both the movie/show controls and Live TV controls opens Off, Balanced, Night, and Auto. Modes are remembered per server, user, and TV. Auto checks the schedule every 15 seconds; policy settings refresh every 60 seconds. Offline playback uses the last valid policy or a 22:00–07:00 TV-local default.

## Compatibility and install identity

The preview uses package ID `org.moonfin.androidtv.volumeguard` and display name **Moonfin + Volume Guard**, so it installs beside Moonfin instead of overwriting it. Sign in separately after installation. Use Jellyfin, the Android TV flavor, and the built-in Media3 player. Desktop, mpv, Apple, web, Emby policy integration, external players, and casting are outside this preview.

Audio is decoded to PCM in every mode, including Off, for immediate mode changes. Bitstream passthrough, IEC packing, offload, tunneling and float output are disabled; Atmos/DTS bitstream metadata is unavailable. Moonfin's separate normalization/user gain effects are disabled so nothing boosts the signal after the limiter. Normal playback volume control remains available. Use the original Moonfin app when you want its original audio behavior.

This is streaming RMS leveling and sample-peak limiting, not LUFS measurement, true-peak limiting or an absolute room-volume cap. Physical Chromecast playback, remote focus and real Live TV must still be checked on the device.

## Reproduce the preview

Install Java 17, Flutter **3.47.2**, Android SDK 36, NDK `28.2.13676358`, and CMake `3.22.1`. On Linux/macOS:

```sh
git clone https://github.com/Moonfin-Client/Moonfin-Core.git moonfin
git -C moonfin checkout b86bbe6021eff5befdf311134bf3e831c032f21f
git -C moonfin apply /absolute/path/to/volume-guard.patch
cd moonfin
export MOONFIN_TEST_ID_SUFFIX=.volumeguard
flutter pub get
flutter test test/playback/volume_guard_controller_test.dart --no-pub
flutter build apk --debug --flavor androidTv --no-pub \
  --dart-define=MOONFIN_FORCE_TV=true \
  --dart-define=DISTRIBUTION_CHANNEL=volume_guard_preview
cd android
./gradlew :moonfin_native_video:testDebugUnitTest
```

The APK is `build/app/outputs/flutter-apk/app-androidtv-debug.apk`. A debug build is intentionally separate from the official release signing identity. Follow upstream platform setup for any other native dependency requirements.

The repository's **Build Moonfin Volume Guard preview** GitHub Actions workflow runs the same pinned build and uploads the APK with SHA-256 checksums after its tests pass. The patch retains upstream GPL licensing and reuses the Volume Guard DSP included in this repository.
