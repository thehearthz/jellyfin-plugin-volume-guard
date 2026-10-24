param(
    [string]$Version = '0.2.0.0',
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
    overview = 'Server audio leveling, peak limiting and automatic night profiles'
    description = 'Experimental server audio processing for Jellyfin 12.2 and standard clients such as Moonfin. Enable server processing in the plugin settings; audio is converted while video can be copied where compatible. Includes a manual switch, Balanced/Night modes and weekly schedules. Changes apply to new playback. No companion app is required for the server route; Direct Play outside playback negotiation bypasses processing. Physical client and Live TV validation remains necessary.'
    owner = $Repository.Split('/')[0]
    category = 'General'
    versions = @([ordered]@{
        version = $Version
        changelog = 'Adds opt-in server audio processing for standard Moonfin: requests audio conversion at playback negotiation, applies leveling and peak limiting in FFmpeg, and keeps video copying available where compatible. Manual on/off, Balanced/Night/Auto and weekly schedules are in Jellyfin plugin settings. Restart playback after changes. No companion app or Moonfin preview-screen/player button is required. Validate on a real server and Chromecast before relying on it.'
        targetAbi = '12.2.0.0'
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/volume-guard-server-$Version.zip"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }) + @($previous | Where-Object guid -EQ 'c49aef32-6c2e-462c-a5ee-40e18c8d28af' | ForEach-Object versions | Where-Object version -NE $Version)
}
# Jellyfin's catalog format uses MD5. Release SHA256SUMS is supplied independently.
ConvertTo-Json -InputObject @($entry) -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Manifest updated: $manifestPath"
