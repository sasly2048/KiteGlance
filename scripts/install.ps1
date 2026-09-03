<#
    install.ps1 - install Kite Glance like a normal Windows application.

    Does what a real installer does:
      - copies the exe to %LOCALAPPDATA%\Programs\KiteGlance
      - creates a Start Menu shortcut (so it's searchable from the key)
      - optionally a Desktop shortcut
      - registers in Add/Remove Programs, with a working uninstaller
      - optionally starts with Windows

    Per-user, so it needs no admin rights and no UAC prompt. That's the same
    choice VS Code, Slack, and Discord make.

    Usage:
      .\install.ps1
      .\install.ps1 -NoDesktopShortcut
      .\install.ps1 -Uninstall
      .\install.ps1 -Source path\to\KiteGlance-win-x64.exe   # explicit arch
#>

param(
    [switch]$Uninstall,
    [switch]$NoDesktopShortcut,
    [switch]$NoAutostart,
    [string]$Source
)

$ErrorActionPreference = 'Stop'

$AppName   = 'Kite Glance'
$AppId     = 'KiteGlance'
$Version   = '1.5.0'
$Publisher = $env:KITEGLANCE_PUBLISHER; if (-not $Publisher) { $Publisher = 'KiteGlance' }

$InstallDir  = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$ExePath     = Join-Path $InstallDir 'KiteGlance.exe'
$UninstallPs = Join-Path $InstallDir 'uninstall.ps1'

$StartMenu   = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$StartLnk    = Join-Path $StartMenu "$AppName.lnk"
$DesktopLnk  = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"

$ArpKey  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
$RunKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'


# Detect the host architecture once. Anything other than ARM64 picks x64.
# 32-bit (x86) Windows is not supported -- the project only ships win-arm64
# and win-x64. If the script is somehow run there, fall through to x64 so
# the loader reports the missing-support error instead of a confusing crash.
function Get-HostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    switch ($arch) {
        'Arm64' { 'win-arm64' }
        default { 'win-x64'   }
    }
}

function Resolve-SourceExe {
    param([string]$Explicit)

    $RepoRoot = Split-Path -Parent $PSScriptRoot

    # 1. Explicit path wins.
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) {
            throw "Source exe not found: $Explicit"
        }
        return $Explicit
    }

    # 2. dist\KiteGlance-<rid>.exe matching the host architecture.
    $rid = Get-HostRid
    $ridSpecific = Join-Path $RepoRoot "src\KiteGlance\dist\KiteGlance-$rid.exe"
    if (Test-Path $ridSpecific) { return $ridSpecific }

    # 3. The legacy single-exe layout (dist\KiteGlance.exe). Refuse it if its
    #    PE machine type does not match the host -- this is the bug that
    #    produced "the latest release isn't working on other PCs" reports:
    #    a single binary in dist/ silently shipped the wrong architecture.
    $legacy = Join-Path $RepoRoot 'src\KiteGlance\dist\KiteGlance.exe'
    if (Test-Path $legacy) {
        $bytes   = [System.IO.File]::ReadAllBytes($legacy)
        $peOff   = [System.BitConverter]::ToInt32($bytes, 0x3C)
        $machine  = [System.BitConverter]::ToUInt16($bytes, $peOff + 4)
        $isArm64 = ($machine -eq 0xAA64)         # IMAGE_FILE_MACHINE_ARM64
        $isX64   = ($machine -eq 0x8664)         # IMAGE_FILE_MACHINE_AMD64

        $wantedRid = $rid
        $actualRid = if ($isArm64) { 'win-arm64' }
                     elseif ($isX64) { 'win-x64'  }
                     else            { "unknown (0x$($machine.ToString('X4')))" }

        if ($actualRid -ne $wantedRid) {
            Write-Host ""
            Write-Host "  ARCHITECTURE MISMATCH" -ForegroundColor Red
            Write-Host "  --------------------" -ForegroundColor DarkGray
            Write-Host "  This machine:        $wantedRid" -ForegroundColor DarkGray
            Write-Host "  Exe in dist\:        $actualRid" -ForegroundColor DarkGray
            Write-Host ""
            Write-Host "  The single-file exe in src\KiteGlance\dist\ was built for a" -ForegroundColor Yellow
            Write-Host "  different architecture. Re-run the build to produce both:" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "      .\scripts\build.ps1" -ForegroundColor White
            Write-Host ""
            throw "Architecture mismatch: have $actualRid, need $wantedRid"
        }
        return $legacy
    }

    throw "KiteGlance.exe not found. Run .\scripts\build.ps1 first, or pass -Source <path>."
}


