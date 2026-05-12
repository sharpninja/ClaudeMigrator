[CmdletBinding()]
param(
    [string]$ProfileRoot = (Join-Path $env:TEMP 'ClaudeMigrator.Tests\edge-test-profile'),
    [string]$ProfileDirectory = 'Profile 1',
    [string]$StartUrl = 'https://claude.ai/settings/data-privacy-controls',
    [int]$RemoteDebuggingPort = 9222,
    [string]$EdgeExePath
)

$ErrorActionPreference = 'Stop'

function Resolve-EdgeExecutable {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = $ExplicitPath
        if (-not [System.IO.Path]::IsPathRooted($resolved)) {
            $resolved = Join-Path (Get-Location) $resolved
        }

        $resolved = [System.IO.Path]::GetFullPath($resolved)
        if (-not (Test-Path -LiteralPath $resolved)) {
            throw "Edge executable not found: $resolved"
        }

        return $resolved
    }

    $candidates = @(
        (Get-Command msedge.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
        'C:\Program Files (x86)\Microsoft\EdgeCore\Optimized\msedge.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    throw 'Microsoft Edge executable not found.'
}

$edgePath = Resolve-EdgeExecutable -ExplicitPath $EdgeExePath
$profileRootPath = [System.IO.Path]::GetFullPath($ProfileRoot)
$profilePath = Join-Path $profileRootPath $ProfileDirectory

New-Item -ItemType Directory -Force -Path $profileRootPath | Out-Null
New-Item -ItemType Directory -Force -Path $profilePath | Out-Null

$debugUrl = "http://127.0.0.1:$RemoteDebuggingPort"
$env:CLAUDEMIGRATOR_LIVE_EDGE_DEBUG_URL = $debugUrl
$env:CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_ROOT = $profileRootPath
$env:CLAUDEMIGRATOR_LIVE_EDGE_PROFILE_DIRECTORY = $ProfileDirectory

$arguments = @(
    "--user-data-dir=$profileRootPath"
    "--profile-directory=$ProfileDirectory"
    "--remote-debugging-port=$RemoteDebuggingPort"
    '--no-first-run'
    '--new-window'
    $StartUrl
)

$process = Start-Process -FilePath $edgePath -ArgumentList $arguments -PassThru

Write-Host "Started Edge PID $($process.Id)"
Write-Host "Profile root: $profileRootPath"
Write-Host "Profile dir: $profilePath"
Write-Host "Debug URL: $debugUrl"
Write-Host "Use the window to sign in to Claude, then run the live test from the same shell."
