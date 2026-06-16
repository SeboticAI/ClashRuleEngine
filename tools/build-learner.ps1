# Builds the NWD Clash Learner desktop app into a self-contained folder you can
# just double-click: the GUI exe + the extractor plugin DLL beside it.
# ASCII-only (PowerShell 5.1).
param([string]$Version = "2027")

$ErrorActionPreference = "Stop"
$tools = $PSScriptRoot
$root  = Split-Path $tools -Parent
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }

Write-Host "Building extractor plugin..."
& $msbuild (Join-Path $root "ClashRuleEngine.csproj") /p:Configuration=Release /p:Platform=x64 /p:NavisworksVersion=$Version /v:minimal /nologo
if (-not $?) { throw "Plugin build failed" }

Write-Host "Building NWD Clash Learner GUI..."
& $msbuild (Join-Path $tools "NwdClashLearner\NwdClashLearner.csproj") /p:Configuration=Release /v:minimal /nologo
if (-not $?) { throw "GUI build failed" }

$exeDir = Join-Path $tools "NwdClashLearner\bin\Release"
$plugin = Join-Path $root "bin\x64\Release\$Version\ClashRuleEngine.dll"
if (-not (Test-Path $plugin)) { throw "Plugin DLL not found: $plugin" }

# Drop the extractor plugin next to the exe so it's auto-located.
Copy-Item $plugin (Join-Path $exeDir "ClashRuleEngine.dll") -Force

$exe = Join-Path $exeDir "NwdClashLearner.exe"
Write-Host ""
Write-Host "READY: $exe"
Write-Host "Close Navisworks, run the exe, drag your NWDs in, click Generate Summary."
