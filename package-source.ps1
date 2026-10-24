$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$root = $PSScriptRoot
$archivePath = Join-Path $root 'artifacts/volume-guard-source-0.1.0.zip'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'artifacts') | Out-Null
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
$excluded = @('.git', '.build-tools', 'artifacts', 'bin', 'obj', 'build', '.gradle', '.kotlin', 'classes', 'node_modules')
function Add-SourceFolder([string]$folder) {
    foreach ($item in Get-ChildItem -LiteralPath $folder -Force) {
        if ($item.Name -in $excluded) { continue }
        if ($item.PSIsContainer) { Add-SourceFolder $item.FullName; continue }
        if ($item.Name -eq 'local.properties' -or $item.Extension -eq '.png' -and $item.Directory.Name -eq 'ui-tests') { continue }
        $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $inputStream = [IO.File]::OpenRead($item.FullName)
        $entryStream = $entry.Open()
        try { $inputStream.CopyTo($entryStream) }
        finally { $entryStream.Dispose(); $inputStream.Dispose() }
    }
}
try { Add-SourceFolder $root }
finally { $archive.Dispose(); $stream.Dispose() }
Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -File | Where-Object Extension -In '.zip', '.dll', '.apk' | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $root 'artifacts/SHA256SUMS') -Encoding utf8NoBOM
Write-Output "Source archive: $archivePath"
