# OConnors Clash Engine — Navisworks add-in

<img src="Resources/oconnors_clash_256.png" width="96" align="right" alt="OConnors Clash Engine">

A dockable Navisworks Manage panel that **assigns, auto-approves and groups clash results**
from rules learned off previously-coordinated models — instead of by hand, clash by clash.

- **Per-test element-pair rules.** For each clash test, the *actual clashing elements* decide
  the owner: "in `_ELEC vs _HYD`, a Cable Tray Fitting vs a Pipe → ELEC." Rules are mined from
  ~28 coordinated NWDs. **Ambiguous pairs are left unassigned on purpose** — the tool never
  guesses at something a coordinator should look at.
- **Auto-approve.** Clearance-gated (gap ≥ a per-pair floor, default 50 mm), plus always-approve
  element kinds that simply bend (Flex Pipe/Duct). Penetrations and `_X vs _STR` (structure) are
  **never** auto-approved.
- **Grid grouping.** Bundles are named by the bare grid intersection ("H-22"), which is how
  coordination meetings actually navigate a model.
- **Safe writes.** One atomic transaction per clash test, using the SDK-supported
  copy-and-replace pattern. Re-runnable and idempotent; a failure leaves the document untouched.

---

## Install (team members)

1. Get `OConnorsClashEngine_Setup_<version>.exe`.
2. **Close Navisworks.** The setup will refuse to continue while it is running — a plugin that
   is already loaded is locked on disk, so installing over it would leave the old version.
3. Run the setup and accept the admin prompt (it writes into the Navisworks install folder,
   the only location Navisworks 2027 loads third-party plugins from).
4. It auto-detects your Navisworks versions and installs for each one it has a build for. If you
   have a version it does not cover, it says so instead of silently skipping.
5. Start Navisworks → **Clash Rule Engine** tab → **Open Panel**.

The panel opens on click — no ticking boxes in View → Windows.

Supported and shipping: **Navisworks Manage 2026 and 2027**, x64 — one build each, because the
Navisworks API assemblies are strong-named per release. The setup.exe lists what it contains on
the welcome screen, and warns if it finds a Navisworks version it has no build for.

### Uninstall
Settings → Apps → *OConnors Clash Engine*. Your rules are **not** deleted; they live in
`%AppData%\ClashRuleEngine\config.clashre`.

---

## Using it

1. **Nothing to import — the rules ship inside the plugin.** On first run the panel loads the
   **Standard** rule set automatically, so it works straight after install. Your config lives at
   `%AppData%\ClashRuleEngine\config.clashre` and follows you across models and sessions.
   The **Rule set** picker (above the tabs) switches between the shipped sets:
   - **Standard — in production** *(default)*: 923 element-pair rules + 25 per-test defaults,
     auto-approve at 50 mm. The set used on coordinated jobs.
   - **Mined v2 — trial**: 274 deviation-only rules + 25 defaults, reproduces 81.8% of past
     calls, auto-approves from 25 mm with per-pair floors — so it **approves more**. Compare it
     on one test before adopting it.

   Switching **replaces** all per-test rules (it warns first) and changes nothing already written
   into the model until you Run again. **Import** is still there for one-off rule files.
2. **Pick a grouping mode** in the bar above the tabs — **Grid** is the recommended one. It
   applies to all tests.
3. **Run rules** on the selected test or all tests. Per clash: assign → approve → group, written
   back in one transaction per test.
4. Clashes whose element pair matches no rule stay **unassigned** — that is the review queue.

Tabs: **Rules** (the per-test pair rules) · **Clashes** (list, 3D markers, inspector) ·
**General** (exports, AI assist).

A banner above the rule list says what a Run will actually do for the selected test — the default
assignee and how many pair rules override it. **A test showing no pair rules is usually not
broken**: rules only encode the *exceptions* to a test's default assignee, and structure (`_STR`)
tests carry none at all by design.

### Batch Extract (ribbon tab → Batch Extract)

Records this model's clash data — each side's element kind, assignee, status, clearance gap, grid
and level — to a `.jsonl` file, which is the input for mining a rule set
(`tools\run-analyze.ps1`). It **appends**, so several models can build one dataset; it asks before
adding to an existing file, since extracting the same model twice would count it twice. For many
models at once, use `tools\NwdClashLearner` instead — it drives Navisworks headlessly.

---

## Developing

Requires VS 2022 Build Tools (or VS 2026) and Navisworks Manage installed. `.NET Framework 4.8`,
classic-style csproj, x64 only.

```powershell
tools\build.ps1                      # build + deploy one version (default 2027)
tools\build-all.ps1                  # build every version this machine can target
tools\build-all.ps1 -Installer       # ...and compile the setup.exe
tools\harvest-refs.ps1 -List         # which versions are installed / harvested
tools\make-icons.ps1                 # regenerate ribbon + installer icons from the logo
tools\stage-rulesets.ps1             # refresh the rule sets EMBEDDED in the plugin
```

Rule sets are embedded resources (`Resources\RuleSets\`), so a user never has to be sent a file.
`stage-rulesets.ps1` refreshes them from a live config + the newest analyzer output and **always
scrubs the `ApiKey`** — never hand-copy a `.clashre` into `Resources\RuleSets\`, or you ship
someone's Claude key inside the DLL. Rebuild afterwards to embed the new content.

### Covering more Navisworks versions
One build per Navisworks release is **unavoidable**: the API assemblies are strong-named per
release (2024 = `21.0.0.0` … 2027 = `24.0.0.0`) and `roamer.exe.config` pins each with
`publisherPolicy apply="no"`, so a 2027-built DLL cannot load in 2026.

**Easiest: install that Navisworks version on the build machine.** `build-all.ps1` detects it
and builds for it — no configuration, no flags. `tools\harvest-refs.ps1 -List` shows what's
covered.

If you can't install it here, harvest just its reference DLLs instead (only
`Autodesk.Navisworks.Api.dll` and `.Clash.dll` are actually required):

```powershell
# on a machine that has Navisworks Manage 2026:
tools\harvest-refs.ps1 -Version 2026
# or from elsewhere:
tools\harvest-refs.ps1 -Version 2026 -From "\\host\c$\Program Files\Autodesk\Navisworks Manage 2026"
```

They land in `Refs\2026\`, which is **gitignored** — Autodesk binaries stay on the build machine.
Either way, the installer packages exactly the versions that built, and says which ones those
were.

### Shipping the installer
```powershell
winget install --id JRSoftware.InnoSetup   # installs PER-USER, under %LocalAppData%\Programs
tools\build-all.ps1 -Installer
# -> Installer\Output\OConnorsClashEngine_Setup_<version>.exe
```

`CLAUDE.md` carries the full architecture notes and the hard-won Navisworks API quirks — read it
before changing plugin loading, clash write-back, or the ribbon.
