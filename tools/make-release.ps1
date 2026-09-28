# Builds a Tandem release: the Windows app (with the phone app inside), packed by Velopack into
# artifacts\releases\ as TandemSetup.exe plus the update package and feed that installed copies
# of Tandem read to update themselves.
#
#   tools\make-release.ps1           build only (version comes from Tandem.App.csproj)
#   tools\make-release.ps1 -Upload   also create a DRAFT GitHub release with the files testers need;
#                                    publish it on github.com (or `gh release edit vX --draft=false`)
#   tools\make-release.ps1 -Upload -SkipBuild   upload what's already in artifacts\releases (e.g. after
#                                    installing and trying that exact TandemSetup.exe)
#
# Keep artifacts\releases\ between releases: vpk makes small "delta" updates from the previous one.
[CmdletBinding()]
param(
    [switch] $Upload,
    [switch] $SkipBuild,
    [string] $Dotnet = $(if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'C:\dev\dotnet\dotnet.exe' }),
    [string] $Vpk = $(if (Get-Command vpk -ErrorAction SilentlyContinue) { 'vpk' } else { 'C:\dev\vpk\vpk.exe' })
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'windows\Tandem.App\Tandem.App.csproj'
$version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
# Also the install folder name (%LOCALAPPDATA%\<packId>); must not be "Tandem", which holds Tandem's settings.
$packId = 'SamyogKarki.Tandem'
$repo = 'https://github.com/SamyogKarki/tandem'
$notes = Join-Path $root "docs\release-notes\$version.md"
$publishDir = Join-Path $root "artifacts\publish\$version"
$releaseDir = Join-Path $root 'artifacts\releases'

# vpk is a .NET tool built for an older runtime; let it run on whatever .NET is installed.
if (-not $env:DOTNET_ROOT -and (Test-Path 'C:\dev\dotnet')) { $env:DOTNET_ROOT = 'C:\dev\dotnet' }
$env:DOTNET_ROLL_FORWARD = 'Major'

Write-Host "== Tandem $version"
if (-not (Test-Path $notes)) { throw "Write the release notes first: $notes" }

$setup = Join-Path $releaseDir 'TandemSetup.exe'
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-companion.ps1')

    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & $Dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained -o $publishDir -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

    & $Vpk pack --packId $packId --packVersion $version --packDir $publishDir --mainExe Tandem.exe --runtime win-x64 --channel win `
        --packTitle Tandem --packAuthors 'Samyog Karki' --aumid SamyogKarki.Tandem `
        --icon (Join-Path $root 'windows\Tandem.App\Assets\tandem.ico') `
        --releaseNotes $notes --outputDir $releaseDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }
    # Testers see this name on the download page.
    Copy-Item (Join-Path $releaseDir "$packId-win-Setup.exe") $setup -Force
    Write-Host ("Built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
}

if (-not $Upload) { return }

# vpk uploads what the updater needs (feed + packages) as a draft; then swap in the friendly
# installer name and drop the extras testers don't need.
$token = (& gh auth token).Trim()
& $Vpk upload github --repoUrl $repo --token $token --outputDir $releaseDir `
    --tag "v$version" --releaseName "Tandem $version"
if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
& gh release upload "v$version" $setup --repo $repo --clobber
foreach ($extra in "$packId-win-Setup.exe", "$packId-win-Portable.zip") {
    & gh release delete-asset "v$version" $extra --repo $repo --yes 2>$null
}
& gh release edit "v$version" --repo $repo --notes-file $notes
Write-Host "Draft release v$version is ready: $repo/releases (publish it when you've checked it)"
