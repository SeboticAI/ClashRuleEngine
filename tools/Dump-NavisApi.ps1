# Dumps the Navisworks Clash API surface from the installed DLLs - no Navisworks
# session needed. Used to verify the plugin's typed API calls against a given
# version before building/deploying.
#
#   .\Dump-NavisApi.ps1                  # uses 2027
#   .\Dump-NavisApi.ps1 -Version 2026
#
# NOTE: keep this file ASCII-only. PowerShell 5.1 misreads BOM-less UTF-8.
param(
    [string]$Version = "2027",
    [string]$NavisPath = ""
)

if (-not $NavisPath) { $NavisPath = "C:\Program Files\Autodesk\Navisworks Manage $Version" }
if (-not (Test-Path "$NavisPath\Autodesk.Navisworks.Api.dll")) {
    Write-Error "Navisworks API not found at $NavisPath"
    exit 1
}

# Resolve dependent Autodesk assemblies from the install folder
$resolver = [System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    $candidate = Join-Path $NavisPath "$name.dll"
    if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFile($candidate) }
    return $null
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)

$api   = [System.Reflection.Assembly]::LoadFile("$NavisPath\Autodesk.Navisworks.Api.dll")
$clash = [System.Reflection.Assembly]::LoadFile("$NavisPath\Autodesk.Navisworks.Clash.dll")

$out = New-Object System.Collections.Generic.List[string]
$out.Add("Navisworks API dump - $NavisPath")
$out.Add("Api: $($api.FullName)")
$out.Add("Clash: $($clash.FullName)")
$out.Add("")

function Dump-Type {
    param($Assembly, [string]$TypeName, [string[]]$MethodFilter)
    $t = $Assembly.GetType($TypeName)
    $out.Add("======================================================================")
    if ($null -eq $t) { $out.Add("$TypeName  <NOT FOUND>") } else { $out.Add($TypeName) }
    $out.Add("======================================================================")
    if ($null -eq $t) { $out.Add(""); return }

    $out.Add("-- Constructors --")
    foreach ($c in $t.GetConstructors()) {
        $sig = ($c.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        $out.Add("  .ctor($sig)")
    }
    $out.Add("-- Properties --")
    foreach ($p in ($t.GetProperties() | Sort-Object Name)) {
        $rw = ""
        if ($p.CanRead) { $rw += "R" }
        if ($p.CanWrite -and $p.GetSetMethod()) { $rw += "W" }
        $out.Add("  [$rw] $($p.PropertyType.Name) $($p.Name)")
    }
    $out.Add("-- Methods --")
    foreach ($m in ($t.GetMethods() | Where-Object { -not $_.IsSpecialName } | Sort-Object Name)) {
        if ($MethodFilter) {
            $match = $false
            foreach ($f in $MethodFilter) { if ($m.Name -like $f) { $match = $true; break } }
            if (-not $match) { continue }
        }
        $sig = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        $out.Add("  $($m.ReturnType.Name) $($m.Name)($sig)")
    }
    $out.Add("")
}

# The exact types/members ClashProcessingService relies on:
Dump-Type $clash "Autodesk.Navisworks.Api.Clash.DocumentClashTests" @("Tests*")
Dump-Type $clash "Autodesk.Navisworks.Api.Clash.ClashResult"
Dump-Type $clash "Autodesk.Navisworks.Api.Clash.ClashResultGroup"
Dump-Type $clash "Autodesk.Navisworks.Api.Clash.ClashTest"
Dump-Type $api   "Autodesk.Navisworks.Api.SavedItem"   @("CreateCopy", "Clone")
Dump-Type $api   "Autodesk.Navisworks.Api.GroupItem"
Dump-Type $api   "Autodesk.Navisworks.Api.SavedItemCollection"

$dest = Join-Path $PSScriptRoot "navis-api-$Version.txt"
$out | Set-Content -Path $dest -Encoding utf8
Write-Host "Written: $dest ($($out.Count) lines)"

# Quick verdict on the members the plugin's write-path needs:
$txt = $out -join "`n"
$checks = @(
    @{ Name = "DocumentClashTests.TestsEditTestFromCustom"; Pattern = "TestsEditTestFromCustom" },
    @{ Name = "SavedItem.CreateCopy";              Pattern = "CreateCopy" },
    @{ Name = "ClashResultGroup default ctor";     Pattern = "\.ctor\(\)" },
    @{ Name = "ClashResult.Description settable";  Pattern = "\[RW\] String Description" },
    @{ Name = "ClashResult.Status settable";       Pattern = "\[RW\] ClashResultStatus Status" },
    @{ Name = "ClashResult.AssignedTo";            Pattern = "AssignedTo" },
    @{ Name = "ClashResult.Center";                Pattern = "Point3D Center" },
    @{ Name = "ClashResultGroup.DisplayName settable"; Pattern = "\[RW\] String DisplayName" }
)
Write-Host ""
Write-Host "Write-path member checks:"
foreach ($c in $checks) {
    if ($txt -match $c.Pattern) { $mark = "OK" } else { $mark = "??" }
    Write-Host ("  [{0}] {1}" -f $mark, $c.Name)
}
