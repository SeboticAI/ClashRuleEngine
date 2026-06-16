# Batch-learn how clashes are assigned across many coordinated NWDs.
# Builds the plugin + the Automation driver, then opens every NWD under a folder
# headlessly and extracts element-kind-vs-assignee records into one JSONL dataset.
#
#   .\tools\run-batch-extract.ps1 -NwdFolder "U:\path\to\nwds" [-Output "...\clash_kinds.jsonl"]
#
# IMPORTANT:
#   - CLOSE Navisworks first. The Automation API launches its own Roamer instance;
#     a running instance can conflict. It also consumes a Navisworks Manage licence.
#   - This is a one-off LEARNING run over your 200 coordinated models. The output
#     JSONL is what you feed to Claude to derive the element-kind rule hierarchy.
# ASCII-only (PowerShell 5.1).

param(
    [Parameter(Mandatory = $true)][string]$NwdFolder,
    [string]$Output = "$env:USERPROFILE\Desktop\clash_kinds.jsonl",
    [string]$Version = "2027"
)

$ErrorActionPreference = "Stop"
$tools = $PSScriptRoot
$root  = Split-Path $tools -Parent
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { throw "MSBuild not found: $msbuild" }

Write-Host "Building plugin (extractor lives inside it)..."
& $msbuild (Join-Path $root "ClashRuleEngine.csproj") /p:Configuration=Release /p:Platform=x64 /p:NavisworksVersion=$Version /v:minimal /nologo
if (-not $?) { throw "Plugin build failed" }

Write-Host "Building Automation driver..."
& $msbuild (Join-Path $tools "BatchExtractor\BatchExtractor.csproj") /p:Configuration=Release /v:minimal /nologo
if (-not $?) { throw "Driver build failed" }

$plugin = Join-Path $root "bin\x64\Release\$Version\ClashRuleEngine.dll"
$exe    = Join-Path $tools "BatchExtractor\bin\Release\BatchExtractor.exe"
if (-not (Test-Path $plugin)) { throw "Plugin DLL not found: $plugin" }
if (-not (Test-Path $exe))    { throw "Driver exe not found: $exe" }

$nw = Get-Process -Name Roamer -ErrorAction SilentlyContinue
if ($nw) { Write-Warning "Navisworks (Roamer) is running. Close it before the Automation run to avoid conflicts." }

Write-Host "Extracting from: $NwdFolder"
Write-Host "Output: $Output"
& $exe $NwdFolder $Output $plugin

if (Test-Path $Output) {
    $lines = (Get-Content $Output | Measure-Object -Line).Lines
    Write-Host "Wrote $lines aggregated record(s) to $Output"
    Write-Host "Next: hand this JSONL to Claude to derive the element-kind rule hierarchy."
} else {
    Write-Warning "No output produced. Check that the NWDs contain clash tests with assignments."
}
