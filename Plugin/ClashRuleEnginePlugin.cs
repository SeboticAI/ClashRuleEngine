using System;
using System.Windows.Forms.Integration;
using Autodesk.Navisworks.Api.Plugins;

namespace ClashRuleEngine.Plugin
{
    [Plugin("ClashRuleEngine", "ACME",
        DisplayName = "Clash Rule Engine",
        ToolTip = "Rule-based clash grouping and assignment")]
    [DockPanePlugin(800, 600, FixedSize = false)]
    public class ClashRuleEnginePlugin : DockPanePlugin
    {
        private ElementHost _host;
        private UI.RuleEnginePanel _panel;

        public override System.Windows.Forms.Control CreateControlPane()
        {
            _host = new ElementHost { AutoSize = true, Dock = System.Windows.Forms.DockStyle.Fill };
            _panel = new UI.RuleEnginePanel();
            _host.Child = _panel;
            return _host;
        }

        public override void DestroyControlPane(System.Windows.Forms.Control pane)
        {
            _panel = null;
            if (_host != null) { _host.Dispose(); _host = null; }
        }

        /// <summary>
        /// Show the dock pane from a ribbon button. Closing the pane UNLOADS the plugin in
        /// Navisworks (the View → Windows tick is just load/unload), so loading it when it's
        /// not loaded re-opens it. The API exposes no separate show/activate for a pane.
        /// </summary>
        internal static void ShowPanel()
        {
            try
            {
                var rec = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin("ClashRuleEngine.ACME")
                          as DockPanePluginRecord;
                if (rec != null && rec.LoadedPlugin == null) rec.LoadPlugin();
            }
            catch { }
        }
    }

    [Plugin("ClashRuleEngineRibbon", "ACME",
        DisplayName = "OConnors Clash",
        ToolTip = "Open the OConnors Clash panel")]
    [RibbonLayout("ClashRuleEngineRibbon.xaml")]
    [RibbonTab("ID_ClashRuleEngine_Tab", DisplayName = "OConnors Clash")]
    [Command("ID_OpenPanel", DisplayName = "Clash Engine", ToolTip = "Open the OConnors Clash panel")]
    public class ClashRuleEngineRibbonHandler : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            if (commandId == "ID_OpenPanel") ClashRuleEnginePlugin.ShowPanel();
            return 0;
        }

        // Always enabled (don't gate on a document being open) and always show the tab.
        public override CommandState CanExecuteCommand(string commandId) => new CommandState(true);
        public override bool CanExecuteRibbonTab(string ribbonTabId) => true;
        public override bool TryShowCommandHelp(string commandId) { return false; }
    }

    /// <summary>
    /// Ribbon button under "Tool Add-ins" (same place as Clash Batch Extract) that
    /// opens/shows the OConnors Clash dock pane — so it's a click, not a tick in
    /// the Windows menu.
    /// </summary>
    [Plugin("ClashRuleEngineShow", "ACME",
        DisplayName = "Clash Engine",
        ToolTip = "Open the Clash Rule Engine panel")]
    [AddInPlugin(AddInLocation.AddIn, LoadForCanExecute = true)]
    public class ClashRuleEngineShowPlugin : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            ClashRuleEnginePlugin.ShowPanel();
            return 0;
        }
    }
}
