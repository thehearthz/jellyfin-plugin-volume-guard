param(
    [string]$Version = '0.1.0.0',
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
    overview = 'Volume profiles and automatic night schedules for the Volume Guard TV companion'
    description = 'Experimental companion settings plugin for Jellyfin 12.2. Provides Balanced/Night profiles and weekly schedules. Requires the separately installed Volume Guard Android TV companion for audio leveling, peak limiting, and player controls. Stock Jellyfin clients do not process audio from this plugin alone.'
    owner = $Repository.Split('/')[0]
    category = 'General'
    versions = @([ordered]@{
        version = $Version
        changelog = 'Initial experimental release: shared audio profiles, timezone-aware weekly night schedules, administrator configuration, and authenticated companion policy API. Install the Android TV companion separately for actual audio processing and player toggles. Physical Chromecast and Live TV playback validation remains necessary.'
        targetAbi = '12.2.0.0'
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/volume-guard-server-$Version.zip"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }) + @($previous | Where-Object guid -EQ 'c49aef32-6c2e-462c-a5ee-40e18c8d28af' | ForEach-Object versions | Where-Object version -NE $Version)
}
# Jellyfin's catalog format uses MD5. Release SHA256SUMS is supplied independently.
ConvertTo-Json -InputObject @($entry) -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Manifest updated: $manifestPath"
