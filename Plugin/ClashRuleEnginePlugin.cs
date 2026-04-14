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
    }

    [Plugin("ClashRuleEngineRibbon", "ACME",
        DisplayName = "Clash Rule Engine",
        ToolTip = "Open the Clash Rule Engine panel")]
    [RibbonLayout("ClashRuleEngineRibbon.xaml")]
    [RibbonTab("ID_ClashRuleEngine_Tab", DisplayName = "Clash Rules")]
    [Command("ID_OpenPanel", DisplayName = "Open Panel", ToolTip = "Open the Clash Rule Engine dockable panel")]
    public class ClashRuleEngineRibbonHandler : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            if (commandId == "ID_OpenPanel")
            {
                var rec = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin("ClashRuleEngine.ACME");
                if (rec != null && rec.LoadedPlugin == null) rec.LoadPlugin();
            }
            return 0;
        }

        public override bool TryShowCommandHelp(string commandId) { return false; }
    }
}
