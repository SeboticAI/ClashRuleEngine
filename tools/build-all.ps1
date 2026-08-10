# Builds ClashRuleEngine for EVERY Navisworks version this machine can target, then
# (optionally) compiles the installer that bundles them.
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
#   .\build-all.ps1                 # build every buildable version
#   .\build-all.ps1 -Installer      # ...then compile Installer\ClashRuleEngine.iss
#   .\build-all.ps1 -Versions 2026,2027
#
# A version is buildable when its API reference DLLs are reachable, i.e. EITHER
# Navisworks Manage <year> is installed locally OR Refs\<year>\ has been populated by
# tools\harvest-refs.ps1. Versions that are not buildable are REPORTED AND SKIPPED, not
# silently dropped - the installer packages only the versions that actually built, so a
# silent skip would ship a setup.exe missing a version nobody noticed.
#
# One build per version is unavoidable: the Navisworks API assemblies are strong-named
# per release (2024 = 21.0.0.0 ... 2027 = 24.0.0.0) and roamer.exe.config pins each with
# publisherPolicy apply="no". See tools\harvest-refs.ps1 for the detail.
param(
    [string[]]$Versions,
    [string]$Configuration = "Release",
    [switch]$Installer,
    [switch]$SkipIcons
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$SupportedVersions = @("2024", "2025", "2026", "2027", "2028")

# -- 1. Locate MSBuild ------------------------------------------------
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
        -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
}
if (-not $msbuild) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($edition in @("Community", "Professional", "Enterprise")) {
        $candidates += "$env:ProgramFiles\Microsoft Visual Studio\2022\$edition\MSBuild\Current\Bin\MSBuild.exe"
    }
    $msbuild = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $msbuild) {
    Write-Host "MSBuild not found. Install VS 2022 Build Tools (one command, ~5 min):" -ForegroundColor Yellow
    Write-Host ""
    Write-Host '  winget install --id Microsoft.VisualStudio.2022.BuildTools --accept-package-agreements --accept-source-agreements --override "--add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.Net.Component.4.8.TargetingPack --includeRecommended --quiet --norestart --wait"'
    exit 1
}
Write-Host "MSBuild: $msbuild"

# -- 2. Icons (shipped next to the DLL; the csproj errors if they are missing) --
if (-not $SkipIcons) {
    & (Join-Path $PSScriptRoot "make-icons.ps1") | Out-Null
    Write-Host "Icons: regenerated from Resources\oconnors_logo.png"
}

# -- 3. Work out which versions are buildable --------------------------
# Mirrors the NavisworksPath resolution order in ClashRuleEngine.csproj: harvested
# Refs\<year>\ wins over a local install, so a pinned copy beats whatever is on the box.
function Get-RefSource([string]$Year) {
    $harvested = Join-Path $root "Refs\$Year"
    if (Test-Path (Join-Path $harvested "Autodesk.Navisworks.Api.dll")) {
        return @{ Label = "Refs\$Year"; Dir = $harvested }
    }
    $installed = "C:\Program Files\Autodesk\Navisworks Manage $Year"
    if (Test-Path (Join-Path $installed "Autodesk.Navisworks.Api.dll")) {
        return @{ Label = "installed"; Dir = $installed }
    }
    return $null
}

$wanted = if ($Versions) { $Versions } else { $SupportedVersions }
$buildable = @()
$skipped = @()
foreach ($v in $wanted) {
    $src = Get-RefSource $v
    if ($src) { $buildable += , @{ Year = $v; Refs = $src.Label; RefDir = $src.Dir } }
    else { $skipped += $v }
}

Write-Host ""
if ($buildable.Count -eq 0) {
    Write-Error ("Nothing to build. No Navisworks install and no Refs\<year>\ for: " +
        ($wanted -join ", ") + ". Run tools\harvest-refs.ps1 -List to see the state, then " +
        "harvest the API DLLs on a machine that has the version.")
    exit 1
}
Write-Host ("Building: " + (($buildable | ForEach-Object { "$($_.Year) [$($_.Refs)]" }) -join ", "))
if ($skipped.Count -gt 0) {
    Write-Warning ("NOT buildable, skipped: " + ($skipped -join ", ") +
        " - no local install and no Refs\<year>\. Run: tools\harvest-refs.ps1 -Version <year>")
}

