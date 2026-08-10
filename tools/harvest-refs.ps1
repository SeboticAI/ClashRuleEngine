# Harvests a Navisworks version's API reference DLLs into Refs\<year>\ so this repo can
# build for a Navisworks version that is NOT installed on the build machine.
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
#   .\harvest-refs.ps1                       # every installed version found locally
#   .\harvest-refs.ps1 -Version 2026         # just 2026, from a local install
#   .\harvest-refs.ps1 -Version 2025 -From "\\build01\c$\Program Files\Autodesk\Navisworks Manage 2025"
#   .\harvest-refs.ps1 -List                 # show what is installed / already harvested
#
# WHY THIS EXISTS
#   The Navisworks API assemblies are strong-named and version-stamped PER RELEASE
#   (NW 2024 = 21.0.0.0, 2025 = 22.0, 2026 = 23.0, 2027 = 24.0, all with
#   PublicKeyToken d85e58fa5af9b484). roamer.exe.config pins each to its own release
#   with publisherPolicy apply="no", so a plugin compiled against 24.0 CANNOT load in
#   2026 - there is no binding redirect that would make one DLL span versions. Every
#   Navisworks release therefore needs its own build, and every build needs that
#   release's reference DLLs.
#
#   Run this on a machine that HAS the version (or point -From at a copy of its install
#   folder / a network path), then commit nothing: Refs\ is gitignored, because these
#   are Autodesk's redistributable-restricted binaries. Keep them on the build machine.
param(
    [string]$Version,
    [string]$From,
    [switch]$List
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$refsRoot = Join-Path $root "Refs"

# Must match the <Reference> items in ClashRuleEngine.csproj. Only Api + Clash are
# REQUIRED - they are the only two the plugin actually compiles against. Automation and
# Controls are conditional references there, so they are copied when present and skipped
# when not (they are absent from the community NuGet reference packages, for instance).
$RequiredDlls = @(
    "Autodesk.Navisworks.Api.dll",
    "Autodesk.Navisworks.Clash.dll"
)
$OptionalDlls = @(
    "Autodesk.Navisworks.Automation.dll",
    "Autodesk.Navisworks.Controls.dll"
)

$SupportedVersions = @("2024", "2025", "2026", "2027", "2028")

function Get-NavisworksPath([string]$Year) {
    foreach ($hive in @("HKLM:\SOFTWARE\Autodesk\Navisworks Manage $Year",
                        "HKLM:\SOFTWARE\WOW6432Node\Autodesk\Navisworks Manage $Year")) {
        try {
            $p = (Get-ItemProperty -Path $hive -Name InstallDir -ErrorAction Stop).InstallDir
            if ($p -and (Test-Path (Join-Path $p "roamer.exe"))) { return $p.TrimEnd('\') }
        } catch { }
    }
    $d = "C:\Program Files\Autodesk\Navisworks Manage $Year"
    if (Test-Path (Join-Path $d "roamer.exe")) { return $d }
    return $null
}

function Test-Harvested([string]$Year) {
    Test-Path (Join-Path $refsRoot "$Year\Autodesk.Navisworks.Api.dll")
}

if ($List) {
    Write-Host "Navisworks build targets:"
    Write-Host ""
    Write-Host ("  {0,-6} {1,-11} {2,-11} {3}" -f "Year", "Installed", "Refs\", "API assembly version")
    Write-Host ("  {0,-6} {1,-11} {2,-11} {3}" -f "----", "---------", "-----", "--------------------")
    foreach ($v in $SupportedVersions) {
        $inst = Get-NavisworksPath $v
        $har = Test-Harvested $v
        $ver = "-"
        $probe = if ($har) { Join-Path $refsRoot "$v\Autodesk.Navisworks.Api.dll" }
                 elseif ($inst) { Join-Path $inst "Autodesk.Navisworks.Api.dll" }
                 else { $null }
        if ($probe -and (Test-Path $probe)) {
            try { $ver = [System.Reflection.AssemblyName]::GetAssemblyName($probe).Version.ToString() } catch { $ver = "?" }
        }
        Write-Host ("  {0,-6} {1,-11} {2,-11} {3}" -f $v,
            $(if ($inst) { "yes" } else { "no" }),
            $(if ($har) { "yes" } else { "no" }), $ver)
    }
    Write-Host ""
    Write-Host "A version is buildable when EITHER column says yes (see tools\build-all.ps1)."
    exit 0
}

# -- Decide what to harvest -------------------------------------------
$targets = @()
if ($Version) {
    $src = if ($From) { $From.TrimEnd('\') } else { Get-NavisworksPath $Version }
    if (-not $src) {
        Write-Error ("Navisworks Manage $Version not found on this machine. Either run this " +
            "script on a machine that has it, or pass -From with the path to a copy of its " +
            "install folder (a network path works).")
        exit 1
    }
    $targets += , @{ Year = $Version; Path = $src }
}
else {
    if ($From) { Write-Error "-From requires -Version (it names one specific install)."; exit 1 }
    foreach ($v in $SupportedVersions) {
        $p = Get-NavisworksPath $v
        if ($p) { $targets += , @{ Year = $v; Path = $p } }
    }
    if ($targets.Count -eq 0) {
        Write-Error "No Navisworks Manage install found on this machine (looked for 2024-2028)."
        exit 1
    }
}

# -- Copy --------------------------------------------------------------
$failed = 0
foreach ($t in $targets) {
    $destDir = Join-Path $refsRoot $t.Year
    Write-Host ""
    Write-Host ("Navisworks Manage {0}" -f $t.Year) -ForegroundColor Cyan
    Write-Host ("  from: {0}" -f $t.Path)

    $missing = $RequiredDlls | Where-Object { -not (Test-Path (Join-Path $t.Path $_)) }
    if ($missing) {
        Write-Warning ("  SKIPPED - missing required API DLLs: " + ($missing -join ", "))
        $failed++
        continue
    }

    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    $copied = 0
    foreach ($d in $RequiredDlls) {
        Copy-Item (Join-Path $t.Path $d) (Join-Path $destDir $d) -Force
        $copied++
    }
    $skippedOptional = @()
    foreach ($d in $OptionalDlls) {
        if (Test-Path (Join-Path $t.Path $d)) {
            Copy-Item (Join-Path $t.Path $d) (Join-Path $destDir $d) -Force
            $copied++
        } else { $skippedOptional += $d }
    }
    $apiVer = [System.Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $destDir "Autodesk.Navisworks.Api.dll")).Version.ToString()
    Write-Host ("  into: Refs\{0}\  ({1} DLLs, API {2})" -f $t.Year, $copied, $apiVer) -ForegroundColor Green

    # A year/API-version mismatch means the source folder is not the version claimed -
    # e.g. -From pointed at the wrong install. Silently building against it would produce
    # a DLL that cannot load in the version it is packaged for.
    $expectedMajor = [int]$t.Year - 2003
    $actualMajor = [int]($apiVer.Split('.')[0])
    if ($actualMajor -ne $expectedMajor) {
        Write-Warning ("  API MAJOR MISMATCH: Navisworks {0} expects {1}.x but these DLLs are {2}.x. " -f `
            $t.Year, $expectedMajor, $actualMajor)
        Write-Warning ("  The source folder is not Navisworks {0}. Refs\{0}\ is now WRONG - delete it." -f $t.Year)
        $failed++
    }
    if ($skippedOptional.Count -gt 0) {
        Write-Host ("        (optional, not present: " + (($skippedOptional | ForEach-Object { $_ -replace '^Autodesk\.Navisworks\.|\.dll$','' }) -join ", ") + ")")
    }
}

Write-Host ""
if ($failed -gt 0) { Write-Warning "$failed target(s) skipped." }
Write-Host "Next: tools\build-all.ps1   (builds every version that is installed or harvested)"
if ($failed -gt 0) { exit 1 }
