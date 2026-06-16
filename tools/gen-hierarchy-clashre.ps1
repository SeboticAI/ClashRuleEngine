# Generates a .clashre import file whose discipline hierarchy was DERIVED from the
# user's actual assignments (00_Z_05_NWF - Copy.summary.json, 2026-06-16).
#
# Precedence (index 0 = highest = never moves):
#   Structure > Security > Mechanical > Electrical > ICT > Hydraulic > Fire
# The LOWER-precedence side of a clash is assigned (it must move). This single
# order reproduces every coordinated assignment in the summary and auto-assigns
# the uncoordinated "x STR" pairs to the responsible service.
#
# Classification is by MODEL FILE NAME tokens (the federation is by-discipline),
# matched case-insensitively against each element's ancestor path + key properties.
#
# v2 (2026-06-16): also adds per-test WITHIN-trade rules derived from the
# assignment fingerprints (tundish, clearance zones, shared hangers, hydraulic
# drainage-vs-retic, etc). Rules run before the hierarchy; see Add-TestRules.
#
# ASCII-only (PowerShell 5.1 misreads BOM-less UTF-8). No AssemblyResolve handler
# (one caused a StackOverflow before) - Models + XmlSerializer need no Navisworks DLLs.

param(
    [string]$OutPath = "C:\Users\sebastianmaciuszko\OneDrive - Oconnorservices\Desktop\00_Z_05_NWF - Copy.nwf.clashre"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dll  = Join-Path $root "bin\x64\Release\2027\ClashRuleEngine.dll"
if (-not (Test-Path $dll)) { throw "Build output not found: $dll" }

[Reflection.Assembly]::LoadFrom($dll) | Out-Null

function New-Disc([string]$name, [string]$color, [string]$assignee, [string]$group, [string]$keywordsCsv) {
    $d = New-Object ClashRuleEngine.Models.DisciplineDefinition
    $d.Name = $name
    $d.Color = $color
    $d.Assignee = $assignee
    $d.GroupName = $group
    $d.KeywordsCsv = $keywordsCsv   # setter splits into Keywords
    return $d
}

$cfg = New-Object ClashRuleEngine.Models.ProjectConfig
$cfg.ProjectName = "00_Z_05 - data-derived discipline hierarchy"
$cfg.UseHierarchyFallback = $true

# Start with NO auto-grouping: clean per-clash assignment, no merged blobs.
# Turn on a grouping mode in the Hierarchy tab once assignments are trusted.
$cfg.GroupingMode = [ClashRuleEngine.Models.ClashGroupingMode]::None
$cfg.AssignByGroup = $false

# Highest precedence (never moves) -> lowest (always moves).
# Keywords are MODEL-FILE tokens ONLY (the federation is strictly by-discipline
# model: 00_Z_05_CON_<disc>.nwc + ...RVT-<disc>-0001.rvt). Bare words like "MECH"
# are deliberately AVOIDED - they false-match property values (e.g. ICT/Security
# cable-tray brackets are in the "Mechanical Equipment" category, which "MECH"
# would wrongly grab). The .nwc/.rvt tokens never appear in element properties.
$cfg.Hierarchy.Disciplines.Clear()
$cfg.Hierarchy.Disciplines.Add((New-Disc "Structure"     "#6B7280" "STR"  "STR"  "CON_STR, RVT-ST-, IFC-SS-, Structural Steel model"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "Security"      "#0EA5E9" "SEC"  "SEC"  "CON_SEC, RVT-SEC-"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "Mechanical"    "#2563EB" "MECH" "MECH" "CON_MECH, RVT-MECH-"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "Electrical"    "#CA8A04" "ELEC" "ELEC" "CON_ELEC, RVT-ELEC-"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "ICT"           "#7C3AED" "ICT"  "ICT"  "CON_ICT, RVT-COMMS-"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "Hydraulic"     "#0891B2" "HYD"  "HYD"  "CON_HYD, RVT-HYD-"))
$cfg.Hierarchy.Disciplines.Add((New-Disc "Fire"          "#DC2626" "FIRE" "FIRE" "CON_FIRE, RVT-FIRE-"))

# Make the assignee/group dropdowns aware of the manual sub-buckets too (spatial,
# not auto-assignable - kept here so they're one click away when refining by hand).
foreach ($a in @("STR","SEC","MECH","ELEC","ICT","HYD","FIRE",
                 "MECH - CLEARANCE","FIRE - CLEARANCE","ICT - CLEARANCE","HYD - CLEARANCE",
                 "ELEC - CLEARANCE","TUNDISH","SHARED HANGERS")) {
    $cfg.Assignees.Add($a)
    $cfg.GroupNames.Add($a)
}

# ---------------------------------------------------------------------------
# Within-trade rules derived from the assignment fingerprints (summary v3).
# Rules run FIRST (first-match-wins); whatever no rule catches falls through to
# the discipline hierarchy above. ClashStatus is left blank so running rules
# NEVER changes a clash's Approved/Active status - it only sets assignee+group.
# Tree-Path conditions match element type words in the model tree (the clashing
# node itself is a bare "Solid"). OR logic: any listed word matches.
# ---------------------------------------------------------------------------
$RULE_COLOR = "#7C3AED"

function New-TreeRule([string]$name, [string]$assignee, [string]$group, [string[]]$contains) {
    $r = New-Object ClashRuleEngine.Models.ClashRule
    $r.Name = $name
    $r.Assignee = $assignee
    $r.GroupName = $group
    $r.Color = $RULE_COLOR
    $r.AssigneeMode = [ClashRuleEngine.Models.AssigneeMode]::Named
    $r.ConditionLogic = [ClashRuleEngine.Models.LogicOperator]::Or
    $r.ClashStatus = ""   # leave clash status untouched
    foreach ($v in $contains) {
        $c = New-Object ClashRuleEngine.Models.RuleCondition
        $c.PropertyCategory = "Tree"
        $c.PropertyName = "Path"
        $c.Operator = [ClashRuleEngine.Models.ConditionOperator]::Contains
        $c.Value = $v
        $c.Target = [ClashRuleEngine.Models.ClashItemTarget]::Either
        $r.Conditions.Add($c)
    }
    return $r
}

function Add-TestRules([string]$testName, [object[]]$rules) {
    $set = $cfg.GetOrCreateTestRuleSet($testName)
    foreach ($r in $rules) { $set.Rules.Add($r) }
    $set.ReindexPriorities()
}

Add-TestRules "_ELEC vs _MECH" @(
    (New-TreeRule "MECH access/clearance zone" "MECH" "MECH - CLEARANCE" @("Clearance Zone_Generic_BMAIFM")),
    (New-TreeRule "Shared cable-tray hangers"  "SHARED HANGERS" "SHARED HANGERS" @("Cable Tray Saddle"))
)

Add-TestRules "_HYD vs _MECH" @(
    (New-TreeRule "Tundish"                    "TUNDISH" "TUNDISH" @("Tundish")),
    (New-TreeRule "MECH access/clearance zone" "MECH" "MECH - CLEARANCE" @("Clearance Zone_Generic_BMAIFM")),
    (New-TreeRule "Hyd drainage routes around MECH" "MECH" "MECH" @("Waste Drain", "Vent"))
)

Add-TestRules "_FIRE vs _HYD" @(
    (New-TreeRule "Hydraulic water retic stays HYD" "HYD" "HYD" @("Cold Water", "Hot Water"))
)

Add-TestRules "_ICT vs _SEC" @(
    (New-TreeRule "Shared cable-tray hangers/brackets" "SHARED HANGERS" "SHARED HANGERS" `
        @("Cable Tray", "Bracket", "Unistrut", "Hanging Rod", "Hanger"))
)

# Tentative - flag for live validation (~50/50 split, weaker signal)
Add-TestRules "_FIRE vs _ICT" @(
    (New-TreeRule "ICT cable-tray bracket moves" "ICT" "ICT" @("DS-R20-E-ME"))
)

Add-TestRules "_ELEC vs _SEC" @(
    (New-TreeRule "Light fittings" "ELEC - LIGHT" "ELEC - LIGHT" @("Batten", "Lighting Fixture"))
)

$xml = $cfg.ToXml()
[System.IO.File]::WriteAllText($OutPath, $xml, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote: $OutPath"
Write-Host ("Disciplines: " + (($cfg.Hierarchy.Disciplines | ForEach-Object { $_.Name }) -join " > "))

# Round-trip sanity: reload and confirm the hierarchy survived.
$reload = [ClashRuleEngine.Models.ProjectConfig]::FromXml([System.IO.File]::ReadAllText($OutPath))
Write-Host ("Reloaded OK - fallback=" + $reload.UseHierarchyFallback + ", disciplines=" + $reload.Hierarchy.Disciplines.Count)
