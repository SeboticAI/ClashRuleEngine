// Offline plugin-load prober: loads the plugin DLL with dependencies resolved
// from the Navisworks install folder (like the real loader) and reports type
// load failures and plugin attribute metadata. Catches "scanner silently
// skipped the DLL" causes without restarting Navisworks.
using System;
using System.IO;
using System.Linq;
using System.Reflection;

static class PluginProbe
{
    static string nwDir = @"C:\Program Files\Autodesk\Navisworks Manage 2027";

    static void Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: PluginProbe <plugin.dll> [nwDir]"); return; }
        if (args.Length > 1) nwDir = args[1];

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string cand = Path.Combine(nwDir, name);
            if (File.Exists(cand)) return Assembly.LoadFrom(cand);
            return null;
        };

        Console.WriteLine("Probing: " + args[0]);
        var asm = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Console.WriteLine("Assembly: " + asm.FullName);
        Console.WriteLine("References:");
        foreach (var r in asm.GetReferencedAssemblies().Where(r => r.Name.StartsWith("Autodesk")))
            Console.WriteLine("  " + r.FullName);

        Type[] types;
        try
        {
            types = asm.GetTypes();
            Console.WriteLine("GetTypes OK: " + types.Length + " types");
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.WriteLine("!! TYPE LOAD FAILURES (" + ex.LoaderExceptions.Length + "):");
            foreach (var le in ex.LoaderExceptions.Take(15))
                Console.WriteLine("   " + le.Message);
            types = ex.Types.Where(t => t != null).ToArray();
            Console.WriteLine("Partially loaded: " + types.Length + " types");
        }

        foreach (var t in types)
        {
            System.Collections.Generic.IList<CustomAttributeData> attrs;
            try { attrs = CustomAttributeData.GetCustomAttributes(t); }
            catch (Exception ex)
            {
                Console.WriteLine("!! attribute read failed on " + t.FullName + ": " + ex.Message);
                continue;
            }
            var pluginish = attrs.Where(a => a.AttributeType.FullName != null
                                          && a.AttributeType.FullName.StartsWith("Autodesk.Navisworks.Api.Plugins")).ToList();
            if (pluginish.Count == 0) continue;

            Console.WriteLine("PLUGIN TYPE: " + t.FullName);
            var b = t;
            var chain = "";
            while (b != null && b != typeof(object)) { chain += (chain == "" ? "" : " -> ") + b.Name; b = b.BaseType; }
            Console.WriteLine("  base chain: " + chain);
            Console.WriteLine("  public: " + t.IsPublic + "  abstract: " + t.IsAbstract + "  has public default ctor: "
                              + (t.GetConstructor(Type.EmptyTypes) != null));
            foreach (var a in pluginish)
                Console.WriteLine("  attr: " + a.ToString());
        }
        Console.WriteLine("PROBE DONE");
    }
}
