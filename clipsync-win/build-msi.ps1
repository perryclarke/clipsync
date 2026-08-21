# Build a self-contained ClipSync publish and wrap it in a per-user MSI.
# Output: dist\ClipSync.msi (relative to repo root).
#
# By default the app binaries and the MSI are Authenticode-signed with a
# self-signed code-signing certificate (created on first run, kept in the
# CurrentUser store). Pass -SkipSign to build unsigned. To trust the
# signature on a machine, import dist\clipsync-codesign.cer into that user's
# Trusted Root Certification Authorities and Trusted Publishers stores.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [string]$Configuration = 'Release',
    [switch]$SkipSign
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

$certSubject  = 'CN=ClipSync Code Signing'
$timestampUrl = 'http://timestamp.digicert.com'

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

# The wxs uses util:CloseApplication, which needs the Util extension.
# Install it (matched to the wix version, so the schemas agree) if this
# machine doesn't have it yet.
$utilExt = 'WixToolset.Util.wixext'
$exts = & $wix extension list --global
if (-not ($exts -match [regex]::Escape($utilExt))) {
    $wixVer = ((& $wix --version) -split '\+')[0]
    Write-Host "Installing WiX extension $utilExt/$wixVer..."
    & $wix extension add --global "$utilExt/$wixVer"
    if ($LASTEXITCODE -ne 0) {
        & $wix extension add --global $utilExt
        if ($LASTEXITCODE -ne 0) { throw "wix extension add $utilExt failed (exit $LASTEXITCODE)." }
    }
}

# Resolve signtool and ensure a signing certificate exists (unless -SkipSign).
$certThumb = $null
$signtool  = $null
if (-not $SkipSign) {
    $signtool = 'signtool'
    if (-not (Get-Command $signtool -ErrorAction SilentlyContinue)) {
        $cand = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' } |
                Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $cand) { throw "signtool not found. Install the Windows SDK, or pass -SkipSign." }
        $signtool = $cand.FullName
    }

    $cert = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Subject -eq $certSubject -and $_.NotAfter -gt (Get-Date) } |
            Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating self-signed code-signing certificate ($certSubject)..."
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy Exportable `
            -KeyUsage DigitalSignature -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(10) -FriendlyName 'ClipSync Code Signing'
    }
    $certThumb = $cert.Thumbprint
}

function Invoke-Sign {
    param([string[]]$Paths)
    if ($SkipSign -or $Paths.Count -eq 0) { return }
    & $signtool sign /fd SHA256 /sha1 $certThumb /tr $timestampUrl /td SHA256 @Paths
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)." }
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

# Sign our own binaries before packaging, so the signed apphost/dll end up
# inside the MSI (third-party runtime files are left as shipped).
if (-not $SkipSign) {
    Write-Host "Signing app binaries..."
    $toSign = @('ClipSync.exe', 'ClipSync.dll') |
              ForEach-Object { Join-Path $publishDir $_ } |
              Where-Object { Test-Path $_ }
    Invoke-Sign -Paths $toSign
}

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# WiX expects per-arch arguments.
$wixArch = if ($Arch -eq 'arm64') { 'arm64' } else { 'x64' }

Write-Host "Building MSI with WiX..."
& $wix build $wxs `
    -arch $wixArch `
    -ext $utilExt `
    -bindpath "publish=$publishDir" `
    -bindpath "icon=$iconDir" `
    -o $msiOut
if ($LASTEXITCODE -ne 0) { throw "wix build failed (exit $LASTEXITCODE)." }

# Sign the finished MSI and export the public cert for trust distribution.
if (-not $SkipSign) {
    Write-Host "Signing MSI..."
    Invoke-Sign -Paths @($msiOut)
    $cerOut = Join-Path $dist 'clipsync-codesign.cer'
    Export-Certificate -Cert (Get-Item "Cert:\CurrentUser\My\$certThumb") -FilePath $cerOut | Out-Null
    Write-Host "Public cert: $cerOut (import into Trusted Root + Trusted Publishers to trust the signature)."
}

$size = '{0:N1} MB' -f ((Get-Item $msiOut).Length / 1MB)
Write-Host "Done: $msiOut ($size)"
