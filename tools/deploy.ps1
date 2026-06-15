# Deploys the built plugin for a given Navisworks version.
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
# Requires elevation (Program Files) - the script self-elevates with a UAC prompt.
param(
    [string]$Version = "2027",
    [string]$Configuration = "Release"
)

$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root "bin\x64\$Configuration\$Version\ClashRuleEngine.dll"
if (-not (Test-Path $dll)) {
    Write-Error "Build output not found: $dll - build first (tools\build.ps1 -Version $Version)"
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
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item $dll (Join-Path $dest "ClashRuleEngine.dll") -Force
}

# Verify by CONTENT, not mere existence: a cancelled UAC prompt or a locked DLL
# (Navisworks still open) leaves a STALE copy in place, which a Test-Path check
# would wrongly report as success.
$destDll = Join-Path $dest "ClashRuleEngine.dll"
$srcHash  = (Get-FileHash $dll).Hash
$destHash = if (Test-Path $destDll) { (Get-FileHash $destDll).Hash } else { "" }

if ($srcHash -eq $destHash) {
    Write-Host "Deployed and verified: $destDll"
    Write-Host "Restart Navisworks, then: View -> Windows -> Clash Rule Engine"
} else {
    Write-Error ("Deploy FAILED - $destDll does not match the build. " +
        "The copy did not land: accept the UAC prompt, and close Navisworks first (a running instance locks the DLL).")
    exit 1
}
