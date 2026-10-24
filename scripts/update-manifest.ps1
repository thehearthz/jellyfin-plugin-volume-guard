param(
    [string]$Version = '0.3.1.0',
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
    description = 'Experimental audio processing for Jellyfin 12.2 and standard Moonfin. Includes leveling, peak limiting, dialogue emphasis, per-device modes, cached local-library loudness analysis and administrator playback status. Optional Plugin Pages 3 integration adds Volume Guard to Jellyfin web navigation. Active switching requests a brief reconnect; Live TV returns to the live edge. No companion app required. Physical client and Live TV validation remains necessary.'
    owner = $Repository.Split('/')[0]
    category = 'General'
    versions = @([ordered]@{
        version = $Version
        changelog = 'Adds optional Plugin Pages 3 registration for the Volume Guard controls/status page in Jellyfin web navigation. Reuses the existing settings component with fragment initialization and authenticated APIs; configuration and status remain administrator-only. The normal dashboard page works without Plugin Pages. Requires compatible Plugin Pages and File Transformation installations for the sidebar entry. Audio features remain unchanged. Actual full-server/client validation remains necessary.'
        targetAbi = '12.2.0.0'
        sourceUrl = "https://github.com/$Repository/releases/download/v$Version/volume-guard-server-$Version.zip"
        checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }) + @($previous | Where-Object guid -EQ 'c49aef32-6c2e-462c-a5ee-40e18c8d28af' | ForEach-Object versions | Where-Object version -NE $Version)
}
# Jellyfin's catalog format uses MD5. Release SHA256SUMS is supplied independently.
ConvertTo-Json -InputObject @($entry) -Depth 20 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output "Manifest updated: $manifestPath"
