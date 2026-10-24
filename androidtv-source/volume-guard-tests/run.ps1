$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $PSScriptRoot 'classes'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$source = Join-Path $projectRoot 'app/src/main/java/org/jellyfin/androidtv/volumeguard/VolumeGuardDsp.java'
& java -m jdk.compiler/com.sun.tools.javac.Main -d $outputDirectory $source (Join-Path $projectRoot 'app/src/main/java/org/jellyfin/androidtv/volumeguard/VolumeGuardSchedule.java') (Join-Path $PSScriptRoot 'VolumeGuardDspTest.java')
if ($LASTEXITCODE -ne 0) { throw 'DSP compilation failed' }
& java -cp $outputDirectory VolumeGuardDspTest
if ($LASTEXITCODE -ne 0) { throw 'DSP checks failed' }
