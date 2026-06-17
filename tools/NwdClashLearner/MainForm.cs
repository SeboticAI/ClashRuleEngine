using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Automation;

namespace ClashRuleEngine.NwdClashLearner
{
    /// <summary>
    /// Standalone desktop tool: add (or drag in) coordinated NWDs, click Generate, and
    /// it opens each headlessly via the Navisworks Automation engine, extracts how every
    /// clash was assigned by element kind, and writes one summary JSONL. That JSONL is
    /// the training data for the element-kind rule hierarchy.
    ///
    /// Needs the extractor plugin (ClashRuleEngine.dll, with ClashBatchExtract) — it is
    /// auto-located next to this exe or in the repo build output, or you can browse to it.
    /// Navisworks must be CLOSED (the engine launches its own instance; uses a Manage licence).
    /// </summary>
    public class MainForm : Form
    {
        private const string PluginId = "ClashBatchExtract.ACME";

        private readonly ListBox _files = new ListBox();
        private readonly TextBox _output = new TextBox();
        private readonly TextBox _plugin = new TextBox();
        private readonly TextBox _log = new TextBox();
        private readonly Button _generate = new Button();
        private readonly Button _addFiles = new Button();
        private readonly Button _addFolder = new Button();
        private readonly Button _remove = new Button();

        public MainForm()
        {
            Text = "NWD Clash Learner";
            Width = 820;
            Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            var lblFiles = new Label { Text = "NWD files (drag files here, or Add):", Left = 12, Top = 10, Width = 400, AutoSize = true };

            _files.Left = 12; _files.Top = 32; _files.Width = 660; _files.Height = 180;
            _files.SelectionMode = SelectionMode.MultiExtended;
            _files.AllowDrop = true;
            _files.HorizontalScrollbar = true;
            _files.DragEnter += OnDragEnter;
            _files.DragDrop += OnDragDrop;

            _addFiles.Text = "Add NWDs..."; _addFiles.Left = 684; _addFiles.Top = 32; _addFiles.Width = 110; _addFiles.Height = 28;
            _addFiles.Click += (s, e) => AddFilesDialog();
            _addFolder.Text = "Add Folder..."; _addFolder.Left = 684; _addFolder.Top = 66; _addFolder.Width = 110; _addFolder.Height = 28;
            _addFolder.Click += (s, e) => AddFolderDialog();
            _remove.Text = "Remove"; _remove.Left = 684; _remove.Top = 100; _remove.Width = 110; _remove.Height = 28;
            _remove.Click += (s, e) => RemoveSelected();

            var lblOut = new Label { Text = "Output summary (.jsonl):", Left = 12, Top = 222, AutoSize = true };
            _output.Left = 12; _output.Top = 242; _output.Width = 660;
            _output.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "clash_kinds.jsonl");
            var browseOut = new Button { Text = "Browse...", Left = 684, Top = 240, Width = 110, Height = 26 };
            browseOut.Click += (s, e) => BrowseOutput();

            var lblPlugin = new Label { Text = "Extractor plugin (info only - engine uses the DEPLOYED Navisworks plugin):", Left = 12, Top = 274, AutoSize = true };
            _plugin.Left = 12; _plugin.Top = 294; _plugin.Width = 660;
            _plugin.Text = LocatePlugin();
            var browsePlugin = new Button { Text = "Browse...", Left = 684, Top = 292, Width = 110, Height = 26 };
            browsePlugin.Click += (s, e) => BrowsePlugin();

            _generate.Text = "Generate Summary"; _generate.Left = 12; _generate.Top = 326; _generate.Width = 200; _generate.Height = 36;
            _generate.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            _generate.Click += (s, e) => StartRun();

            var lblLog = new Label { Text = "Log:", Left = 12, Top = 372, AutoSize = true };
            _log.Left = 12; _log.Top = 392; _log.Width = 782; _log.Height = 200;
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical;
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            Controls.AddRange(new Control[] {
                lblFiles, _files, _addFiles, _addFolder, _remove,
                lblOut, _output, browseOut,
                lblPlugin, _plugin, browsePlugin,
                _generate, lblLog, _log
            });
        }