# -- 4. Build each -----------------------------------------------------
$results = @()
foreach ($b in $buildable) {
    Write-Host ""
    Write-Host ("=== Navisworks $($b.Year) ===") -ForegroundColor Cyan
    & $msbuild (Join-Path $root "ClashRuleEngine.csproj") `
        /p:Configuration=$Configuration /p:Platform=x64 /p:NavisworksVersion=$($b.Year) `
        /v:minimal /nologo
    $ok = ($LASTEXITCODE -eq 0)
    $dll = Join-Path $root "bin\x64\$Configuration\$($b.Year)\ClashRuleEngine.dll"
    if ($ok -and -not (Test-Path $dll)) { $ok = $false }
    $results += , @{ Year = $b.Year; Ok = $ok; Dll = $dll; RefDir = $b.RefDir }
    if ($ok) { Write-Host "  OK" -ForegroundColor Green } else { Write-Host "  FAILED" -ForegroundColor Red }
}

# -- 5. Summary --------------------------------------------------------
Write-Host ""
Write-Host "Build summary" -ForegroundColor Cyan
foreach ($r in $results) {
    if ($r.Ok) {
        # Report the API version compiled against by reading the REFERENCE DLL, not the
        # output. Loading four ClashRuleEngine.dll builds for reflection in one process
        # fails - they all share the identity "ClashRuleEngine, Version=0.0.0.0", and the
        # CLR refuses the same identity from a second location.
        $apiVer = "?"
        try {
            $apiVer = [System.Reflection.AssemblyName]::GetAssemblyName(
                (Join-Path $r.RefDir "Autodesk.Navisworks.Api.dll")).Version.ToString()
        } catch { }
        Write-Host ("  {0}  OK      -> bin\x64\{1}\{0}\ClashRuleEngine.dll  (API {2})" -f `
            $r.Year, $Configuration, $apiVer) -ForegroundColor Green
    } else {
        Write-Host ("  {0}  FAILED" -f $r.Year) -ForegroundColor Red
    }
}
foreach ($s in $skipped) { Write-Host ("  {0}  skipped - no refs" -f $s) -ForegroundColor DarkYellow }

$failedCount = @($results | Where-Object { -not $_.Ok }).Count
$okCount = @($results | Where-Object { $_.Ok }).Count

# -- 6. Installer ------------------------------------------------------
if ($Installer) {
    Write-Host ""
    # Inno Setup 6.3+ installs PER USER by default (winget runs it unelevated), so the
    # per-user path is checked first and the uninstall registry key is the fallback -
    # probing only Program Files finds nothing on a winget-installed machine.
    $isccCandidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($key in @("HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*",
                       "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*",
                       "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*")) {
        Get-ItemProperty $key -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like "Inno Setup*" -and $_.InstallLocation } |
            ForEach-Object { $isccCandidates += (Join-Path $_.InstallLocation "ISCC.exe") }
    }
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Host "Inno Setup 6 not found. Install it, then re-run with -Installer:" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  winget install --id JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements"
        Write-Host ""
        Write-Host "(Skipping installer; the per-version DLLs above are built and usable.)"
    } else {
        Write-Host "Compiling installer with: $iscc" -ForegroundColor Cyan
        # The .iss packages each version with skipifsourcedoesntexist, so it bundles
        # exactly the builds that succeeded above.
        & $iscc (Join-Path $root "Installer\ClashRuleEngine.iss")
        if ($LASTEXITCODE -ne 0) { Write-Error "Installer compile FAILED (exit $LASTEXITCODE)."; exit 1 }
        Write-Host ""
        Write-Host ("Installer bundles {0} Navisworks version(s): " -f $okCount) -ForegroundColor Green
        ($results | Where-Object { $_.Ok } | ForEach-Object { "  $($_.Year)" })
        Get-ChildItem (Join-Path $root "Installer\Output") -Filter *.exe |
            ForEach-Object { Write-Host ("  -> {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
    }
}

if ($failedCount -gt 0) { exit 1 }
