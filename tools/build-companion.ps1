# Builds the Android companion APK (android/) that the Windows app installs on the phone.
# Output: android\app\build\outputs\apk\release\app-release.apk (picked up by Tandem.App.csproj).
[CmdletBinding()]
param(
    [string] $JavaHome = $(if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'C:\dev\jdk-21' }),
    [string] $AndroidHome = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'C:\dev\android-sdk' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:JAVA_HOME = $JavaHome
$env:ANDROID_HOME = $AndroidHome

Push-Location (Join-Path $root 'android')
try {
    & .\gradlew.bat assembleRelease --console=plain
    if ($LASTEXITCODE -ne 0) { throw "Gradle build failed ($LASTEXITCODE)" }
}
finally {
    Pop-Location
}
$apk = Join-Path $root 'android\app\build\outputs\apk\release\app-release.apk'
Write-Host ("Built {0} ({1:N0} KB)" -f $apk, ((Get-Item $apk).Length / 1KB))
