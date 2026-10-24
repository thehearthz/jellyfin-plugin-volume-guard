param(
    [string]$Version = '0.3.0.0',
    [string]$Repository = 'thehearthz/jellyfin-plugin-volume-guard'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Version must have four numeric parts.' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository must be owner/name.' }
$root = Split-Path $PSScriptRoot -Parent
$archive = Join-Path $root "artifacts/volume-guard-server-$Version.zip"
if (!(Test-Path -LiteralPath $archive)) { throw "Build the server package first: $archive" }
$manifestPath = Join-Path $root 'manifest.json'
$previous = if (Test-Path -LiteralPath $manifestPath) { @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json) } else { @() }
$entry = [ordered]@{
    guid = 'c49aef32-6c2e-462c-a5ee-40e18c8d28af'
    name = 'Volume Guard'
    overview = 'Server leveling, dialogue boost, measured loudness and night profiles'
    description = 'Experimental audio processing for Jellyfin 12.2 and standard Moonfin. Includes leveling, peak limiting, dialogue emphasis, per-device modes, cached local-library loudness analysis and administrator playback status. Optional active switching requests a brief playback reconnect; Live TV returns to the live edge. No companion app required. Enable server processing in plugin settings. Physical client and Live TV validation remains necessary.'
    owner = $Repository.Split('/')[0]
    category = 'General'
    versions = @([ordered]@{
        version = $Version
        changelog = 'Adds dialogue emphasis, whole-track local movie/episode loudness analysis with persistent cache, per-device mode/boost overrides, confirmed encoder status, and opt-in switching during playback via a brief reconnect. Movies resume at the reported position; Live TV returns to the live edge. Run Volume Guard loudness analysis in Scheduled Tasks after enabling analysis. New features default off/inherit. Automated checks pass; actual Chromecast/server/Live TV playback remains unverified.'
        targetAbi = '12.2.0.0'
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/volume-guard-server-$Version.zip"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }) + @($previous | Where-Object guid -EQ 'c49aef32-6c2e-462c-a5ee-40e18c8d28af' | ForEach-Object versions | Where-Object version -NE $Version)
}
# Jellyfin's catalog format uses MD5. Release SHA256SUMS is supplied independently.
ConvertTo-Json -InputObject @($entry) -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Manifest updated: $manifestPath"
