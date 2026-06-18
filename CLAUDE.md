# CLAUDE.md — Project Context for Claude Code

## Project overview
This is a **Navisworks Manage** dockable panel plugin (C# / WPF / .NET Framework 4.8) for BIM coordination clash management. It assigns and groups clash-detection results using **per-test element-pair rules** learned from historically-coordinated models, plus an **auto-approve engine** (clearance-based) and grid-aware grouping.

Builds are per-Navisworks-version (`/p:NavisworksVersion=2027`, default = newest installed). **This machine has Navisworks Manage 2027** (2026 was removed in the 2026-04 upgrade cycle); older-version builds need that version's API DLLs dropped in `Refs\<version>\`.

The business context: we're building a product to help companies automate BIM coordination workflows. **Direction (2026-06):** assignment is driven by the *actual clashing elements* (category/family/size) per clash test, learned from ~28+ coordinated NWDs — NOT by a fixed discipline hierarchy (that system was removed). "In test _ELEC vs _HYD, a Cable Tray Fitting vs a Pipe → ELEC." Ambiguous element pairs are left unassigned for human review rather than guessed.

## Architecture

### Project structure
```
ClashRuleEngine/
├── Models/
│   ├── ClashRule.cs              # One rule: conditions (Item-A/B/Either), group, assignee, priority
│   ├── RuleCondition.cs          # Individual condition (category.property operator value, per-side target)
│   ├── KindRule.cs               # Single-element kind rule (keywords + size band → owner/other/named); largely unused now
│   ├── ApprovePolicy.cs          # Auto-approve engine: min clearance gap, per-pair floors, never-penetration/structure
│   └── TestRuleSet.cs            # Per-test rules + ProjectConfig + ClashGroupingMode enum
├── Services/
│   ├── ClashProcessingService.cs # assign (rules) → approve → group (mode-aware) → atomic write-back
│   ├── ClashApiCompat.cs         # Version compat for the 2027 TestsRoot folder tree
│   ├── ClashNavigationService.cs # Resolve-by-GUID navigate/select/frame (stale-ref-safe)
│   ├── ClashMarkerService.cs     # Shared state + drawing for the 3D marker overlay
│   ├── ClashTestScanner.cs       # Discovers clash tests/results from the NW document
│   ├── ElementKind.cs            # Computes an element's kind (cat/family/type/system + diameter mm); shared by extractor + engine
│   ├── KindRuleImport.cs         # Parses the clashre-kind-rules/1 JSON → testRules (element-pair ClashRules), approve policy
│   ├── ModelPropertyScanner.cs   # Scans model for available properties (for dropdowns)
│   ├── SessionExportService.cs   # Full session export + lean per-test assignment summary
│   ├── AiRuleGenerator.cs / ClaudeApiService.cs # AI rule authoring (raw-HTTP, opus-4-8)
│   └── RulePersistenceService.cs # Saves/loads ProjectConfig as ONE global .clashre (%AppData%\ClashRuleEngine)
├── UI/
│   ├── Converters.cs             # WPF value converters
│   ├── RuleEditorDialog.xaml/.cs # Rule creation/editing dialog (Named assignee)
│   ├── ClashInspectorDialog.xaml/.cs # Side-by-side Item A/B property inspector
│   ├── ClashMatrixDialog.cs      # Code-only test-pair matrix view
│   ├── ExportProgressWindow.cs   # Streaming-export progress + cancel
│   ├── AiAssistDialog.xaml/.cs   # AI rule-generation dialog
│   └── RuleEnginePanel.xaml/.cs  # Main panel: 3 tabs (Rules/Clashes/General); grouping is a GLOBAL bar above the tabs (applies to all tests). Light theme.
├── Plugin/
│   ├── ClashRuleEnginePlugin.cs  # DockPanePlugin + RibbonTab handler
│   ├── ClashMarkerPlugins.cs     # RenderPlugin (overlay) + InputPlugin (click-to-select)
│   ├── BatchClashExtractPlugin.cs# Headless AddInPlugin: extracts per-clash kind/assignee/status/gap/grid/level → JSONL (the learning data)
│   └── ClashRuleEngineRibbon.xaml # Ribbon layout (embedded resource)
├── tools/
│   ├── BatchExtractor/           # Automation console driver for BatchClashExtractPlugin
│   └── NwdClashLearner/          # WinForms GUI: pick NWDs, run the extractor (uses the DEPLOYED plugin)
├── Installer/
│   └── ClashRuleEngine.iss       # Inno Setup installer script
├── PackageContents.xml           # Navisworks plugin manifest
└── ClashRuleEngine.csproj        # Classic-style .NET 4.8 project (NOT SDK-style)
```

### Key design decisions
- **Per-test element-pair rules** (CURRENT model): each clash test has its own ordered `ClashRule` list in `ProjectConfig.TestRuleSets`. An element-pair rule = a `ClashRule` with `And` logic + two `Either`-target "Category contains" conditions → matches a clash where one element is category A and the other is category B (unordered), assigned to a Named trade. Imported from a `clashre-kind-rules/1` JSON's `"testRules":[{test,a,b,assign}]` block via `KindRuleImport`. Built from mining ~28 coordinated NWDs (`clash_kinds.jsonl`). Ambiguous pairs are intentionally omitted → left unassigned.
- **Approve engine** (`ApprovePolicy` / `ClashProcessingService.ApproveWithinTolerance`): after assignment, auto-set Status=Approved. Two paths: (1) **always-approve** (gap-independent) — `ApproveKinds` (element-kind keywords, e.g. Flex Pipe 91% / Flex Duct 94% approved → approved even on a hard clash, they bend) and `ApproveAssignees` (e.g. TUNDISH 90% approved); (2) **clearance-gated** — gap ≥ a per-pair floor (default ≥50 mm). Hard gates: penetrations never approved by the gap path; `_X vs _STR` (structure) never approved (test-name guard). All learned from the data. (We do NOT set ApprovedBy — it caused a confusing "Approved by: varies" group rollup; Status only.)
- **NO discipline/system hierarchy** — that whole responsibility system (SystemHierarchy, DisciplineClassifier, owner/other resolution, Hierarchy tab) was REMOVED 2026-06-17. Assignment comes only from per-test rules now.
- **Pipeline order**: assign-per-clash (rules) → approve → group → ONE atomic write-back per test. Grouping only organises; it never re-assigns.
- **Grouping** (`ClashGroupingMode`): None / SharedElement / Proximity / **Grid** / Level / ByAssignee / Hybrid. **Grid** is the recommended mode: groups named by the bare grid intersection only (e.g. "H-22" — level stripped, no trade, no count), with " (1)"/" (2)" suffixes when two groups share a grid name (`GroupByGrid`/`GridName`). (`GridTrade` enum value is retained but now routes to the same grid grouping.) `AssignByGroup` (group-then-assign majority) conflicts with per-element specificity — leave OFF.
- **Persistence**: ONE GLOBAL `.clashre` XML at `%AppData%\ClashRuleEngine\config.clashre` (`RulePersistenceService`). It is the single source of truth — an imported rule set survives across files AND Navisworks instances; only a new import (or an edit) overwrites it. (Was a per-document sidecar; changed 2026-06-18 so a learned rule file follows the user, not the model. The API has no reliable document-level user-data store anyway.)
- **Light theme UI**: white cards on `#F8F9FA`, dark `#1A1A2E` text, blue `#2563EB` accent, `#E5E7EB` borders. (A dark-theme attempt was reverted — it produced unreadable light-on-light fields.)
- **Ribbon / naming**: the dock pane (the "app") is **Clash Rule Engine** (DockPanePlugin DisplayName → View→Windows entry + pane title). A custom ribbon tab **OConnors Clash** (CommandHandlerPlugin + RibbonLayout) holds a **Clash Engine** button that opens the pane; an `AddInPlugin` (DisplayName **Clash Engine**) under Tool Add-ins does the same. `ShowPanel()` = `if (rec.LoadedPlugin==null) rec.LoadPlugin()` — `DockPanePluginRecord` exposes ONLY LoadPlugin/IsLoaded (no Unload/Show), and closing a pane unloads it, so LoadPlugin re-opens.

## Navisworks API quirks (IMPORTANT)
These were discovered through trial and error during development (2026/2027 APIs):

0. **Writing to clash results (THE big one — caused the crashes):** attached
   `ClashResult` objects must not be re-inserted with their original GUID — that duplicates
   result GUIDs and **crashes Navisworks** (corrupts Clash Detective state). `TestsEditTestFromCopy`
   is **SETTINGS-ONLY** (rename/selections) and CANNOT swap in regrouped children — using it for
   that was the root cause. The CORRECT, SDK-supported pattern (Autodesk's own ClashGrouper sample,
   `api\...\ClashDetective\ClashGrouper`, NW 2015→2027), now in `ClashProcessingService.WriteBack`:
   1. Flatten the LIVE test into detached `ClashResult` copies, each `(ClashResult)cr.CreateCopy()`
      with `.Guid = Guid.Empty`. Set Status/AssignedTo/Description on the copies (plain sets).
   2. `using (var t = doc.BeginTransaction("…"))` → `newTest = (ClashTest)test.CreateCopyWithoutChildren();`
      → `int i = parent.Children.IndexOf(test); TestsData.TestsReplaceWithCopy(parent, i, newTest);`
      → per group/result `TestsData.TestsAddCopy((GroupItem)parent.Children[i], item)` (TestsAddCopy
      DEEP-copies a group with its children — one call per top-level item) → `t.Commit()`.
   An uncommitted transaction rolls back on dispose, so any failure leaves the document untouched.
   Assignment uses the first-class `clash.AssignedTo = new Assignee(name)` (no reflection — it survives
   CreateCopy). Verified via `tools\Dump-NavisApi.ps1` → `tools\navis-api-2027.txt`:
   `ClashResult.Description/Status/AssignedTo/Center/Guid` are all RW, `AssignedTo` is typed `Assignee`,
   `TestsReplaceWithCopy`/`TestsAddCopy`/`CreateCopyWithoutChildren` all present.

0b. **2027 moved the tests collection**: `DocumentClashTests.Tests` no longer exists.
   2027+ uses `TestsData.Value.TestsRoot` — a `ClashTestFolder` TREE (2027 added clash
   test folders), so tests must be collected recursively. ALL test enumeration goes
   through `ClashApiCompat.GetAllTests()` (typed per-version via the `NW_TESTS_TREE`
   define) — never enumerate `TestsData` directly.
1. **`DockPanePluginRecord.IsVisible`** — does NOT exist. Use `LoadedPlugin != null` to check if loaded.
2. **`DockPanePluginRecord.Enabled`** — does NOT exist. Can't toggle visibility programmatically.
3. **`ModelItemEnumerableCollection.DescendantsAndSelf`** — does NOT exist. Use `model.RootItem.Descendants` instead, iterating through `doc.Models` first.
4. **`ModelItemEnumerableCollection.Descendants`** — does NOT exist on the collection. Must go through individual `Model` objects: `foreach (Model model in doc.Models) foreach (ModelItem item in model.RootItem.Descendants)`.
5. **`ClashResult.ApprovedBy`** — is NOT a string; it's a typed `Assignee` (`[RW]`). Set `clash.ApprovedBy = new Assignee(name)` (same as `AssignedTo`), and `clash.ApprovedTime = DateTime.Now` (`[RW] DateTime?`) so an auto-approval is complete. The approve engine sets all three (Status/ApprovedBy/ApprovedTime) on the detached copy.
6. **`ClashTest.LastRun`** — returns `DateTime?` (nullable), not `DateTime`. Use `ct.LastRun ?? DateTime.MinValue`.
7. **`Document.SetUserString` / `GetUserString`** — do NOT exist. Don't try to store data in the NW document.
8. **`SavedViewpoint.Comment`** — does NOT exist. 
9. **`Autodesk.Navisworks.Api.Data.DataProperty`** — wrong namespace for this purpose.
10. **`CommandHandlerPlugin` with `RibbonLayout`** — works for ribbon tabs. `AddInPlugin` works for simple buttons.

### Project file format
- MUST use **classic-style .csproj** (not SDK-style `Microsoft.NET.Sdk`). SDK-style doesn't resolve Navisworks API references properly.
- MUST explicitly list all `<Compile>` items, `<Page>` XAML items, and `<EmbeddedResource>` for the ribbon XAML.
- Navisworks references: `Private=False` (don't copy to output).
- Platform: `x64` only.
- Ribbon XAML: must be `<EmbeddedResource>`, not `<Page>` or `<None>`.
- **Ribbon XAML resource NAME + format (debugged 2026-06-18 — why the custom tab never showed):**
  1. The embedded-resource name MUST be exactly `<RootNamespace>.<RibbonLayout filename>` =
     `ClashRuleEngine.ClashRuleEngineRibbon.xaml`. Because the file lives in `Plugin\`, MSBuild
     names the resource `ClashRuleEngine.Plugin.ClashRuleEngineRibbon.xaml` — Navisworks can't
     find that, so NO tab appears. Fix: set `<LogicalName>ClashRuleEngine.ClashRuleEngineRibbon.xaml</LogicalName>`
     on the `<EmbeddedResource>`.
  2. The XAML MUST use the SDK format (see `…\api\NET\examples\…\CustomRibbon\CustomRibbon.xaml`):
     root `<RibbonControl xmlns="clr-namespace:Autodesk.Windows;assembly=AdWindows" …>` with
     `<RibbonTab Id Title>`, `<RibbonPanel><RibbonPanelSource Title>`,
     `<local:NWRibbonButton Id …>` where `local="clr-namespace:Autodesk.Navisworks.Gui.Roamer.AIRLook;assembly=navisworks.gui.roamer"`.
     The old `<RibbonTab xmlns=".../navisworks/2023">` + `<RibbonButton>` form silently fails.
  3. The CommandHandlerPlugin should override `CanExecuteCommand => new CommandState(true)` and
     `CanExecuteRibbonTab => true` so the button isn't greyed out / the tab always shows.

### Build and deploy
1. Build: `msbuild ClashRuleEngine.csproj /p:Configuration=Release /p:Platform=x64 /p:NavisworksVersion=2027`
   (version defaults to newest installed Navisworks; output goes to `bin\x64\Release\<version>\`)
   MSBuild on this machine: VS2022 BuildTools (`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe`) or VS 2026 Community. `tools\build.ps1` does dump→build→deploy in one go.
2. Deploy: `tools\deploy.ps1 -Version 2027` — installs the bundle to
   `C:\ProgramData\Autodesk\ApplicationPlugins\ClashRuleEngine.bundle\` (user-writable, no admin).
   Alternatives: `-UserPlugins` → `%AppData%\Autodesk Navisworks Manage 2027\Plugins\ClashRuleEngine\`,
   `-Flat` (elevated) → `C:\Program Files\Autodesk\Navisworks Manage 2027\Plugins\`.
3. Restart Navisworks
4. Panel appears via View → Windows → Clash Rule Engine

### Plugin loading notes — FINAL, debugged to ground truth 2026-06-12
**The ONLY way third-party .NET plugins load in Navisworks 2027** (confirmed via Autodesk
forums "Plugin location under profile no longer supported in Version 2027" + live testing):

```
C:\Program Files\Autodesk\Navisworks Manage <year>\Plugins\ClashRuleEngine\ClashRuleEngine.dll
```
— a **name-matched folder/DLL pair** in the INSTALL's Plugins folder (admin to deploy).
This layout works on ≤2026 as well → single mechanism for every version.

Things that DO NOT work (each cost a debugging round — do not retry):
- **Any per-user folder.** Navisworks 2027 DEPRECATED the user-profile plugin location
  (`%AppData%\Autodesk Navisworks Manage <year>\Plugins`). ≤2026 honoured it; 2027 ignores it.
- `%LocalAppData%\Autodesk\Navisworks Manage <year>\Plugins` — never scanned by any version.
- **ApplicationPlugins bundles** (`ProgramData` or `AppData`) + PackageContents.xml — Navisworks
  does not use this mechanism for .NET plugins (it's Revit/AutoCAD's). PackageContents.xml in
  this repo is retained only for potential App Store packaging.
- Flat DLL directly in `Plugins\` root (no subfolder) — loaded in ≤2026, NOT in 2027.
- Naming the DLL `*.Plugin.dll` — that convention is for Autodesk-INTERNAL plugins
  (install root + `InternalPlugins\`); irrelevant and ineffective for third-party.

Useful diagnostics that survive in `tools\`: `Dump-NavisApi.ps1` (offline API surface),
`PluginProbe.cs/.exe` (loads the DLL exactly like Navisworks would; proves type/attribute
health and catches missing-dependency skips without restarting Navisworks).
- `tools\Dump-NavisApi.ps1` reflection-dumps the installed version's Clash API surface to
  `tools\navis-api-<version>.txt` — use it to verify API members offline before building/running

## Model property structure (from actual Revit export)
Properties are accessed via `ModelItem.PropertyCategories` → `PropertyCategory.Properties` → `DataProperty`.

### Example: HDPE pipe element
**Item tab:**
- Comments = VS
- Workset = SANITARY
- Type Name = HDPE
- Model = Plain end pipe, BIM: LOD400
- Manufacturer = Geberit
- Type Mark = PE80
- product_serie = PE-HD

**Dimensions tab:**
- Outside Diameter = 0.050 m (values in METRES)
- Inside Diameter = 0.044 m
- Size = Ø50
- Length = 3.484 m

**CRITICAL**: Dimension values are in **metres** in the API. So 100mm = `0.1`, 50mm = `0.05`.
The "Size" field contains the Ø symbol (e.g., "Ø50") which makes numeric comparison fail — use "Outside Diameter" for numeric rules.

Other tabs available: Mechanical, Mechanical - Flow, Constraints, Identity Data, Insulation, Other, Phasing.

## Current state and next steps

### Working (builds clean for 2027; live re-verification of the new rule model in progress)
- **Main panel = 3 tabs**: Rules · Clashes · General (light theme). The GROUPING control is a
  **global bar above the tabs** (applies to all tests). Header has **+ New Rule** and **Import**
  (load a `.clashre` OR a `clashre-kind-rules/1` `.json` from anywhere → saved to the global store, so it persists across files/instances).
  Opened from the **Tool Add-ins** ribbon button.
- **Learning pipeline**: `BatchClashExtractPlugin` (run headless via `tools\BatchExtractor` or the
  `tools\NwdClashLearner` GUI over coordinated NWDs) extracts per clash: each side's element kind
  (cat/family/type/system + diameter band), assignee, status, **clearance gap (mm, signed; <0 =
  penetration)**, **grid cell**, **level**, plus **family / type / leaf names** and **raw bore
  (`diaMm` min/max)** → one `clash_kinds.jsonl`. That data is mined into the per-test element-pair
  rule set (the `clashre-kind-rules/1` JSON the user imports) by **`tools\analyze_clashes.py`**
  (`python tools\analyze_clashes.py [clash_kinds.jsonl]`) — emits the importable rule JSON (two-tier
  per-test `testRules` fine→category, `tests` defaults, calibrated `approve` block) AND a
  `clash_analysis_report.txt` (per-test coverage, approve calibration, **service-type × clearance**
  breakdown, diameter-split suggestions). Rules are mined per CANONICAL trade pair and only where a
  pair DEVIATES from the test's dominant assignee (no blanket rules; the default is the safety net).
- **Run rules** (selected test / all) — SDK-supported Transaction write-back (quirk #0):
  per clash, the test's element-pair rules (first-match-wins) → **approve** (clearance-gated) →
  **grouping** (mode-aware) in ONE atomic write per test. Re-runnable/idempotent. Clashes whose
  element pair has no rule are left UNASSIGNED (no guessing).
- **Approve engine**: `ApprovePolicy` on `ProjectConfig`. Always-approve kinds (Flex Pipe/Duct) +
  assignees (TUNDISH); else per-pair clearance floors (default ≥50 mm). `NeverApprovePenetration`
  + structure test-name guard. Status only (no ApprovedBy).
- **Grouping**: **Grid** mode names bundles by the bare grid intersection ("H-22", with (1)/(2) on collision).
- **Stale-ref safety**: clash list caches GUID + Center (never holds live `ClashResult`);
  navigate/inspect re-resolve via `TestsData.ResolveGuid`; panel subscribes `TestsData.Changed`
  (auto-refresh, suppressed during our own runs). Global WPF dispatcher safety-net keeps an
  unhandled UI-thread exception from killing Navisworks.
- **3D clash markers** (Clashes tab toggle); **clash matrix view** (Matrix button); **exports**
  (full session JSON + lean per-test assignment summary); **AI rule generation** (Claude raw-HTTP,
  `claude-opus-4-8`). Light theme UI.

### Next to build
1. **Family/size refinement of element-pair rules** — category is the START; split the MIXED pairs
   (and cases like switchboard-vs-pipe → HYD) by Revit family / diameter where category is too coarse.
2. **Rules-by-example replay/validation** — score a proposed rule set against the historical
   assignments ("reproduces X% of your decisions") before trusting it. (Current replay: ~91% acc /
   83% recall on the approve model; an assignment-replay is the next trust step.)
3. **In-panel rule editing UX** for the per-test pair rules (add/disable/reorder, see confidence).
4. **In-document stamping** (optional) — per-clash outcome onto model items via the COM
   `InwGUIPropertyNode2` bridge (config stays in the global `.clashre` store).

## Clash test pairs (real project models, "_X vs _Y" naming)
Service-vs-service: `_ELEC vs _MECH`, `_FIRE vs _MECH`, `_ELEC vs _FIRE`, `_FIRE vs _HYD`, `_ICT vs _MECH`,
`_ELEC vs _ICT`, `_HYD vs _MECH`, `_MECH vs _SEC`, `_ELEC vs _HYD`, `_ELEC vs _SEC`, `_FIRE vs _ICT`,
`_ICT vs _SEC`, `_FIRE vs _SEC`, `_ELEC vs _FUEL`, `_HYD vs _ICT` … and each `_X vs _STR` (structure).

Trades: ELEC = Electrical, MECH = Mechanical, FIRE = Fire, HYD = Hydraulic, ICT = Comms/Data,
SEC = Security, FUEL = Fuel, STR = Structure, DRUPS = diverse/redundant UPS power (a cable-tray
subset of electrical, not separable from ELEC by element kind alone).
