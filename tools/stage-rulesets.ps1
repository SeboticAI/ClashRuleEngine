# Refreshes the rule sets EMBEDDED in the plugin (Resources\RuleSets\), which is what a new
# user gets on first run and what the "Rule set" picker switches between.
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
#
#   .\stage-rulesets.ps1                                  # from the live config + newest mined json
#   .\stage-rulesets.ps1 -Current path\to\some.clashre
#   .\stage-rulesets.ps1 -Mined  path\to\mined.json
#
# Outputs (LogicalNames are pinned in ClashRuleEngine.csproj - do not rename these):
#   Resources\RuleSets\current.clashre  -> ClashRuleEngine.RuleSets.current.clashre
#   Resources\RuleSets\mined.json       -> ClashRuleEngine.RuleSets.mined.json
# and a matching distributable copy under rules\ for anyone who wants the file itself.
#
# WHY THE SCRUB: ProjectConfig has an ApiKey field, so a .clashre exported from a machine
# where the Claude key was entered would carry that key INTO THE SHIPPED DLL and into git.
# This script always blanks it. Never hand-copy a .clashre into Resources\RuleSets\.
#
# WHY UTF-8: ProjectConfig.ToXml uses XmlSerializer over a StringWriter, which declares
# encoding="utf-16"; saving that with XmlDocument.Save yields a UTF-16 file, doubling the
# embedded size for no benefit. The declaration is cosmetic here (we always load via
# File.ReadAllText -> StringReader), so it is rewritten as UTF-8.
param(
    [string]$Current,
    [string]$Mined
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "Resources\RuleSets"
$rulesDir = Join-Path $root "rules"

if (-not $Current) { $Current = Join-Path $env:APPDATA "ClashRuleEngine\config.clashre" }
if (-not $Mined) {
    $Mined = Join-Path ([Environment]::GetFolderPath('UserProfile')) `
        "OneDrive - Oconnorservices\Desktop\clashre_kind_rules.generated.json"
}

foreach ($p in @($Current, $Mined)) {
    if (-not (Test-Path $p)) { Write-Error "Source not found: $p"; exit 1 }
}
New-Item -ItemType Directory -Force -Path $dir, $rulesDir | Out-Null

# -- "current": the production config, ApiKey scrubbed, written as UTF-8 ----
[xml]$x = Get-Content $Current
$key = $x.SelectSingleNode("//ApiKey")
$hadKey = ($key -ne $null) -and (-not [string]::IsNullOrWhiteSpace($key.InnerText))
if ($key) { $key.InnerText = "" }

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
foreach ($target in @((Join-Path $dir "current.clashre"),
                      (Join-Path $rulesDir "clashre-config-live.clashre"))) {
    $w = [System.Xml.XmlWriter]::Create($target, $settings)
    try { $x.Save($w) } finally { $w.Dispose() }
}

$ruleCount = $x.SelectNodes("//ClashRule").Count
$setCount = $x.SelectNodes("//TestRuleSet").Count
$defaults = @($x.SelectNodes("//TestRuleSet") |
    Where-Object { $_.DefaultAssignee -and $_.DefaultAssignee.Trim() }).Count
$minGap = $x.SelectSingleNode("//ApprovePolicy/MinGapMm")

Write-Host "current.clashre" -ForegroundColor Cyan
Write-Host ("  from      : {0}" -f $Current)
Write-Host ("  rules     : {0} across {1} test set(s), {2} per-test default(s)" -f $ruleCount, $setCount, $defaults)
Write-Host ("  approve   : >= {0} mm" -f $(if ($minGap) { $minGap.InnerText } else { "?" }))
if ($hadKey) { Write-Host "  ApiKey    : PRESENT IN SOURCE - scrubbed from both copies" -ForegroundColor Yellow }
else { Write-Host "  ApiKey    : none in source" }

# -- "mined": analyzer output, no secrets ----------------------------------
Copy-Item $Mined (Join-Path $dir "mined.json") -Force
Copy-Item $Mined (Join-Path $rulesDir "clashre-kind-rules-mined.json") -Force
$m = Get-Content $Mined -Raw | ConvertFrom-Json
Write-Host "mined.json" -ForegroundColor Cyan
Write-Host ("  from      : {0}" -f $Mined)
Write-Host ("  rules     : {0} pair rule(s), {1} per-test default(s)" -f @($m.testRules).Count, @($m.tests).Count)
Write-Host ("  approve   : >= {0} mm + {1} per-pair floor(s)" -f $m.approve.minGapMm, @($m.approve.pairFloors).Count)
if ($m._confidence) { Write-Host ("  replay    : {0}% of past specific calls" -f $m._confidence.reproducedPctOverall) }

Write-Host ""
Write-Host "Staged. Rebuild so the new content is embedded: tools\build-all.ps1 -Installer"
