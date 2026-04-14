# CLAUDE.md — Project Context for Claude Code

## Project overview
This is a **Navisworks Manage 2026** dockable panel plugin (C# / WPF / .NET Framework 4.8) for BIM coordination clash management. It provides a rule-based engine for grouping and assigning clash detection results, with per-test rule hierarchies and AI-assisted analysis planned.

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

## Navisworks 2026 API quirks (IMPORTANT)
These were discovered through trial and error during development:

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
1. Build in Visual Studio 2022 as Release | x64
2. Copy `ClashRuleEngine.dll` + `PackageContents.xml` to:
   `C:\Program Files\Autodesk\Navisworks Manage 2026\Plugins\`
   (requires admin — or use `%AppData%\Autodesk Navisworks Manage 2026\Plugins\`)
3. Restart Navisworks
4. Panel appears via View → Windows → Clash Rule Engine

### Plugin loading notes
- `PackageContents.xml` MUST have the `.xml` extension (Windows sometimes strips it)
- Files can go directly in the Plugins folder (no subfolder required)
- The `%LocalAppData%` path did NOT work on this machine; `Program Files` path works

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

### Working
- Dockable panel loads in Navisworks 2026
- Clash test selector with per-test rule hierarchies
- Rule editor with model property dropdowns (Scan Model button)
- Drag-and-drop + arrow buttons for rule reordering
- Save/load rules to .clashre file
- Run rules against selected test or all tests
- Light theme UI

### Next to build
1. **Clash inspector** — click a clash in the rule engine panel, see properties of both items (Item A and Item B) side-by-side. "Create rule from this" button that pre-fills the editor.
2. **Core property filtering** — filter out noise from property dropdowns, show only useful properties (Dimensions, Item, Identity Data, Mechanical, etc.)
3. **Better results view** — visual breakdown of how clashes were distributed across rules, which didn't match, clickable to navigate in Navisworks.
4. **Claude AI integration** — "Smart Assign" button that sends clash data to the Claude API for intelligent grouping suggestions. API key stored in ProjectConfig.
5. **System hierarchy editor** — UI to view/edit the discipline precedence order.
6. **Clash matrix view** — overview showing all test pairs and their rule/assignment status.

## Clash test pairs in the test model
MC v EC, MC v FC, MC v HC, EC v FC, EC v HC, FC v HC, MC v SC, EC v SC, FC v SC, HC vs SC

Where: MC = Mechanical, EC = Electrical, FC = Fire, HC = Hydraulic, SC = Structural
