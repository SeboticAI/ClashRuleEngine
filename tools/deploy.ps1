# Deploys the built plugin for a given Navisworks version (DEV loop - for the team,
# ship Installer\ClashRuleEngine.iss instead).
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
# Navisworks 2027 rules (learned the hard way, 2026-06-12):
#   - The ONLY supported third-party location is the install's Plugins folder
#     with a NAME-MATCHED pair:  Plugins\ClashRuleEngine\ClashRuleEngine.dll
#   - The user-profile plugin folder (%AppData%) was DEPRECATED in 2027.
#   - ApplicationPlugins bundles are not used by Navisworks for .NET plugins.
#   - "*.Plugin.dll" naming is for Autodesk-internal plugins only.
#   This layout also works on <= 2026, so it is the single mechanism for all.
#
# DEPLOYED LAYOUT (do not flatten - each location is load-bearing):
#   Plugins\ClashRuleEngine\
#     ClashRuleEngine.dll
#     en-US\ClashRuleEngineRibbon.xaml   <- [RibbonLayout]; NO custom ribbon tab without it
#     en-US\ClashRuleEngine.name         <- [Strings]
#     Images\*.ico  and  *.ico           <- [Command]/[AddInPlugin] Icon/LargeIcon
# Navisworks reads the ribbon layout and strings as LOOSE FILES from a locale subfolder
# next to the DLL, per the SDK CustomRibbon sample's post-build event - NOT as embedded
# resources. Icons are shipped both flat and under Images\ because the SDK allows either.
#
# Requires elevation (Program Files) - the script self-elevates with a UAC prompt.
param(
    [string]$Version = "2027",
    [string]$Configuration = "Release"
)

$root = Split-Path $PSScriptRoot -Parent
$srcDir = Join-Path $root "bin\x64\$Configuration\$Version"
$dll = Join-Path $srcDir "ClashRuleEngine.dll"

if (-not (Test-Path $dll)) {
    Write-Error "Build output not found: $dll - build first (tools\build.ps1 -Version $Version)"
    exit 1
}

# Files that must land in the plugin folder, as paths RELATIVE to the build output. The
# csproj target StagePluginPayload puts them there.
$payload = @(
    "ClashRuleEngine.dll",
    "en-US\ClashRuleEngineRibbon.xaml",
    "en-US\ClashRuleEngine.name",
    "oconnors_clash_16.ico",
    "oconnors_clash_32.ico",
    "Images\oconnors_clash_16.ico",
    "Images\oconnors_clash_32.ico"
)
foreach ($f in $payload) {
    if (-not (Test-Path (Join-Path $srcDir $f))) {
        Write-Error "Missing from build output: $f - rebuild (and run tools\make-icons.ps1 if an icon)."
        exit 1
    }
}

# A running Navisworks holds a lock on the loaded plugin DLL, so the copy silently
# fails and leaves the OLD build in place. Catch that before wasting a UAC prompt.
$running = @(Get-Process -Name "roamer" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Error ("Navisworks is running (roamer.exe, PID " + ($running.Id -join ", ") +
        "). Close it first - a loaded plugin DLL is locked and the deploy would " +
        "silently keep the old build.")
    exit 1
}

$dest = "C:\Program Files\Autodesk\Navisworks Manage $Version\Plugins\ClashRuleEngine"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Elevating to write to Program Files (accept the UAC prompt)..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-Version', $Version, '-Configuration', $Configuration
    )
} else {
    foreach ($f in $payload) {
        $target = Join-Path $dest $f
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
        Copy-Item (Join-Path $srcDir $f) $target -Force
    }
}

# Verify by CONTENT, not mere existence: a cancelled UAC prompt or a locked file
# leaves a STALE copy in place, which a Test-Path check would wrongly report as
# success. Every payload file is checked, so a missing icon is caught too.
$bad = @()
foreach ($f in $payload) {
    $destFile = Join-Path $dest $f
    $srcHash = (Get-FileHash (Join-Path $srcDir $f)).Hash
    $destHash = if (Test-Path $destFile) { (Get-FileHash $destFile).Hash } else { "" }
    if ($srcHash -ne $destHash) { $bad += $f }
}

if ($bad.Count -eq 0) {
    Write-Host "Deployed and verified to: $dest" -ForegroundColor Green
    $payload | ForEach-Object { Write-Host "  $_" }
    Write-Host "Restart Navisworks, then click OConnors Clash -> Clash Engine."
} else {
    Write-Error ("Deploy FAILED - these do not match the build: " + ($bad -join ", ") +
        ". Accept the UAC prompt, and make sure Navisworks is closed.")
    exit 1
}
