<#
    build.ps1 - produce the shippable Kite Glance executable.

    Output:
      dist\KiteGlance-win-arm64.exe   # native ARM64 (Snapdragon X Elite)
      dist\KiteGlance-win-x64.exe     # Intel / AMD
      - one file each, .NET bundled, no runtime needed on the target machine

    Usage:
      .\build.ps1                       # both architectures (recommended)
      .\build.ps1 -Arch x64             # x64 only
      .\build.ps1 -Arch arm64           # ARM64 only
      .\build.ps1 -SingleArch arm64     # legacy single-exe output (dist\KiteGlance.exe)

    Why this changed
    ----------------
    The previous default of "ARM64 only" was a footgun the moment anyone other
    than the developer needed the binary: an arm64 PE will not load on an x64
    PC, so a self-contained single-file exe that "isn't working" on a friend's
    machine was almost always an architecture mismatch, not a real bug.

    Building both architectures in one run costs little (no extra toolchain
    work, just a second `dotnet publish` over the same source) and removes
    the entire class of "wrong-binary" reports. `install.ps1` picks the
    right one for the host automatically.
#>

param(
    [ValidateSet('both', 'x64', 'arm64')]
    [string]$Arch = 'both',

    [switch]$SingleArch
)

$ErrorActionPreference = 'Stop'

# Back-compat: old call shape was `-Arch arm64` and produced a single
# dist\KiteGlance.exe. Honour it when requested, then bail out of the new
# dual-build path.
if ($SingleArch -and $Arch -eq 'both') {
    Write-Warning "-SingleArch requires -Arch x64 or -Arch arm64"
    exit 2
}

if ($SingleArch) {
    $rid = "win-$Arch"
    $RepoRoot = Split-Path -Parent $PSScriptRoot
    $ProjectDir = Join-Path $RepoRoot 'src\KiteGlance'
    Push-Location $ProjectDir

    Get-Process KiteGlance -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  stopping running instance..." -ForegroundColor DarkYellow
        $_ | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Remove-Item -Recurse -Force obj, bin, dist -ErrorAction SilentlyContinue

    dotnet publish `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -o "publish\$rid"

    if ($LASTEXITCODE -ne 0) { Pop-Location; exit 1 }

    New-Item -ItemType Directory -Force -Path dist | Out-Null
    Copy-Item "publish\$rid\KiteGlance.exe" -Destination "dist\KiteGlance.exe" -Force

    $mb = [math]::Round((Get-Item "dist\KiteGlance.exe").Length / 1MB, 1)
    Write-Host ""
    Write-Host "  Built  dist\KiteGlance.exe  ($mb MB, $rid)" -ForegroundColor Green
    Write-Host ""
    Pop-Location
    exit 0
}

# Default dual build.
$rids = if ($Arch -eq 'both') { @('win-arm64', 'win-x64') } else { @("win-$Arch") }

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjectDir = Join-Path $RepoRoot 'src\KiteGlance'
Push-Location $ProjectDir

Write-Host ""
Write-Host "  Kite Glance - production build" -ForegroundColor Cyan
Write-Host "  -------------------------------" -ForegroundColor DarkGray
Write-Host "  targets: $($rids -join ', ')" -ForegroundColor DarkGray
Write-Host ""

Get-Process KiteGlance -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  stopping running instance..." -ForegroundColor DarkYellow
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Remove-Item -Recurse -Force obj, bin, dist -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path dist | Out-Null

foreach ($rid in $rids) {
    Write-Host "  publishing $rid ..." -ForegroundColor DarkGray

    dotnet publish `
        -c Release `
        -r $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -o "publish\$rid" | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  BUILD FAILED ($rid)" -ForegroundColor Red
        Pop-Location
        exit 1
    }

    $src = "publish\$rid\KiteGlance.exe"
    $dst = "dist\KiteGlance-$rid.exe"
    Copy-Item $src -Destination $dst -Force
    $mb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Host "  + $dst  ($mb MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "  Built:" -ForegroundColor Green
Get-ChildItem dist\*.exe | ForEach-Object {
    Write-Host ("    {0,-32}  {1} MB" -f $_.Name, ([math]::Round($_.Length / 1MB, 1)))
}
Write-Host ""
Write-Host "  Run it:      .\dist\KiteGlance-$($rids[0]).exe"
Write-Host "  Install it:  .\install.ps1   (picks the right exe for this machine)"
Write-Host ""

Pop-Location
