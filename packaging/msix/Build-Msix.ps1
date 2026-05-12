# Build, sign, and install a local MSIX of ClaudeMigrator.
#
# Steps:
#   1. Resolve Windows SDK tools (makeappx, signtool).
#   2. Generate PNG visual assets from the existing app icon.
#   3. dotnet publish the app as framework-dependent win-x64.
#   4. Stage AppxManifest + Images alongside the publish output.
#   5. makeappx pack into an .msix.
#   6. Make or reuse a self-signed code-signing cert (CN=ClaudeMigrator).
#   7. signtool sign the .msix.
#   8. Install the cert to LocalMachine TrustedPeople (admin).
#   9. Add-AppxPackage to install the .msix for the current user.
#
# Run from any cwd. Re-runnable.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$AppProject = Join-Path $RepoRoot "src\ClaudeMigrator.App\ClaudeMigrator.App.csproj"
$ManifestSource = Join-Path $PSScriptRoot "Package.appxmanifest"
$IconSource = Join-Path $RepoRoot "src\ClaudeMigrator.App\Assets\avalonia-logo.ico"
$OutputRoot = Join-Path $PSScriptRoot "out"
$StagingDir = Join-Path $OutputRoot "staging"
$ImagesDir = Join-Path $StagingDir "Images"
$PackageOutput = Join-Path $OutputRoot "ClaudeMigrator.msix"
$CertSubject = "CN=ClaudeMigrator"
$CertExport = Join-Path $OutputRoot "ClaudeMigrator.cer"

function Resolve-SdkTool {
    param([string]$ToolName)
    $bin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path $bin)) { throw "Windows SDK not found under $bin" }
    $versions = Get-ChildItem $bin -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object Name -Descending
    foreach ($ver in $versions) {
        $candidate = Join-Path $ver.FullName "x64\$ToolName"
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Could not locate $ToolName under $bin"
}

function Write-PngFromIcon {
    param(
        [string]$IconPath,
        [string]$DestinationPath,
        [int]$Width,
        [int]$Height
    )
    Add-Type -AssemblyName System.Drawing | Out-Null
    $icon = [System.Drawing.Icon]::new($IconPath)
    try {
        $sourceBitmap = $icon.ToBitmap()
        try {
            $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
            try {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.DrawImage($sourceBitmap, 0, 0, $Width, $Height)
                } finally {
                    $graphics.Dispose()
                }
                $bitmap.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $bitmap.Dispose()
            }
        } finally {
            $sourceBitmap.Dispose()
        }
    } finally {
        $icon.Dispose()
    }
}

function Ensure-CodeSigningCert {
    param([string]$Subject)
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
    if ($existing) {
        Write-Host "Reusing existing code-signing cert thumbprint $($existing.Thumbprint)"
        return $existing
    }

    Write-Host "Creating new self-signed code-signing cert ($Subject)..."
    $params = @{
        Type              = 'CodeSigningCert'
        Subject           = $Subject
        KeyUsage          = 'DigitalSignature'
        KeyExportPolicy   = 'Exportable'
        CertStoreLocation = 'Cert:\CurrentUser\My'
        NotAfter          = (Get-Date).AddYears(3)
        FriendlyName      = 'ClaudeMigrator local signing'
    }
    return New-SelfSignedCertificate @params
}

Write-Host "==> Resolving Windows SDK tools" -ForegroundColor Cyan
$Makeappx = Resolve-SdkTool -ToolName "makeappx.exe"
$Signtool = Resolve-SdkTool -ToolName "signtool.exe"
Write-Host "    makeappx: $Makeappx"
Write-Host "    signtool: $Signtool"

Write-Host "==> Cleaning output directory" -ForegroundColor Cyan
if (Test-Path $OutputRoot) { Remove-Item $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir | Out-Null

Write-Host "==> Publishing app ($Configuration, $Runtime, self-contained)" -ForegroundColor Cyan
& dotnet publish $AppProject -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $StagingDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE" }

Write-Host "==> Generating PNG visual assets" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $ImagesDir -Force | Out-Null
$assetSizes = @{
    "Square44x44Logo.png"   = @{ Width = 44;  Height = 44 }
    "Square71x71Logo.png"   = @{ Width = 71;  Height = 71 }
    "Square150x150Logo.png" = @{ Width = 150; Height = 150 }
    "Wide310x150Logo.png"   = @{ Width = 310; Height = 150 }
    "StoreLogo.png"         = @{ Width = 50;  Height = 50 }
    "SplashScreen.png"      = @{ Width = 620; Height = 300 }
}
foreach ($name in $assetSizes.Keys) {
    $size = $assetSizes[$name]
    $destination = Join-Path $ImagesDir $name
    Write-PngFromIcon -IconPath $IconSource -DestinationPath $destination -Width $size.Width -Height $size.Height
}

Write-Host "==> Copying manifest" -ForegroundColor Cyan
Copy-Item $ManifestSource (Join-Path $StagingDir "AppxManifest.xml") -Force

Write-Host "==> Packing MSIX" -ForegroundColor Cyan
& $Makeappx pack /o /d $StagingDir /p $PackageOutput
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit $LASTEXITCODE" }

Write-Host "==> Ensuring code-signing certificate" -ForegroundColor Cyan
$cert = Ensure-CodeSigningCert -Subject $CertSubject
Export-Certificate -Cert $cert -FilePath $CertExport -Type CERT | Out-Null

Write-Host "==> Signing MSIX" -ForegroundColor Cyan
& $Signtool sign /fd SHA256 /a /sha1 $cert.Thumbprint $PackageOutput
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit $LASTEXITCODE" }

Write-Host ""
Write-Host "Package: $PackageOutput" -ForegroundColor Green
Write-Host "Cert:    $CertExport" -ForegroundColor Green
Write-Host "Thumbprint: $($cert.Thumbprint)" -ForegroundColor Green

if ($SkipInstall) {
    Write-Host "Skipping install (per -SkipInstall)."
    return
}

Write-Host ""
Write-Host "==> Installing certificate to LocalMachine TrustedPeople (requires admin)" -ForegroundColor Cyan
$adminScript = @"
Import-Certificate -FilePath '$CertExport' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
"@
$adminScriptPath = Join-Path $OutputRoot "install-cert.ps1"
Set-Content -Path $adminScriptPath -Value $adminScript -Encoding UTF8
& gsudo pwsh -ExecutionPolicy Bypass -File $adminScriptPath
if ($LASTEXITCODE -ne 0) { throw "Certificate install failed with exit $LASTEXITCODE" }
Remove-Item $adminScriptPath -Force -ErrorAction SilentlyContinue

Write-Host "==> Installing MSIX for current user" -ForegroundColor Cyan
# Force install on the system volume (C:). Installing on a non-system AppX
# volume can fail with 0x800701C0 at launch on some Windows 11 builds.
$systemVolume = Get-AppxVolume | Where-Object { $_.IsSystemVolume -eq $true } | Select-Object -First 1
if ($null -ne $systemVolume) {
    Add-AppxPackage -Path $PackageOutput -Volume $systemVolume -ForceApplicationShutdown
} else {
    Add-AppxPackage -Path $PackageOutput -ForceApplicationShutdown
}
Write-Host ""
Write-Host "Installed. Launch via Start menu: ClaudeMigrator" -ForegroundColor Green
