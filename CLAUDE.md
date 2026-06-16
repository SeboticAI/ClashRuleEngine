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
│   ├── ClashProcessingService.cs # Evaluates rules + hierarchy, groups (mode-aware), writes back
│   ├── ClashApiCompat.cs         # Version compat for the 2027 TestsRoot folder tree
│   ├── ClashNavigationService.cs # Resolve-by-GUID navigate/select/frame (stale-ref-safe)
│   ├── ClashMarkerService.cs     # Shared state + drawing for the 3D marker overlay
│   ├── ClashTestScanner.cs       # Discovers clash tests/results from the NW document
│   ├── DisciplineClassifier.cs   # Classifies an element to a discipline by keywords
│   ├── ModelPropertyScanner.cs   # Scans model for available properties (for dropdowns)
│   ├── SessionExportService.cs   # Full session export + lean per-test assignment summary
│   ├── AiRuleGenerator.cs / ClaudeApiService.cs # AI rule authoring (raw-HTTP, opus-4-8)
│   └── RulePersistenceService.cs # Saves/loads ProjectConfig as .clashre XML file
├── UI/
│   ├── Converters.cs             # WPF value converters (incl. AssigneeModeIndexConverter)
│   ├── RuleEditorDialog.xaml/.cs # Rule creation/editing dialog
│   ├── ClashInspectorDialog.xaml/.cs # Side-by-side Item A/B property inspector
│   ├── ClashMatrixDialog.cs      # Code-only discipline-vs-discipline matrix view
│   ├── ExportProgressWindow.cs   # Streaming-export progress + cancel
│   ├── SettingsDialog.xaml/.cs   # LEGACY (settings now live in main tabs; file retained, unused)
│   └── RuleEnginePanel.xaml/.cs  # Main panel: 5 tabs (Rules/Clashes/Hierarchy/Assignees&Groups/General)
├── Plugin/
│   ├── ClashRuleEnginePlugin.cs  # DockPanePlugin + RibbonTab handler
│   ├── ClashMarkerPlugins.cs     # RenderPlugin (overlay) + InputPlugin (click-to-select)
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

### Working (built + builds clean for 2027; live re-verification still pending after the June 2026 redesign)
- **Main panel = 5 tabs**: Rules · Clashes · Hierarchy · Assignees & Groups · General
  (the old Settings dialog's sections were promoted into tabs; `SettingsDialog` is now unused).
  Header has **+ New Rule** and **Import** (load a `.clashre` from anywhere → saved beside the doc).
- Clash test selector + per-test rule hierarchies; rule editor with model-property dropdowns
  (Scan Model, lazy value loading); drag-and-drop + arrow rule reordering; **Tree-Path** condition.
- **Run rules** (selected test / all) — SDK-supported Transaction write-back (quirk #0):
  per clash, rules first (first-match-wins) then the **discipline-hierarchy fallback**; then
  **grouping** (mode-aware) in ONE atomic write per test. Re-runnable/idempotent.
- **Grouping** (Hierarchy tab → GROUPING card): mode = None / Shared element / Proximity /
  Grid / Level / Assignee / Hybrid; editable proximity threshold; **group-then-assign**
  (`AssignByGroup`) gives each bundle one trade (majority of members). Persisted in `.clashre`.
- **Discipline hierarchy editor** (Hierarchy tab): order = precedence, keywords, and an
  **"Assign the clash to"** mode per discipline (Specific owner / This trade / **The other trade**)
  — e.g. a "Hydraulic Drainage" sub-discipline set to "other trade" routes drainage across in any test.
- **Stale-ref safety**: clash list caches GUID + Center (never holds live `ClashResult`);
  navigate/inspect re-resolve via `TestsData.ResolveGuid`; panel subscribes `TestsData.Changed`
  (auto-refresh, suppressed during our own runs). Global WPF dispatcher safety-net keeps an
  unhandled UI-thread exception from killing Navisworks.
- **3D clash markers** (Clashes tab toggle): `RenderPlugin` draws status-coloured circles at
  clash centres; `InputPlugin` click-to-select; shared snapshot in `ClashMarkerService`.
- **Clash matrix view** (Matrix button): discipline-vs-discipline grid, active/total per pair,
  click a cell to open that test.
- **Exports**: full session JSON (`ExportTests`) + lean per-test **assignment summary**
  (`ExportSummary`, harvests element type/system/workset/family/diameter tokens per assignee
  bucket + a raw tree sample) — small enough to paste into a prompt.
- **AI rule generation** (AI Rules): Claude (raw-HTTP, `claude-opus-4-8`) authors rules, the engine executes.
- Light theme UI.

### Next to build
1. **AI rules-by-example REPLAY/validation** — score proposed rules against the example
   assignments ("matches 37/40 of your Hydraulics") BEFORE the user accepts. The trust step.
2. **Priority scoring** — deterministic clash triage (penetration depth, hard/soft pairing,
   system criticality, cluster size).
3. **Group → assign UI in the Clashes tab** — select a bundle, assign it a trade in one click
   (SDK `TestsEditResultStatus` cascades to a whole `ClashResultGroup`).
4. **In-document stamping** (optional) — write per-clash outcome onto model items via the COM
   `InwGUIPropertyNode2` bridge for downstream visibility (config stays sidecar `.clashre`).

## Clash test pairs in the test model
MC v EC, MC v FC, MC v HC, EC v FC, EC v HC, FC v HC, MC v SC, EC v SC, FC v SC, HC vs SC

Where: MC = Mechanical, EC = Electrical, FC = Fire, HC = Hydraulic, SC = Structural
