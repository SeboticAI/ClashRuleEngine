using System;
using System.Windows.Forms.Integration;
using Autodesk.Navisworks.Api.Plugins;

namespace ClashRuleEngine.Plugin
{
    [Plugin(PluginIds.PanelName, PluginIds.Developer,
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
        /// Open the dock pane and actually show it, from a ribbon/add-in button.
        ///
        /// LOADING THE PLUGIN IS NOT ENOUGH — that was the long-standing "janky button"
        /// bug: LoadPlugin() only instantiates the pane, leaving Visible false, so the
        /// user still had to tick View → Windows → Clash Rule Engine by hand. The pane
        /// is shown by setting DockPanePlugin.Visible (that tick box IS this property),
        /// and ActivatePane() brings it to the front when it is docked behind a sibling
        /// tab. Pattern from the SDK sample
        /// api\NET\examples\PlugIns\ClashDetective\EventLog\LogDockPaneAddin.cs.
        ///
        /// Idempotent: safe to click when the pane is already open (it just gets focus).
        /// </summary>
        internal static void ShowPanel()
        {
            try
            {
                // Automation hosts (tools\BatchExtractor) have no GUI to dock into.
                if (Autodesk.Navisworks.Api.Application.IsAutomated) return;

                var rec = Autodesk.Navisworks.Api.Application.Plugins
                          .FindPlugin(PluginIds.Panel) as DockPanePluginRecord;
                if (rec == null || !rec.IsEnabled) return;

                // TryLoadPlugin returns null instead of throwing if construction fails.
                var pane = rec.LoadedPlugin ?? rec.TryLoadPlugin();
                if (pane == null) return;

                pane.Visible = true;
                pane.ActivatePane();
            }
            catch (Exception ex)
            {
                // Never let a failed open take Navisworks down; tell the user why.
                System.Windows.Forms.MessageBox.Show(
                    "Could not open the Clash Rule Engine panel.\r\n\r\n" + ex.Message,
                    "Clash Rule Engine",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }
    }

    [Plugin(PluginIds.RibbonName, PluginIds.Developer,
        DisplayName = "Clash Rule Engine",
        ToolTip = "Clash Rule Engine — rule-based clash assignment, approval and grouping")]
    // BOTH of these name LOOSE FILES that Navisworks loads from a locale subfolder next to
    // the DLL — Plugins\ClashRuleEngine\en-US\. They are NOT embedded resources; treating
    // the layout as one is why this tab never appeared before 2026-08-10. See the csproj.
    [RibbonLayout("ClashRuleEngineRibbon.xaml")]
    [Strings("ClashRuleEngine.name")]
    // The tab's visible name comes from Title in ClashRuleEngineRibbon.xaml, which
    // overrides this DisplayName; both are set to the same thing so they can't drift.
    [RibbonTab("ID_ClashRuleEngine_Tab", DisplayName = "Clash Rule Engine")]
    // Icon/LargeIcon are resolved by Navisworks RELATIVE TO THE PLUGIN DLL (or an
    // Images\ subfolder next to it), so these files must be deployed alongside
    // ClashRuleEngine.dll — the csproj copies them and deploy.ps1/the installer ship
    // them. CallCanExecute.Always keeps the button live with no document open (when
    // CanExecuteCommand is never called, the command defaults to DISABLED).
    [Command("ID_OpenPanel",
        DisplayName = "Open Panel",
        ToolTip = "Open the Clash Rule Engine panel",
        ExtendedToolTip = "Assign, auto-approve and group clash results from learned per-test element-pair rules.",
        Icon = "oconnors_clash_16.ico",
        LargeIcon = "oconnors_clash_32.ico",
        CallCanExecute = CallCanExecute.Always,
        // Load at startup so CanExecuteCommand below is definitely called. The SDK docs
        // say commands are enabled by default but ALSO that an uncalled
        // CanExecuteCommand leaves the command disabled; this removes the ambiguity, and
        // this handler is trivial (it does not build the panel).
        LoadForCanExecute = true)]
    // CallCanExecute.DocumentNotClear means Navisworks only calls CanExecuteCommand for this one
    // when a model is open — and an uncalled CanExecuteCommand leaves a command DISABLED, so the
    // button greys itself out with no document, for free and correctly.
    [Command("ID_BatchExtract",
        DisplayName = "Batch Extract",
        ToolTip = "Extract this model's clash data to a .jsonl learning file",
        ExtendedToolTip = "For every clash, records each side's element kind, the assignee, status, "
                        + "clearance gap, grid and level. Feed the file to tools\\analyze_clashes.py "
                        + "to mine a rule set.",
        Icon = "oconnors_extract_16.ico",
        LargeIcon = "oconnors_extract_32.ico",
        CallCanExecute = CallCanExecute.DocumentNotClear,
        LoadForCanExecute = true)]
    public class ClashRuleEngineRibbonHandler : CommandHandlerPlugin
    {
        public override int ExecuteCommand(string commandId, params string[] parameters)
        {
            switch (commandId)
            {
                case "ID_OpenPanel": ClashRuleEnginePlugin.ShowPanel(); break;
                case "ID_BatchExtract": RunBatchExtract(); break;
            }
            return 0;
        }

        /// <summary>
        /// Interactive front end for BatchClashExtractPlugin, which is otherwise headless
        /// (AddInLocation.None) and driven by Automation from tools\BatchExtractor and
        /// tools\NwdClashLearner over many NWDs. This runs it against the OPEN model only.
        ///
        /// The plugin itself deliberately swallows every exception so one bad file cannot abort a
        /// batch — which means a click would otherwise give no feedback whatsoever. Hence the
        /// row count / -1 contract and the reporting here.
        /// </summary>
        private static void RunBatchExtract()
        {
            const string title = "Clash Batch Extract";
            try
            {
                var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (doc == null || doc.IsClear)
                {
                    System.Windows.Forms.MessageBox.Show("Open a model with clash results first.",
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                    return;
                }

                var dlg = new System.Windows.Forms.SaveFileDialog
                {
                    Title = "Extract clash data to",
                    Filter = "Clash learning data (*.jsonl)|*.jsonl|All files (*.*)|*.*",
                    FileName = "clash_kinds.jsonl",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    OverwritePrompt = false,   // we APPEND — see below
                    AddExtension = true,
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                string outPath = dlg.FileName;

                // The extractor APPENDS, by design, so a run over many models accumulates into one
                // dataset. That makes picking an existing file a real choice rather than a mistake,
                // but re-running the SAME model into it would double-count — so ask.
                bool existed = System.IO.File.Exists(outPath);
                if (existed)
                {
                    var answer = System.Windows.Forms.MessageBox.Show(
                        "That file already exists and this ADDS to it (so several models can build "
                        + "one dataset).\r\n\r\nContinue appending?\r\n\r\nChoose No to pick a "
                        + "different file — extracting the same model twice would count it twice.",
                        title, System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Question);
                    if (answer != System.Windows.Forms.DialogResult.Yes) return;
                }

                var rec = Autodesk.Navisworks.Api.Application.Plugins
                          .FindPlugin(PluginIds.BatchExtract) as AddInPluginRecord;
                if (rec == null)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "The extractor plugin '" + PluginIds.BatchExtract + "' was not found.",
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }
                var plugin = rec.LoadedPlugin ?? rec.TryLoadPlugin();
                if (plugin == null)
                {
                    System.Windows.Forms.MessageBox.Show("The extractor plugin failed to load.",
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Warning);
                    return;
                }

                int rows;
                var previous = System.Windows.Forms.Cursor.Current;
                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;
                try { rows = plugin.Execute(outPath); }
                finally { System.Windows.Forms.Cursor.Current = previous; }

                if (rows < 0)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "Extraction failed while reading this model's clash results. Nothing was "
                        + "written.\r\n\r\n" + outPath,
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                else if (rows == 0)
                {
                    System.Windows.Forms.MessageBox.Show(
                        "No clash results found to extract — run the clash tests first.",
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
                else
                {
                    System.Windows.Forms.MessageBox.Show(
                        string.Format("{0:N0} row(s) {1} to:\r\n\r\n{2}\r\n\r\n"
                            + "Each row is one (test x element-pair x assignee x status x gap band) "
                            + "combination. Mine a rule set from it with tools\\run-analyze.ps1.",
                            rows, existed ? "appended" : "written", outPath),
                        title, System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show("Batch extract failed.\r\n\r\n" + ex.Message,
                    title, System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }

        // Enabled whenever asked. ID_OpenPanel is asked always; ID_BatchExtract only when a
        // document is open (see its CallCanExecute), which is exactly the gating we want.
        public override CommandState CanExecuteCommand(string commandId) => new CommandState(true);
        public override bool CanExecuteRibbonTab(string ribbonTabId) => true;
        public override bool TryShowCommandHelp(string commandId) { return false; }
    }

    // REMOVED 2026-08-10: ClashRuleEngineShowPlugin, an AddInPlugin that also opened the
    // panel. It existed only as a fallback while the custom ribbon tab did not work (see the
    // csproj note on the ribbon layout being a loose file). Now that the "Clash Rule Engine"
    // tab appears, that second button was a duplicate sitting in Navisworks' stock
    // "Tool add-ins 1" panel — which cannot be renamed and looked unfinished next to the
    // branded tab. The tab is the single entry point.
}