function Stop-App {
    Get-Process $AppId -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 500
}

function New-Shortcut($LinkPath, $Target, $Description) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($LinkPath)
    $sc.TargetPath       = $Target
    $sc.WorkingDirectory = Split-Path $Target
    $sc.IconLocation     = "$Target,0"
    $sc.Description      = $Description
    $sc.Save()
}


# ══════════════════════════════════════════════════════════════════════
#  UNINSTALL
# ══════════════════════════════════════════════════════════════════════
if ($Uninstall) {
    Write-Host ""
    Write-Host "  Uninstalling $AppName..." -ForegroundColor Cyan

    Stop-App

    Remove-Item $StartLnk   -Force -ErrorAction SilentlyContinue
    Remove-Item $DesktopLnk -Force -ErrorAction SilentlyContinue

    Remove-Item $ArpKey -Recurse -Force -ErrorAction SilentlyContinue

    Remove-ItemProperty -Path $RunKey -Name $AppId -Force -ErrorAction SilentlyContinue

    # The exe deletes itself last, from a detached shell, because a running
    # script cannot remove the directory it is executing out of.
    if (Test-Path $InstallDir) {
        Start-Process powershell -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-Command',
            "Start-Sleep -Seconds 1; Remove-Item -Recurse -Force '$InstallDir'"
        )
    }

    Write-Host "  Done. Your API credentials in %APPDATA%\KiteGlance were left alone." -ForegroundColor Green
    Write-Host "  Delete that folder too if you want a clean slate." -ForegroundColor DarkGray
    Write-Host ""
    exit 0
}


# ══════════════════════════════════════════════════════════════════════
#  INSTALL
# ══════════════════════════════════════════════════════════════════════
$source = Resolve-SourceExe -Explicit $Source

Write-Host ""
Write-Host "  Installing $AppName $Version" -ForegroundColor Cyan
Write-Host "  source: $source" -ForegroundColor DarkGray
Write-Host "  target: $InstallDir" -ForegroundColor DarkGray
Write-Host ""

Stop-App

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $source -Destination $ExePath -Force
Copy-Item $PSCommandPath -Destination $UninstallPs -Force

# ---- Start Menu -------------------------------------------------------
New-Shortcut $StartLnk $ExePath 'Your Zerodha portfolio, at a glance.'
Write-Host "  + Start Menu shortcut" -ForegroundColor DarkGray

# ---- Desktop ----------------------------------------------------------
if (-not $NoDesktopShortcut) {
    New-Shortcut $DesktopLnk $ExePath 'Your Zerodha portfolio, at a glance.'
    Write-Host "  + Desktop shortcut" -ForegroundColor DarkGray
}

# ---- Add/Remove Programs ---------------------------------------------
# This is the bit that makes it feel like real software rather than a
# loose exe someone dropped in a folder.
$size = [math]::Round((Get-Item $ExePath).Length / 1KB)

New-Item -Path $ArpKey -Force | Out-Null
Set-ItemProperty $ArpKey DisplayName     $AppName
Set-ItemProperty $ArpKey DisplayVersion  $Version
Set-ItemProperty $ArpKey Publisher       $Publisher
Set-ItemProperty $ArpKey DisplayIcon     $ExePath
Set-ItemProperty $ArpKey InstallLocation $InstallDir
Set-ItemProperty $ArpKey EstimatedSize   $size -Type DWord
Set-ItemProperty $ArpKey NoModify        1 -Type DWord
Set-ItemProperty $ArpKey NoRepair        1 -Type DWord
Set-ItemProperty $ArpKey UninstallString `
    "powershell -NoProfile -ExecutionPolicy Bypass -File `"$UninstallPs`" -Uninstall"

Write-Host "  + Registered in Add/Remove Programs" -ForegroundColor DarkGray

# ---- Autostart --------------------------------------------------------
if (-not $NoAutostart) {
    Set-ItemProperty -Path $RunKey -Name $AppId -Value "`"$ExePath`""
    Write-Host "  + Starts with Windows" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  Installed." -ForegroundColor Green
Write-Host ""
Write-Host "  Launching..." -ForegroundColor DarkGray
Start-Process $ExePath
Write-Host ""
Write-Host "  Find it in the system tray. Uninstall from Settings > Apps," -ForegroundColor DarkGray
Write-Host "  or with:  .\install.ps1 -Uninstall" -ForegroundColor DarkGray
Write-Host ""
