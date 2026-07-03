# Build a self-contained ClipSync publish and wrap it in a per-user MSI.
# Output: dist\ClipSync.msi (relative to repo root).
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot   = (Resolve-Path (Join-Path $root '..')).Path
$proj       = Join-Path $root 'ClipSync\ClipSync.csproj'
$wxs        = Join-Path $root 'installer\ClipSync.wxs'
$publishDir = Join-Path $root "publish\win-$Arch"
$iconDir    = Join-Path $root 'ClipSync\Assets'
$dist       = Join-Path $repoRoot 'dist'
$msiOut     = Join-Path $dist 'ClipSync.msi'

# Resolve dotnet and wix without relying on PATH.
$dotnet = 'dotnet'
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    $dotnet = 'C:\Program Files\dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw "dotnet not found." }
}
$wix = 'wix'
if (-not (Get-Command $wix -ErrorAction SilentlyContinue)) {
    $wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (-not (Test-Path $wix)) {
        throw "wix not found. Install with: dotnet tool install --global wix"
    }
}

# Ensure the icon is up to date.
& (Join-Path $root 'scripts\build-icon.ps1')

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host "Publishing $Configuration win-$Arch to $publishDir..."
& $dotnet publish $proj `
    -c $Configuration `
    -r "win-$Arch" `
    --self-contained true `
    -p:Platform=$Arch `
    -p:PublishSingleFile=false `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=None `
    -p:PublishReadyToRun=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Drop publish-time .pdb files to keep the MSI smaller.
Get-ChildItem $publishDir -Recurse -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# WiX expects per-arch arguments.
$wixArch = if ($Arch -eq 'arm64') { 'arm64' } else { 'x64' }

Write-Host "Building MSI with WiX..."
& $wix build $wxs `
    -arch $wixArch `
    -bindpath "publish=$publishDir" `
    -bindpath "icon=$iconDir" `
    -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)." }

$size = '{0:N1} MB' -f ((Get-Item $msiOut).Length / 1MB)
Write-Host "Done: $msiOut ($size)"
