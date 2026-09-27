# Downloads the third-party binaries Tandem bundles (not committed to git):
#   scrcpy for Windows (Apache-2.0) — includes adb.exe and scrcpy-server.
# Verifies the SHA-256 against the release's SHA256SUMS.txt before unpacking.
[CmdletBinding()]
param(
    [string] $ScrcpyVersion = '4.1'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'vendor'
$target = Join-Path $vendor "scrcpy-win64-v$ScrcpyVersion"
if (Test-Path (Join-Path $target 'scrcpy.exe')) {
    Write-Host "scrcpy $ScrcpyVersion already present in $target"
    return
}

New-Item -ItemType Directory -Force $vendor | Out-Null
$base = "https://github.com/Genymobile/scrcpy/releases/download/v$ScrcpyVersion"
$zipName = "scrcpy-win64-v$ScrcpyVersion.zip"
$zip = Join-Path $vendor $zipName
$sums = Join-Path $vendor "scrcpy-v$ScrcpyVersion-SHA256SUMS.txt"

Write-Host "Downloading $zipName ..."
Invoke-WebRequest "$base/$zipName" -OutFile $zip -UseBasicParsing
Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile $sums -UseBasicParsing

$line = Select-String -Path $sums -Pattern ([regex]::Escape($zipName)) | Select-Object -First 1
if (-not $line) { throw "$zipName not listed in SHA256SUMS.txt" }
$expected = $line.Line.Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].ToLowerInvariant()
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($expected -ne $actual) { Remove-Item $zip; throw "Checksum mismatch for $zipName (expected $expected, got $actual)" }

Expand-Archive $zip -DestinationPath $vendor -Force
Remove-Item $zip, $sums
Write-Host "scrcpy $ScrcpyVersion ready in $target"
