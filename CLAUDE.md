# CLAUDE.md — Project Context for Claude Code

## Project overview
This is a **Navisworks Manage** dockable panel plugin (C# / WPF / .NET Framework 4.8) for BIM coordination clash management. It provides a rule-based engine for grouping and assigning clash detection results, with per-test rule hierarchies and AI-assisted analysis planned.

Builds are per-Navisworks-version (`/p:NavisworksVersion=2027`, default = newest installed). **This machine has Navisworks Manage 2027** (2026 was removed in the 2026-04 upgrade cycle); older-version builds need that version's API DLLs dropped in `Refs\<version>\`.

The business context: we're building a product to help companies automate BIM coordination workflows, following Australian BIM coordination standards (system hierarchy, clash matrix, discipline responsibility).

## Architecture

### Project structure
```
ClashRuleEngine/
├── Models/
│   ├── ClashRule.cs              # Single rule with conditions, group, assignee, priority
│   ├── RuleCondition.cs          # Individual condition (category.property operator value)
│   └── TestRuleSet.cs            # Per-test rules + ProjectConfig + SystemHierarchy
├── Services/
│   ├── ClashProcessingService.cs # Evaluates rules against clash results
│   ├── ClashTestScanner.cs       # Discovers clash tests from the NW document
│   ├── ModelPropertyScanner.cs   # Scans model for available properties (for dropdowns)
│   └── RulePersistenceService.cs # Saves/loads ProjectConfig as .clashre XML file
├── UI/
│   ├── Converters.cs             # WPF value converters
│   ├── RuleEditorDialog.xaml/.cs # Rule creation/editing dialog
│   └── RuleEnginePanel.xaml/.cs  # Main dockable panel with test selector
├── Plugin/
│   ├── ClashRuleEnginePlugin.cs  # DockPanePlugin + RibbonTab handler
│   └── ClashRuleEngineRibbon.xaml # Ribbon layout (embedded resource)
├── Installer/
│   └── ClashRuleEngine.iss       # Inno Setup installer script
├── PackageContents.xml           # Navisworks plugin manifest
└── ClashRuleEngine.csproj        # Classic-style .NET 4.8 project (NOT SDK-style)
```

### Key design decisions
- **Per-test rule hierarchies**: Each clash test (MC vs EC, HC vs SC, etc.) has its own independent list of rules with its own priority ordering. Stored in `ProjectConfig.TestRuleSets`.
- **System hierarchy**: Structure > Architecture > HVAC > Plumbing > Fire > Electrical > Comms > Landscape. The lower-priority system is responsible for resolving clashes.
- **Persistence**: Rules saved as `.clashre` XML file alongside the NW document (not embedded in the NW file — the Navisworks 2026 API doesn't expose reliable document-level user data storage).
- **Light theme UI**: White backgrounds, dark text, blue accents. The dark theme was hard to read inside Navisworks.

## Navisworks API quirks (IMPORTANT)
These were discovered through trial and error during development (2026/2027 APIs):

0. **Writing to clash results (THE big one — caused the 2026-era crashes):** attached
   `ClashResult` objects are read-only; direct property sets are ignored, and pushing
   reflection-built groups/results into a live test via `TestsAddCopy` duplicates result
   GUIDs and **crashes Navisworks** (corrupts Clash Detective state). The correct pattern
   (proven by the open-source GroupClashes plugin, NW 2015→2027):
   `var copy = (ClashTest)test.CreateCopy();` → edit/regroup children on the detached copy
   (plain property sets work there) → swap back atomically with
   `TestsEditTestFromCopy(test, copy)` — **renamed in 2027**; it was
   `TestsEditTestFromCustom` in ≤2026 (`WriteBack` resolves whichever exists).
   One atomic write per test. See `ClashProcessingService.ProcessTestCore`.
   Verified against the real DLLs with `tools\Dump-NavisApi.ps1` → `tools\navis-api-2027.txt`:
   `ClashResult.Description/Status/AssignedTo/Center/Guid` are all RW, `AssignedTo` is
   typed `Assignee`, `ClashResultGroup` has a public ctor + RW DisplayName + mutable Children.

0b. **2027 moved the tests collection**: `DocumentClashTests.Tests` no longer exists.
   2027+ uses `TestsData.Value.TestsRoot` — a `ClashTestFolder` TREE (2027 added clash
   test folders), so tests must be collected recursively. ALL test enumeration goes
   through `ClashApiCompat.GetAllTests()` (typed per-version via the `NW_TESTS_TREE`
   define) — never enumerate `TestsData` directly.
1. **`DockPanePluginRecord.IsVisible`** — does NOT exist. Use `LoadedPlugin != null` to check if loaded.
2. **`DockPanePluginRecord.Enabled`** — does NOT exist. Can't toggle visibility programmatically.
3. **`ModelItemEnumerableCollection.DescendantsAndSelf`** — does NOT exist. Use `model.RootItem.Descendants` instead, iterating through `doc.Models` first.
4. **`ModelItemEnumerableCollection.Descendants`** — does NOT exist on the collection. Must go through individual `Model` objects: `foreach (Model model in doc.Models) foreach (ModelItem item in model.RootItem.Descendants)`.
5. **`ClashResult.ApprovedBy`** — is NOT a simple string. Can't assign strings directly. Use `Description` field for assignee info.
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

### Working (pending re-verification in 2027 after the June 2026 stabilisation rewrite)
- Dockable panel, clash test selector with per-test rule hierarchies
- Rule editor with model property dropdowns (Scan Model button, lazy value loading)
- Clash inspector dialog (side-by-side Item A/B properties)
- Drag-and-drop + arrow buttons for rule reordering
- Save/load rules to .clashre file
- Run rules against selected test or all tests — rewritten June 2026 to the
  copy/edit/swap pattern (see quirk #0): applies Description/Status/AssignedTo and
  auto-groups clashes (shared element + 1m proximity, union-find) in ONE atomic
  write per test
- Session export (`SessionExportService` + footer button): all clash dispositions +
  both items' property bags → `.session.json` — the foundation for AI rule inference
- Light theme UI

### Next to build
1. **AI rules-by-example loop** — feed a `.session.json` (manual coordination pass) to
   Claude, get proposed rules back, REPLAY them deterministically against the session
   ("matches 37/40 of your Hydraulics assignments") before the user accepts. Incremental
   teaching flow: user handles ~20 clashes → AI proposes → engine applies to the rest.
2. **Priority scoring** — deterministic clash triage from data we already read:
   penetration depth, hard/soft element pairing, system criticality, cluster size.
3. **Better results view** — visual breakdown of how clashes were distributed across rules, clickable to navigate in Navisworks.
4. **Claude AI integration** — "Smart Assign" button sending `.session.json` payloads to the Claude API. API key stored in ProjectConfig.
5. **System hierarchy editor** — UI to view/edit the discipline precedence order.
6. **Clash matrix view** — overview showing all test pairs and their rule/assignment status.

## Clash test pairs in the test model
MC v EC, MC v FC, MC v HC, EC v FC, EC v HC, FC v HC, MC v SC, EC v SC, FC v SC, HC vs SC

Where: MC = Mechanical, EC = Electrical, FC = Fire, HC = Hydraulic, SC = Structural
