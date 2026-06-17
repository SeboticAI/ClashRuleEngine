using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace ClashRuleEngine.NwdClashLearner
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // A standalone exe can't find Autodesk.Navisworks.* (they live in the
            // Navisworks install folder, not next to the exe). Resolve them from the
            // install dir BEFORE any Navisworks type is touched.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveNavisworks;

            // Never die silently — surface any startup/thread error.
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
            Application.ThreadException += (s, e) => ShowError(e.Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); }
            catch (Exception ex) { ShowError(ex); }
        }

        private static void ShowError(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    (ex != null ? ex.ToString() : "Unknown error"),
                    "NWD Clash Learner - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static Assembly ResolveNavisworks(object sender, ResolveEventArgs args)
        {
            string shortName;
            try { shortName = new AssemblyName(args.Name).Name; } catch { return null; }
            if (string.IsNullOrEmpty(shortName) ||
                !shortName.StartsWith("Autodesk.Navisworks", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (var dir in NavisworksDirs())
            {
                var p = Path.Combine(dir, shortName + ".dll");
                if (File.Exists(p))
                {
                    try { return Assembly.LoadFrom(p); } catch { }
                }
            }
            return null;
        }

        private static IEnumerable<string> NavisworksDirs()
        {
            const string root = @"C:\Program Files\Autodesk";
            if (!Directory.Exists(root)) yield break;
            // Newest version first.
            var dirs = Directory.GetDirectories(root, "Navisworks Manage*").OrderByDescending(d => d);
            foreach (var d in dirs) yield return d;
        }
    }
}
