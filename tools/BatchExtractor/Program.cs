using System;
using System.IO;
using System.Linq;
using Autodesk.Navisworks.Api.Automation;

namespace ClashRuleEngine.BatchExtractor
{
    /// <summary>
    /// Headless batch learner. Launches Navisworks via the Automation API, opens every
    /// NWD under a folder, and runs the in-process ClashBatchExtract AddInPlugin on each
    /// to append element-kind-vs-assignee records to one JSONL dataset.
    ///
    ///   BatchExtractor.exe &lt;nwdFolderOrFile&gt; &lt;output.jsonl&gt; [pluginDll]
    ///
    /// If pluginDll is given it's registered with AddPluginAssembly; otherwise the
    /// installed ClashRuleEngine plugin is used (so deploy it first, or pass the DLL).
    /// </summary>
    internal static class Program
    {
        private const string PluginId = "ClashBatchExtract.ACME";

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: BatchExtractor <nwdFolderOrFile> <output.jsonl> [pluginDll]");
                return 1;
            }

            string input = args[0];
            string output = Path.GetFullPath(args[1]);
            string pluginDll = args.Length > 2 ? args[2] : null;

            string[] files;
            if (Directory.Exists(input))
                files = Directory.GetFiles(input, "*.nwd", SearchOption.AllDirectories);
            else if (File.Exists(input))
                files = new[] { input };
            else { Console.WriteLine("Input not found: " + input); return 1; }

            if (files.Length == 0) { Console.WriteLine("No .nwd files under: " + input); return 1; }

            // Fresh dataset (the plugin APPENDS across files).
            try { if (File.Exists(output)) File.Delete(output); } catch { }

            Console.WriteLine($"{files.Length} NWD(s) -> {output}");
            int ok = 0, fail = 0;

            var app = new NavisworksApplication();
            try
            {
                try { app.DisableProgress(); } catch { }
                if (!string.IsNullOrWhiteSpace(pluginDll) && File.Exists(pluginDll))
                {
                    try { app.AddPluginAssembly(pluginDll); Console.WriteLine("Registered plugin: " + pluginDll); }
                    catch (Exception ex) { Console.WriteLine("AddPluginAssembly failed (will rely on installed plugin): " + ex.Message); }
                }

                for (int i = 0; i < files.Length; i++)
                {
                    string f = files[i];
                    Console.WriteLine($"[{i + 1}/{files.Length}] {Path.GetFileName(f)}");
                    try
                    {
                        app.OpenFile(f, new string[0]);
                        app.ExecuteAddInPlugin(PluginId, new[] { output });
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Console.WriteLine("    ERROR: " + ex.Message);
                    }
                }
            }
            finally
            {
                try { app.Dispose(); } catch { }
            }

            Console.WriteLine($"Done. {ok} processed, {fail} failed -> {output}");
            return 0;
        }
    }
}
