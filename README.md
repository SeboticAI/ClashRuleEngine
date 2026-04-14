# Navisworks Clash Rule Engine Add-in

A dockable panel plugin for Autodesk Navisworks that provides a **rules-based clash grouping and assignment engine** with drag-and-drop priority ordering.

## Features

- **Rule Builder** — Create rules based on element properties (Category, Family, Type, Parameter values) with conditions (equals, contains, greater than, less than, etc.)
- **Drag & Drop Priority** — Reorder rules by dragging them up/down. Higher rules take precedence.
- **Group + Assignee** — Each rule assigns clashes to a named group AND a team member/discipline.
- **Persistent Storage** — Rules are saved inside the Navisworks file via `DocumentUserData`.
- **Batch Processing** — Run all rules against clash test results in one click.
- **Dockable Panel** — Lives inside Navisworks as a dockable tool panel.

## Project Structure

```
ClashRuleEngine/
├── ClashRuleEngine.csproj          # Project file
├── Plugin/
│   └── ClashRuleEnginePlugin.cs    # Navisworks DockPanePlugin entry point
├── Models/
│   ├── ClashRule.cs                # Rule data model
│   ├── RuleCondition.cs            # Individual condition (property/operator/value)
│   └── RuleSet.cs                  # Collection of rules with serialization
├── Services/
│   ├── ClashProcessingService.cs   # Processes clashes against rules
│   └── RulePersistenceService.cs   # Save/load rules to NW file
├── UI/
│   ├── RuleEnginePanel.xaml        # Main dockable panel UI
│   ├── RuleEnginePanel.xaml.cs     # Code-behind
│   ├── RuleEditorDialog.xaml       # Rule creation/editing dialog
│   ├── RuleEditorDialog.xaml.cs    # Code-behind
│   └── Converters.cs               # WPF value converters
├── PackageContents.xml             # Navisworks plugin manifest
└── README.md
```

## Build Instructions

1. Open in Visual Studio 2022
2. Add references to (from Navisworks install directory):
   - `Autodesk.Navisworks.Api.dll`
   - `Autodesk.Navisworks.Clash.dll`
   - `Autodesk.Navisworks.Automation.dll`
3. Set **Copy Local = False** for all Navisworks references
4. Build as x64
5. Copy output DLL + PackageContents.xml to:
   `%APPDATA%\Autodesk Navisworks Manage 2025\Plugins\ClashRuleEngine\`

## Usage

1. Open Navisworks → Home tab → look for "Clash Rule Engine" in the Tools panel
2. Click to open the dockable panel
3. Click **+ New Rule** to create a rule
4. Define conditions (e.g., Diameter > 100, System Type = Drainage)
5. Set a group name and assignee
6. Drag rules to set priority order
7. Click **Run Rules** to process all clash tests
