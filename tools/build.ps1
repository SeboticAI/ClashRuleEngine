# One-shot: verify API surface -> build -> deploy.
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
#   .\build.ps1                  # 2027, Release, deploy bundle
#   .\build.ps1 -Version 2026    # needs Refs\2026\ API DLLs if 2026 isn't installed
#   .\build.ps1 -NoDeploy
param(
    [string]$Version = "2027",
    [string]$Configuration = "Release",
    [switch]$NoDeploy,
    [switch]$SkipApiCheck
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

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
    Write-Host ""
    Write-Host "Then re-run this script."
    exit 1
}
Write-Host "MSBuild: $msbuild"

# -- 2. Offline API surface check (warn-only; the compile is the real gate) --
if (-not $SkipApiCheck) {
    try { & (Join-Path $PSScriptRoot "Dump-NavisApi.ps1") -Version $Version }
    catch { Write-Warning "API dump failed (non-fatal): $_" }
}

# -- 3. Build ---------------------------------------------------------
& $msbuild (Join-Path $root "ClashRuleEngine.csproj") `
    /p:Configuration=$Configuration /p:Platform=x64 /p:NavisworksVersion=$Version `
    /v:minimal /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build FAILED (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Host ""
Write-Host "Build OK: bin\x64\$Configuration\$Version\ClashRuleEngine.dll" -ForegroundColor Green

# -- 4. Deploy --------------------------------------------------------
if (-not $NoDeploy) {
    & (Join-Path $PSScriptRoot "deploy.ps1") -Version $Version -Configuration $Configuration
}
