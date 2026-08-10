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
│   ├── PluginIds.cs              # plugin Name/DeveloperId consts — the FindPlugin keys
│   ├── ClashRuleEnginePlugin.cs  # DockPanePlugin + RibbonTab handler + ShowPanel()
│   ├── ClashMarkerPlugins.cs     # RenderPlugin (overlay) + InputPlugin (click-to-select)
│   ├── BatchClashExtractPlugin.cs# Headless AddInPlugin: extracts per-clash kind/assignee/status/gap/grid/level → JSONL (the learning data)
│   └── ClashRuleEngineRibbon.xaml # Ribbon layout (embedded resource)
├── tools/
│   ├── BatchExtractor/           # Automation console driver for BatchClashExtractPlugin
│   ├── NwdClashLearner/          # WinForms GUI: pick NWDs, run the extractor (uses the DEPLOYED plugin)
│   ├── make-icons.ps1            # regenerates the ribbon/installer icons from the logo
│   ├── harvest-refs.ps1          # pulls a version's API DLLs into Refs\<year>\ (-List to survey)
│   └── build-all.ps1             # builds every buildable version [-Installer compiles setup.exe]
├── Resources/
│   ├── oconnors_logo.png         # brand lockup (source for every icon)
│   └── oconnors_clash_{16,32}.ico# ribbon icons — MUST deploy next to the DLL
├── Installer/
│   ├── ClashRuleEngine.iss       # Inno Setup installer script (2024-2027, multi-version)
│   └── oconnors_clash.ico        # setup.exe icon (multi-size 16/32/48)
├── rules/                        # DISTRIBUTABLE rule sets + analyzer report (see rules\README.md)
│   ├── clashre-kind-rules-2026-06-18-mined.json  # current 2-tier model, 81.8% replay
│   └── clashre-kind-rules-2026-06-18-full.json   # earlier 923-rule set = what is live now
├── PackageContents.xml           # Navisworks plugin manifest
└── ClashRuleEngine.csproj        # Classic-style .NET 4.8 project (NOT SDK-style)
```

### Key design decisions
- **Per-test element-pair rules** (CURRENT model): each clash test has its own ordered `ClashRule` list in `ProjectConfig.TestRuleSets`. An element-pair rule = a `ClashRule` with `And` logic + two `Either`-target "Category contains" conditions → matches a clash where one element is category A and the other is category B (unordered), assigned to a Named trade. Imported from a `clashre-kind-rules/1` JSON's `"testRules":[{test,a,b,assign}]` block via `KindRuleImport`. Built from mining ~28 coordinated NWDs (`clash_kinds.jsonl`). Ambiguous pairs are intentionally omitted → left unassigned.
- **Approve engine** (`ApprovePolicy` / `ClashProcessingService.ApproveWithinTolerance`): after assignment, auto-set Status=Approved. Two paths: (1) **always-approve** (gap-independent) — `ApproveKinds` (element-kind keywords, e.g. Flex Pipe 91% / Flex Duct 94% approved → approved even on a hard clash, they bend) and `ApproveAssignees` (e.g. TUNDISH 90% approved); (2) **clearance-gated** — gap ≥ a per-pair floor (default ≥50 mm). Hard gates: penetrations never approved by the gap path; `_X vs _STR` (structure) never approved (test-name guard). All learned from the data. (We do NOT set ApprovedBy — it caused a confusing "Approved by: varies" group rollup; Status only.)
- **NO discipline/system hierarchy** — that whole responsibility system (SystemHierarchy, DisciplineClassifier, owner/other resolution, Hierarchy tab) was REMOVED 2026-06-17. Assignment comes only from per-test rules now.
- **Pipeline order**: assign-per-clash (rules) → approve → group → ONE atomic write-back per test. Grouping only organises; it never re-assigns.
- **Grouping** (`ClashGroupingMode`): None / SharedElement / Proximity / **Grid** / Level / ByAssignee / Hybrid. **Grid** is the recommended mode: groups named by the bare grid intersection only (e.g. "H-22" — level stripped, no trade, no count), with " (1)"/" (2)" suffixes when two groups share a grid name (`GroupByGrid`/`GridName`). (`GridTrade` enum value is retained but now routes to the same grid grouping.) `AssignByGroup` (group-then-assign majority) conflicts with per-element specificity — leave OFF.
- **Shipped rule sets + picker** (2026-08-10, `Services\BuiltInRuleSets.cs`): two rule sets are
  **embedded resources** in the DLL, so a new user never has to be sent a file —
  `LoadConfig()` seeds from the default on first run (guard: only when NO test has any rule, so
  it can't clobber an import). The **Rule set** picker above the tabs switches between them:
  - `current` — `RuleSets\current.clashre`, the production config: 923 pair rules + 25 per-test
    defaults, approve ≥50 mm. **It is a HYBRID neither analyzer JSON reproduces alone** (the pair
    rules came from the 923-rule file, which has no defaults; the 25 defaults came from the mined
    file). That's why it ships as a `.clashre`, not a kind-rules JSON. **Default.**
  - `mined` — `RuleSets\mined.json`, newest analyzer output: 274 deviation-only rules + 25
    defaults, 81.8% replay, approve ≥25 mm + 20 per-pair floors → **approves MORE** than
    `current`, hence not the default.
  `ProjectConfig.ActiveRuleSetId` records which is live (empty = hand-imported → "Custom").
  All three entry points (picker, Import button, first-run seed) go through **one**
  `ApplyRuleSetText(text, builtInId, replaceAllRules)`; the picker passes `replaceAllRules:true`
  so a switch is a clean swap — otherwise the old set's rules survive on every test the new one
  doesn't mention, silently blending the two. It also preserves grouping + `ApiKey` across a
  `.clashre` swap (the shipped sets carry no key, so a switch would otherwise wipe it).
  Refresh the embedded copies with `tools\stage-rulesets.ps1`, which **scrubs `ApiKey`** —
  never hand-copy a `.clashre` in, or a user's Claude key ships inside the DLL and lands in git.
  These ARE embedded resources; the ribbon layout deliberately is NOT (see the ribbon note).
- **Persistence**: ONE GLOBAL `.clashre` XML at `%AppData%\ClashRuleEngine\config.clashre` (`RulePersistenceService`). It is the single source of truth — an imported rule set survives across files AND Navisworks instances; only a new import (or an edit) overwrites it. (Was a per-document sidecar; changed 2026-06-18 so a learned rule file follows the user, not the model. The API has no reliable document-level user-data store anyway.)
- **Light theme UI**: white cards on `#F8F9FA`, dark `#1A1A2E` text, blue `#2563EB` accent, `#E5E7EB` borders. (A dark-theme attempt was reverted — it produced unreadable light-on-light fields.)
- **Ribbon / naming** (all "Clash Rule Engine" as of 2026-08-10): the dock pane is **Clash Rule
  Engine** (DockPanePlugin DisplayName → View→Windows entry + pane title). A custom ribbon tab
  **Clash Rule Engine** (CommandHandlerPlugin + RibbonLayout) holds an **Open Panel** button;
  an `AddInPlugin` (DisplayName **Clash Rule Engine**) under Tool Add-ins does the same. Both
  carry the OConnors icon. The tab's label comes from `Title` in the ribbon XAML, which
  overrides the `[RibbonTab] DisplayName` — both are set identically so they can't drift.
  **A custom RibbonTab is the ONLY way to get your own name on the ribbon**: an `AddInPlugin`
  button lands in Navisworks' stock "Tool Add-ins" tab, in a panel Navisworks names itself
  ("Tool add-ins 1"), and no plugin can rename that. **CONFIRMED WORKING 2026-08-10** — the tab
  now shows, with the icon, alongside Navisworks' own tabs.
  Consequently there are **no GUI AddInPlugins left**: the duplicate panel-opener add-in was
  deleted, and `BatchClashExtractPlugin` moved to **`AddInLocation.None`** — registered and
  still reachable via `Automation.ExecuteAddInPlugin("ClashBatchExtract.OCON", …)` from
  `tools\BatchExtractor` / `tools\NwdClashLearner`, but placed nowhere in the UI, which makes
  the stock "Tool add-ins" tab disappear entirely. `AddInLocation.None` exists for exactly this
  (programmatically-invoked add-ins). If the headless learner ever can't find the plugin, put
  `AddInLocation.AddIn` back — that is the whole revert.
  The XAML can be syntax-checked offline against Navisworks' own types:
  ```powershell
  [void][Reflection.Assembly]::LoadFrom("$nav\AdWindows.dll")
  [void][Reflection.Assembly]::LoadFrom("$nav\navisworks.gui.roamer.dll")
  Add-Type -AssemblyName PresentationFramework, System.Xaml
  [Windows.Markup.XamlReader]::Parse((Get-Content $xamlPath -Raw)).Tabs   # Title, Panels, Items
  ```
  **But that only proves the markup is well-formed — it does NOT prove Navisworks shows the
  tab.** It parsed fine for months while no tab appeared, because the problem was WHERE the
  file lived (see the Project-file-format section), not what was in it. The only real test is
  restarting Navisworks and looking at the ribbon. `ShowPanel()` sets **`DockPanePlugin.Visible = true`** then `ActivatePane()` after loading — see API quirk #1; loading alone leaves the pane hidden. It is idempotent (clicking when already open just focuses it).

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
1. **Showing a dock pane from a button (CORRECTED 2026-08-10 — this was the "janky ribbon
   button"):** `DockPanePluginRecord` has no `IsVisible`/`Enabled`, but the members that
   matter live on the **loaded plugin** and on the **base record**, not on
   `DockPanePluginRecord`:
   - **`DockPanePlugin.Visible` `[RW]`** EXISTS — and it *is* the View → Windows tick box.
   - **`DockPanePlugin.ActivatePane()`** EXISTS — brings the pane forward when docked
     behind a sibling tab.
   - **`PluginRecord.IsEnabled`** EXISTS (it's `IsEnabled`, not `Enabled`), as does
     `IsLoaded` and `TryLoadPlugin()` (null instead of throwing).
   So the correct open-from-button sequence — SDK sample
   `api\NET\examples\PlugIns\ClashDetective\EventLog\LogDockPaneAddin.cs`, now in
   `ClashRuleEnginePlugin.ShowPanel()` — is:
   ```csharp
   if (Application.IsAutomated) return;                       // no GUI to dock into
   var rec = Application.Plugins.FindPlugin("ClashRuleEngine.OCON") as DockPanePluginRecord;
   if (rec == null || !rec.IsEnabled) return;
   var pane = rec.LoadedPlugin ?? rec.TryLoadPlugin();         // load only if needed
   if (pane == null) return;
   pane.Visible = true;                                       // <-- THE ACTUAL FIX
   pane.ActivatePane();
   ```
   The old code called only `LoadPlugin()`. That instantiates the pane but leaves
   `Visible == false`, so the button appeared to do nothing and the user still had to tick
   View → Windows by hand. **`LoadPlugin()` alone never shows a pane.**
2. **Plugin lookup key** — `FindPlugin` takes `"<Name>.<DeveloperId>"` from the `[Plugin]`
   attribute. Keep it in ONE place (`Plugin\PluginIds.cs`): a stale literal makes
   `FindPlugin` return null and the button silently no-op. DeveloperId is `OCON`
   (4 chars required; was the sample placeholder `ACME` until 2026-08-10).
2b. **Ribbon button icons** — `[Command(Icon="x_16.ico", LargeIcon="x_32.ico")]` and
   `[AddInPlugin(Icon=…, LargeIcon=…)]`. Navisworks resolves those paths **relative to the
   plugin DLL** (or an `Images\` subfolder beside it), so the .ico files must be deployed
   NEXT TO `ClashRuleEngine.dll` — the csproj target `CopyRibbonIcons` puts them in the
   build output, and `deploy.ps1`/the installer ship them. Use **.ico** (16x16 and 32x32),
   as the SDK samples do; Navisworks loads them with its own loader, not WPF, so
   PNG-compressed ICOs and .png are not worth the risk. Do NOT also set `Image`/`LargeImage`
   in the ribbon XAML — those OVERRIDE the attribute icons and would be resolved relative
   to the embedded ribbon resource, which has no folder on disk.
   Regenerate with `tools\make-icons.ps1` (brand mark + crossing-services clash glyph,
   drawn as vector at 16/32px because a bicubic downscale of the logo goes mushy).
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
- Ribbon XAML: `<None>` staged to `$(OutDir)en-US\` — **NOT** `<EmbeddedResource>`. See below.
- **Ribbon XAML = LOOSE FILE IN A LOCALE FOLDER (SOLVED 2026-08-10 — read this before
  touching the ribbon):**
  1. Navisworks resolves the `[RibbonLayout]` `.xaml` and `[Strings]` `.name` of a
     `CommandHandlerPlugin` from a **locale subfolder next to the plugin DLL**:
     ```
     Plugins\ClashRuleEngine\
         ClashRuleEngine.dll
         en-US\ClashRuleEngineRibbon.xaml
         en-US\ClashRuleEngine.name
         Images\*.ico
     ```
     Ground truth = the post-build event inside
     `api\net\examples\Basic Examples\CSharp\CustomRibbon\CustomRibbon.csproj` (the file is
     ACL-locked; copy it out with an elevated shell to read it):
     ```
     xcopy /Y "$(TargetPath)"    "...\Plugins\$(TargetName)\"
     mkdir                       "...\Plugins\$(TargetName)\en-US"
     copy /Y "CustomRibbon.xaml" "...\Plugins\$(TargetName)\en-US\"
     copy /Y "CustomRibbon.name" "...\Plugins\$(TargetName)\en-US\"
     xcopy /Y "Images\*.*"       "...\Plugins\$(TargetName)\Images\"
     ```
     Both are plain `<None>` + `CopyToOutputDirectory` there. Navisworks' own ribbons ship the
     same way (`<install>\en-US\Ribbon.xaml`, `<install>\en-US\Clash.Plugin.name`).
     **THE PREVIOUS NOTE HERE WAS WRONG** and cost a lot of time: it claimed the XAML had to be
     an `<EmbeddedResource>` whose name was `<RootNamespace>.<file>`, set via `<LogicalName>`.
     Navisworks never looks for an embedded resource, so the tab NEVER appeared — the resource
     name was correct and completely irrelevant. Do not "restore" that.
     A missing layout file fails **silently**: no tab, no error, nothing in any log.
     Only `en-US` is shipped; a non-English Navisworks needs the two files under its own locale.
  1b. **`.name` file format** (`Plugin\ClashRuleEngine.name`): UTF-8 **with BOM**, a `$utf8`
     directive line, `#` comments, and each key ends with `=` with its **value on the NEXT
     line** (`ID_OpenPanel.ToolTip=` ⏎ `Open the …`). Keys: `DisplayName`,
     `<TabId>.DisplayName`, `<CommandId>.DisplayName/.ToolTip/.ExtendedToolTip`.
     Precedence: XAML `Title` > `.name` > attribute `DisplayName`.
  2. The XAML MUST use the SDK format (see `…\api\NET\examples\…\CustomRibbon\CustomRibbon.xaml`):
     root `<RibbonControl xmlns="clr-namespace:Autodesk.Windows;assembly=AdWindows" …>` with
     `<RibbonTab Id Title>`, `<RibbonPanel><RibbonPanelSource Title>`,
     `<local:NWRibbonButton Id …>` where `local="clr-namespace:Autodesk.Navisworks.Gui.Roamer.AIRLook;assembly=navisworks.gui.roamer"`.
     The old `<RibbonTab xmlns=".../navisworks/2023">` + `<RibbonButton>` form silently fails.
  3. The CommandHandlerPlugin should override `CanExecuteCommand => new CommandState(true)` and
     `CanExecuteRibbonTab => true` so the button isn't greyed out / the tab always shows.

### Multi-version support (2024–2027) — ONE BUILD PER VERSION IS MANDATORY
The Navisworks API assemblies are **strong-named and version-stamped per release** —
`Autodesk.Navisworks.Api` is `21.0.0.0` (2024), `22.0` (2025), `23.0` (2026), `24.0` (2027),
all `PublicKeyToken=d85e58fa5af9b484`. `roamer.exe.config` pins each one to its own release
with `<bindingRedirect oldVersion="24.0.0.0-24.0.9999.9999">` **and
`<publisherPolicy apply="no"/>`** (verified 2026-08-10). So a DLL compiled against 24.0
physically cannot load in 2026, and there is no redirect, shim or bundle layout that makes
one DLL span versions. Don't go looking for one — this was checked.

Therefore: one build per year, each against that year's reference DLLs. A version is
buildable when its refs are reachable — **installed locally** (the simplest route: just
install that Navisworks version on the build machine) **or** harvested into `Refs\<year>\`.
- `tools\build-all.ps1 [-Installer]` — regenerates icons, builds every buildable year, and
  **reports the years it skipped** rather than quietly shipping fewer versions. Nothing to
  configure: install Navisworks 2026 and the next run picks 2026 up.
- `tools\harvest-refs.ps1 -List` — survey: installed / harvested / API version per year.
- `tools\harvest-refs.ps1 -Version 2026` — fallback for a version you can't install here.
  Copies the API DLLs into `Refs\2026\`; run it on a machine that HAS 2026, or
  `-From "\\host\c$\Program Files\Autodesk\Navisworks Manage 2026"`. It warns loudly if the
  API major doesn't match the year (i.e. `-From` pointed at the wrong install).
  `Refs\` is **gitignored on purpose** (Autodesk binaries) — it lives on the build machine.
- Only **Api + Clash** are required references. Automation and Controls are conditional in
  the csproj (declared-but-unused; the Automation API is driven from the separate
  `tools\BatchExtractor` / `tools\NwdClashLearner` projects), so a partial ref set builds.
- Only ONE compile-time fork exists: `NW_TESTS_TREE` (≥2027) in `Services\ClashApiCompat.cs`.
  **The ≤2026 branch (`DocumentClashTests.Tests`) has never been compiled** — 2026 was
  removed from this machine before it was written, so the "verified" note on it is inherited,
  not tested. Expect the FIRST 2024–2026 build to be where that surfaces; the fix is local to
  that one `#else`. (Confirmed for 2027: `DocumentClashTests` has `Value.TestsRoot` and NO
  `Tests` property, so the fork is genuinely needed.)

### Build and deploy
1. Build one version: `msbuild ClashRuleEngine.csproj /p:Configuration=Release /p:Platform=x64 /p:NavisworksVersion=2027`
   (version defaults to newest installed Navisworks; output → `bin\x64\Release\<version>\`)
   MSBuild on this machine: VS2022 BuildTools (`C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe`) or VS 2026 Community.
   `tools\build.ps1` = dump→build→deploy for one version; `tools\build-all.ps1` = every version.
   The build copies the ribbon .ico files next to the DLL and **errors if they're missing**
   (run `tools\make-icons.ps1`).
2. Deploy for dev: `tools\deploy.ps1 -Version 2027` — self-elevates and copies
   `ClashRuleEngine.dll` + the two .ico files to
   `C:\Program Files\Autodesk\Navisworks Manage 2027\Plugins\ClashRuleEngine\`, then
   **verifies every file by hash**. It refuses to run while `roamer.exe` is alive, because a
   loaded plugin DLL is locked and the copy would silently leave the old build in place.
3. Ship to the team: `tools\build-all.ps1 -Installer` → `Installer\Output\OConnorsClashEngine_Setup_<ver>.exe`.
   See "Installer" below.
4. Restart Navisworks → **OConnors Clash** tab → **Clash Engine** (also under Tool Add-ins).

### Installer (`Installer\ClashRuleEngine.iss`, Inno Setup 6)
Compiler: `winget install --id JRSoftware.InnoSetup`. NOTE it installs **per-user** at
`%LocalAppData%\Programs\Inno Setup 6\ISCC.exe` — probing only Program Files finds nothing
(`build-all.ps1` checks the per-user path and the uninstall registry key).
- Packages **exactly** the years whose `bin\x64\Release\<year>\ClashRuleEngine.dll` exists at
  compile time (`#if FileExists` → `Has20xx`), and offers a component only when that year is
  BOTH packaged and installed — so a user can never tick a version that copies nothing.
  Warns on launch if the machine has a Navisworks the setup has no build for.
- **Destination is read from the registry at run time**, not hardcoded. Autodesk keys by API
  MAJOR, not year (`major = year - 2003`):
  `HKLM\SOFTWARE\Autodesk\Navisworks API Runtime\<major>\Navisworks Manage` → `Path`, or
  `HKLM\SOFTWARE\Autodesk\Navisworks Manage\<major>.0\Location` → `Path`, else the default
  Program Files path. The previous script probed
  `HKLM\SOFTWARE\Autodesk\Navisworks Manage <year>` for `InstallDir` — **a key that exists in
  no release**, so detection always fell through to the hardcoded path and a non-default
  install location was invisible. Verified against the live 2027 registry 2026-08-10.
- Blocks in `PrepareToInstall` if `roamer.exe` is running (tasklist+find; Navisworks exposes
  no documented mutex or window class to probe).
- To verify the Pascal logic without elevating, compile a copy of the `[Code]` section as a
  `PrivilegesRequired=lowest` probe that dumps what it resolves and returns False from
  `InitializeSetup`. Running the real setup from a non-interactive shell just exits 2 at the
  UAC step with no log, which looks like a logic failure and isn't.

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
- **Opening it is one click** (fixed 2026-08-10): **OConnors Clash** ribbon tab → **Clash Engine**,
  both carrying the OConnors icon; also under Tool Add-ins. No more ticking View → Windows.
- **Learning pipeline**: `BatchClashExtractPlugin` (run headless via `tools\BatchExtractor` or the
  `tools\NwdClashLearner` GUI over coordinated NWDs) extracts per clash: each side's element kind
  (cat/family/type/system + diameter band), assignee, status, **clearance gap (mm, signed; <0 =
  penetration)**, **grid cell**, **level**, plus **family / type / leaf names** and **raw bore
  (`diaMm` min/max)** → one `clash_kinds.jsonl`. That data is mined into the per-test element-pair
  rule set (the `clashre-kind-rules/1` JSON the user imports) by **`tools\analyze_clashes.py`**
  (run via `tools\run-analyze.ps1`, which finds a real Python past the 0-byte MS-Store stub) —
  emits the importable rule JSON (two-tier per-test `testRules` fine→category, `tests` defaults,
  calibrated `approve` block, a `_confidence` replay block, an `_aiHandoff` block) AND a
  `clash_analysis_report.txt` (confidence, per-test coverage, approve calibration, **service-type ×
  clearance** breakdown, diameter-split suggestions). Rules are mined per CANONICAL trade pair and
  only where a pair DEVIATES from the test's dominant assignee (no blanket rules; the default is the
  safety net). **Specific-trade preference**: process/review labels (COORD-CHECK, *CLEARANCE CHECK*,
  bare levels, etc.) are NEVER a rule/default target — `is_specific`/`pick_target` prefer a real trade,
  so an easy fire-vs-elec hanger gets FIRE/ELEC, not "COORD-CHECK". **Confidence**: `replay()` scores
  the rules against history ("reproduces 82% of your specific calls"; per-test, lowest first) — the
  number for an Apply step. **AI handoff**: `_aiHandoff` lists low-confidence + soft-default tests so
  the deterministic miner (grounded proposer) hands the residue to the AI step (judgment/naming).
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
1. **One easy EXE** (the big one — FULL PLAN in [docs/combined-exe-plan.md](docs/combined-exe-plan.md)):
   combine extract→analyze→apply
   into a single desktop app. `tools\NwdClashLearner` already does the extract half; the work is to
   **PORT `tools\analyze_clashes.py` mining logic to C#** (zero Python dependency — the MS-Store stub /
   stale-PATH friction must never reach a customer), then output both a reviewable file AND an "Apply
   now" button that writes the global `%AppData%\ClashRuleEngine\config.clashre` (REPLACE, never append),
   gated by the replay-confidence line ("reproduces 82% of your calls"). The Python analyzer's logic is
   now FINAL (specific-preference, confidence, AI handoff) → the port is a clean translation.
   A clickable HTML prototype of the cockpit lives at
   [docs/prototype/clash-control-center.html](docs/prototype/clash-control-center.html) — it reads the
   REAL generated rules file (Load-rules-file button) and mocks the rest. The plan also covers a
   **per-trade report + drafter directory + send** module (§3.7) — the OUTBOUND counterpart to the
   close-out tool (push each trade their open/hard/priority clashes by email; they reply → close-out).
2. **Family/size refinement** — split the MIXED pairs by Revit family / diameter (needs a re-extract
   with `diaMm` populated; the analyzer's diameter-split section is wired and waiting on that data).
3. **Wire the AI handoff** — feed `_aiHandoff` (low-confidence + soft-default tests) to the existing
   AI rule generator so the deterministic miner proposes and the AI refines the residue.
4. **Clash close-out tool** (FULL PLAN in [docs/closeout-tool-plan.md](docs/closeout-tool-plan.md)):
   import a `clashre-closeout/1` file (AI-generated from trade emails) → preview → apply
   approve/reassign/status/comment per clash via the EXISTING `WriteBack` (match by GUID/name/fuzzy,
   with an audit stamp). Most plumbing already exists.
5. **In-panel rule editing UX** for the per-test pair rules (add/disable/reorder, see confidence).
4. **In-document stamping** (optional) — per-clash outcome onto model items via the COM
   `InwGUIPropertyNode2` bridge (config stays in the global `.clashre` store).

## Clash test pairs (real project models, "_X vs _Y" naming)
Service-vs-service: `_ELEC vs _MECH`, `_FIRE vs _MECH`, `_ELEC vs _FIRE`, `_FIRE vs _HYD`, `_ICT vs _MECH`,
`_ELEC vs _ICT`, `_HYD vs _MECH`, `_MECH vs _SEC`, `_ELEC vs _HYD`, `_ELEC vs _SEC`, `_FIRE vs _ICT`,
`_ICT vs _SEC`, `_FIRE vs _SEC`, `_ELEC vs _FUEL`, `_HYD vs _ICT` … and each `_X vs _STR` (structure).

Trades: ELEC = Electrical, MECH = Mechanical, FIRE = Fire, HYD = Hydraulic, ICT = Comms/Data,
SEC = Security, FUEL = Fuel, STR = Structure, DRUPS = diverse/redundant UPS power (a cable-tray
subset of electrical, not separable from ELEC by element kind alone).
