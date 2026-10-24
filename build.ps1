param([switch]$ServerOnly, [switch]$LegacyCompanion, [switch]$SkipTests, [string]$Version = '0.2.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Version must have four numeric parts.' }
Set-Location $PSScriptRoot
$output = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $output | Out-Null

# Standard installations are supported; the task-local toolchain is an optional convenience.
$localDotnet = Join-Path (Split-Path $PSScriptRoot -Parent) '.dotnet/dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
& $dotnetCommand build server/VolumeGuard/VolumeGuard.csproj -c Release -p:NuGetAudit=false "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
if (!$SkipTests) {
    & $dotnetCommand build server/VolumeGuard.Tests/VolumeGuard.Tests.csproj -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Server test build failed.' }
    & $dotnetCommand server/VolumeGuard.Tests/bin/Release/net10.0/VolumeGuard.Tests.dll
    if ($LASTEXITCODE -ne 0) { throw 'Server tests failed.' }
    & $dotnetCommand build server/VolumeGuard.HttpTests/VolumeGuard.HttpTests.csproj -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'HTTP test build failed.' }
    & $dotnetCommand server/VolumeGuard.HttpTests/bin/Release/net10.0/VolumeGuard.HttpTests.dll
    if ($LASTEXITCODE -ne 0) { throw 'HTTP tests failed.' }
}
$plugin = Join-Path $PSScriptRoot 'server/VolumeGuard/bin/Release/net10.0/Jellyfin.Plugin.VolumeGuard.dll'
Copy-Item -LiteralPath $plugin -Destination $output -Force
Compress-Archive -LiteralPath $plugin -DestinationPath (Join-Path $output "volume-guard-server-$Version.zip") -Force

if ($LegacyCompanion -and !$ServerOnly) {
    $localJava = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '.build-tools/microsoft-jdk') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$localJava) { $localJava = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '.build-tools/jdk') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if ($localJava) { $env:JAVA_HOME = $localJava.FullName }
    if ($env:JAVA_HOME) { $env:PATH = (Join-Path $env:JAVA_HOME 'bin') + [IO.Path]::PathSeparator + $env:PATH }
    if (!$SkipTests) { & ./androidtv-source/volume-guard-tests/run.ps1 }
    $localSdk = Join-Path $PSScriptRoot '.build-tools/android-sdk'
    if (Test-Path -LiteralPath $localSdk) { $env:ANDROID_HOME = $localSdk }
    $env:GRADLE_USER_HOME = Join-Path $PSScriptRoot '.build-tools/gradle'
    Push-Location (Join-Path $PSScriptRoot 'androidtv-source')
    try {
        & ./gradlew.bat :app:assembleDebug --no-daemon --console=plain '-Pjellyfin.version=0.19.10'
        if ($LASTEXITCODE -ne 0) { throw 'Android TV build failed.' }
        $apk = Get-ChildItem -LiteralPath app/build/outputs/apk/debug -Filter '*.apk' | Select-Object -First 1
        if (!$apk) { throw 'Android TV APK was not produced.' }
        Copy-Item -LiteralPath $apk.FullName -Destination (Join-Path $output 'volume-guard-tv-0.1.0-debug.apk') -Force
    }
    finally { Pop-Location }
}

$hashes = Get-ChildItem -LiteralPath $output -File | Where-Object Extension -In '.zip', '.dll', '.apk' | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding utf8NoBOM
Write-Output "Build output: $output"