        // ── file list ──────────────────────────────────────────────
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) AddFolder(p);
                else if (IsNwd(p)) AddFile(p);
            }
        }

        private static bool IsNwd(string p) => p.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase);

        private void AddFile(string p)
        {
            if (!_files.Items.Cast<string>().Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase)))
                _files.Items.Add(p);
        }

        private void AddFolder(string dir)
        {
            try { foreach (var f in Directory.GetFiles(dir, "*.nwd", SearchOption.AllDirectories)) AddFile(f); }
            catch (Exception ex) { Log("Folder error: " + ex.Message); }
        }

        private void AddFilesDialog()
        {
            using (var d = new OpenFileDialog { Filter = "Navisworks (*.nwd)|*.nwd", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) AddFile(f);
        }

        private void AddFolderDialog()
        {
            using (var d = new FolderBrowserDialog { Description = "Pick a folder of NWDs (recursed)" })
                if (d.ShowDialog(this) == DialogResult.OK) AddFolder(d.SelectedPath);
        }

        private void RemoveSelected()
        {
            for (int i = _files.SelectedIndices.Count - 1; i >= 0; i--)
                _files.Items.RemoveAt(_files.SelectedIndices[i]);
        }

        private void BrowseOutput()
        {
            using (var d = new SaveFileDialog { Filter = "JSON Lines (*.jsonl)|*.jsonl", FileName = "clash_kinds.jsonl" })
                if (d.ShowDialog(this) == DialogResult.OK) _output.Text = d.FileName;
        }

        private void BrowsePlugin()
        {
            using (var d = new OpenFileDialog { Filter = "ClashRuleEngine.dll|ClashRuleEngine.dll|All (*.dll)|*.dll" })
                if (d.ShowDialog(this) == DialogResult.OK) _plugin.Text = d.FileName;
        }

        /// <summary>Find ClashRuleEngine.dll next to the exe or in the repo build output.</summary>
        private static string LocatePlugin()
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var here = Path.Combine(exeDir, "ClashRuleEngine.dll");
            if (File.Exists(here)) return here;

            // Walk up looking for bin\x64\Release\<ver>\ClashRuleEngine.dll
            try
            {
                var dir = new DirectoryInfo(exeDir);
                for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
                {
                    var bin = Path.Combine(dir.FullName, "bin", "x64", "Release");
                    if (Directory.Exists(bin))
                    {
                        var hit = Directory.GetFiles(bin, "ClashRuleEngine.dll", SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                }
            }
            catch { }
            return "";
        }

        // ── run ────────────────────────────────────────────────────
        private void StartRun()
        {
            var files = _files.Items.Cast<string>().Where(File.Exists).ToList();
            if (files.Count == 0) { MessageBox.Show(this, "Add at least one NWD."); return; }
            string output = _output.Text.Trim();
            if (string.IsNullOrWhiteSpace(output)) { MessageBox.Show(this, "Choose an output file."); return; }
            string plugin = _plugin.Text.Trim();   // informational only; engine uses the INSTALLED plugin

            var roamer = Process.GetProcessesByName("Roamer");
            if (roamer.Length > 0 &&
                MessageBox.Show(this, "Navisworks appears to be running. Close it first (the tool launches its own instance). Continue anyway?",
                    "Navisworks running", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            SetBusy(true);
            _log.Clear();
            var t = new Thread(() =>
            {
                try { RunBatch(files, output, plugin); }
                catch (Exception ex) { Log("FATAL: " + ex); Done(output, false); }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);   // Navisworks Automation is STA/COM
            t.Start();
        }

        private void RunBatch(List<string> files, string output, string plugin)
        {
            int ok = 0, fail = 0;
            try
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                Log($"Starting Navisworks engine for {files.Count} file(s)...");

                NavisworksApplication app = null;
                bool notFound = false;
                try
                {
                    app = new NavisworksApplication();
                    try { app.DisableProgress(); } catch { }

                    for (int i = 0; i < files.Count; i++)
                    {
                        var f = files[i];
                        Log($"[{i + 1}/{files.Count}] {Path.GetFileName(f)}");
                        try
                        {
                            app.OpenFile(f, new string[0]);
                            app.ExecuteAddInPlugin(PluginId, new[] { output });
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            Log("   ERROR: " + ex.Message);
                            if (ex.Message.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0) notFound = true;
                        }
                    }
                }
                finally { try { app?.Dispose(); } catch { } }

                if (notFound)
                    Log("\n>> The extractor plugin is not installed in Navisworks. Deploy the CURRENT build:\n" +
                        "   run tools\\deploy.ps1 -Version 2027 (elevated), then retry. (Navisworks 2027 only\n" +
                        "   loads plugins from its install Plugins folder.)");

                long recs = 0;
                try { if (File.Exists(output)) recs = File.ReadLines(output).Count(); } catch { }
                Log($"Done. {ok} processed, {fail} failed. {recs} record(s) -> {output}");
                Done(output, ok > 0);
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex.Message);
                Done(output, false);
            }
        }

        // ── ui marshalling ─────────────────────────────────────────
        private void Log(string msg)
        {
            if (IsDisposed) return;
            try { BeginInvoke((Action)(() => { _log.AppendText(msg + Environment.NewLine); })); } catch { }
        }

        private void Done(string output, bool success)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    SetBusy(false);
                    if (success && File.Exists(output))
                    {
                        if (MessageBox.Show(this, "Summary written:\n" + output + "\n\nOpen its folder?",
                            "Done", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                            try { Process.Start("explorer.exe", "/select,\"" + output + "\""); } catch { }
                    }
                    else MessageBox.Show(this, "Finished with no output. Check the log — do the NWDs contain assigned clash tests?",
                        "Done", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            }
            catch { }
        }

        private void SetBusy(bool busy)
        {
            _generate.Enabled = !busy;
            _generate.Text = busy ? "Working..." : "Generate Summary";
            _addFiles.Enabled = _addFolder.Enabled = _remove.Enabled = !busy;
            UseWaitCursor = busy;
        }
    }
}
