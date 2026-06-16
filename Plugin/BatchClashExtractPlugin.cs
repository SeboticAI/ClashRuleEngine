using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.Plugins;

namespace ClashRuleEngine.Plugin
{
    /// <summary>
    /// Headless batch extractor — run via the Automation API over many NWDs to LEARN
    /// how clashes are assigned. For every clash in the open document it records the
    /// element KIND of each side (category / family / type / system + diameter band)
    /// and who it was assigned to, aggregates per (test, kindA, kindB, assignee), and
    /// APPENDS the rows as JSON lines to the output file (parameters[0]).
    ///
    /// Driven by tools\BatchExtractor (Automation console app):
    ///   app.AddPluginAssembly(ClashRuleEngine.dll); app.OpenFile(nwd);
    ///   app.ExecuteAddInPlugin("ClashBatchExtract.ACME", outputJsonlPath);
    ///
    /// The aggregated JSONL is the training data for the element-kind rule hierarchy:
    /// hand it to Claude → "Fire Flex → owner Fire", "Hyd Drainage >75mm → other", …
    /// </summary>
    [Plugin("ClashBatchExtract", "ACME", DisplayName = "Clash Batch Extract",
        ToolTip = "Headless: extract element-kind vs assignee records from clashes")]
    [AddInPlugin(AddInLocation.AddIn, LoadForCanExecute = true)]
    public class BatchClashExtractPlugin : AddInPlugin
    {
        // Property names that carry within-trade meaning (searched up the ancestor chain).
        private static readonly string[] CategoryProps = { "Category" };
        private static readonly string[] FamilyProps = { "Family", "Family Name", "Family and Type" };
        private static readonly string[] TypeProps = { "Type Name" };
        private static readonly string[] SystemProps = { "System Name", "System Classification", "System Type", "System Abbreviation" };
        private static readonly string[] DiameterProps = { "Outside Diameter", "Diameter", "Inside Diameter", "Nominal Diameter" };

        private static readonly HashSet<string> KindNoise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Solid", "Standard", "Default", "Internal", "<Not Shared>", "Default Site",
            "Generic Models", "Direct Shape"
        };

        public override int Execute(params string[] parameters)
        {
            string outPath = (parameters != null && parameters.Length > 0 && !string.IsNullOrWhiteSpace(parameters[0]))
                ? parameters[0]
                : Path.Combine(Path.GetTempPath(), "clash_kinds.jsonl");

            try
            {
                var doc = Application.ActiveDocument;
                if (doc == null) return 0;
                var clash = doc.GetClash();
                if (clash == null) return 0;

                string docTitle = SafeStr(() => doc.Title) ?? SafeStr(() => doc.FileName) ?? "?";

                // (test | kindA | kindB | assignee | status) -> count
                var agg = new Dictionary<string, int[]>(StringComparer.Ordinal);
                var meta = new Dictionary<string, string[]>(StringComparer.Ordinal);

                foreach (ClashTest test in ClashRuleEngine.Services.ClashApiCompat.GetAllTests(clash.TestsData))
                {
                    string testName = SafeStr(() => test.DisplayName) ?? "?";
                    WalkResults(test.Children, testName, agg, meta);
                }

                if (agg.Count == 0) return 0;

                var sb = new StringBuilder(64 * 1024);
                foreach (var kv in agg)
                {
                    var m = meta[kv.Key];   // [test, ka_cat, ka_kind, ka_sys, ka_dia, kb_cat, kb_kind, kb_sys, kb_dia, assignee, status]
                    sb.Append('{');
                    F(sb, "file", docTitle); sb.Append(',');
                    F(sb, "test", m[0]); sb.Append(',');
                    sb.Append("\"a\":{"); F(sb, "cat", m[1]); sb.Append(','); F(sb, "kind", m[2]); sb.Append(','); F(sb, "sys", m[3]); sb.Append(','); F(sb, "dia", m[4]); sb.Append("},");
                    sb.Append("\"b\":{"); F(sb, "cat", m[5]); sb.Append(','); F(sb, "kind", m[6]); sb.Append(','); F(sb, "sys", m[7]); sb.Append(','); F(sb, "dia", m[8]); sb.Append("},");
                    F(sb, "assignee", m[9]); sb.Append(',');
                    F(sb, "status", m[10]); sb.Append(',');
                    sb.Append("\"count\":").Append(kv.Value[0]);
                    sb.Append('}').Append('\n');
                }

                // Append so a multi-file Automation run accumulates into one dataset.
                File.AppendAllText(outPath, sb.ToString(), new UTF8Encoding(false));
                return 0;
            }
            catch
            {
                // Best-effort: one bad file must not abort the whole batch.
                return 0;
            }
        }

        private void WalkResults(SavedItemCollection children, string testName,
            Dictionary<string, int[]> agg, Dictionary<string, string[]> meta)
        {
            foreach (SavedItem si in children)
            {
                if (si is ClashResultGroup grp) { WalkResults(grp.Children, testName, agg, meta); continue; }
                if (!(si is ClashResult cr)) continue;

                try
                {
                    ModelItem i1 = null, i2 = null;
                    try { i1 = cr.Item1; } catch { }
                    try { i2 = cr.Item2; } catch { }

                    // Same extractor the live engine uses -> learned rules match at run time.
                    var ka = ClashRuleEngine.Services.ElementKind.Compute(i1);
                    var kb = ClashRuleEngine.Services.ElementKind.Compute(i2);
                    string aCat = ka.Category, aSys = ka.System, aKind = ka.Label, aDia = Band(ka.DiameterMm);
                    string bCat = kb.Category, bSys = kb.System, bKind = kb.Label, bDia = Band(kb.DiameterMm);

                    string assignee = SafeStr(() => cr.AssignedTo?.DisplayName);
                    if (string.IsNullOrWhiteSpace(assignee)) assignee = "(unassigned)";
                    string status = SafeStr(() => cr.Status.ToString()) ?? "?";

                    string key = string.Join("", testName, aCat, aKind, aSys, aDia, bCat, bKind, bSys, bDia, assignee, status);
                    if (agg.TryGetValue(key, out var c)) c[0]++;
                    else
                    {
                        agg[key] = new[] { 1 };
                        meta[key] = new[] { testName, aCat, aKind, aSys, aDia, bCat, bKind, bSys, bDia, assignee, status };
                    }
                }
                catch { /* skip unreadable clash */ }
            }
        }

        /// <summary>The element's "kind": prefer its System, else the most specific
        /// non-generic ancestor/type token.</summary>
        private string KindOf(ModelItem item, string sys)
        {
            if (!string.IsNullOrWhiteSpace(sys)) return Clip(sys);
            string fam = Prop(item, FamilyProps) ?? Prop(item, TypeProps);
            string tree = TreeToken(item);
            // Prefer a tree token if it's more specific than a generic family.
            string token = !string.IsNullOrWhiteSpace(tree) ? tree : fam;
            return Clip(token);
        }

        /// <summary>Nearest-leaf meaningful ancestor display name.</summary>
        private static string TreeToken(ModelItem item)
        {
            var cur = item; int depth = 0;
            while (cur != null && depth < 14)
            {
                string n = null;
                try { n = cur.DisplayName; } catch { }
                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
                if (string.IsNullOrWhiteSpace(n)) continue;
                string s = n.Trim();
                if (s.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (s.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (s.IndexOf(".nwc", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (s.IndexOf(" : ", StringComparison.Ordinal) >= 0) continue;
                int br = s.IndexOf('['); if (br > 0) s = s.Substring(0, br).Trim();
                int e = s.Length; while (e > 0 && (char.IsDigit(s[e - 1]) || s[e - 1] == ' ')) e--;
                if (e >= 2 && e < s.Length) s = s.Substring(0, e).Trim();
                if (s.Length < 2 || KindNoise.Contains(s)) continue;
                return s;
            }
            return null;
        }

        /// <summary>First value of any of the given property names, searched up the
        /// ancestor chain (clash leaf is usually a bare "Solid" with no properties).</summary>
        private static string Prop(ModelItem item, string[] names)
        {
            var cur = item; int depth = 0;
            while (cur != null && depth < 14)
            {
                try
                {
                    foreach (PropertyCategory cat in cur.PropertyCategories)
                        foreach (DataProperty p in cat.Properties)
                        {
                            if (p.DisplayName == null) continue;
                            for (int k = 0; k < names.Length; k++)
                                if (string.Equals(p.DisplayName, names[k], StringComparison.OrdinalIgnoreCase))
                                {
                                    string v = ValueStr(p);
                                    if (!string.IsNullOrWhiteSpace(v)) return Clip(v.Trim());
                                }
                        }
                }
                catch { }
                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
            }
            return null;
        }

        private static string DiaBand(ModelItem item)
        {
            var cur = item; int depth = 0;
            while (cur != null && depth < 14)
            {
                try
                {
                    foreach (PropertyCategory cat in cur.PropertyCategories)
                        foreach (DataProperty p in cat.Properties)
                        {
                            if (p.DisplayName == null) continue;
                            for (int k = 0; k < DiameterProps.Length; k++)
                                if (string.Equals(p.DisplayName, DiameterProps[k], StringComparison.OrdinalIgnoreCase))
                                {
                                    double d = NumMeters(p);
                                    if (d > 0) return Band(d * 1000.0);
                                }
                        }
                }
                catch { }
                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
            }
            return null;
        }

        private static string Band(double mm)
        {
            if (mm <= 0) return null;
            if (mm <= 40) return "<=40mm";
            if (mm <= 75) return "40-75mm";
            if (mm <= 100) return "75-100mm";
            if (mm <= 150) return "100-150mm";
            if (mm <= 300) return "150-300mm";
            return ">300mm";
        }

        private static double NumMeters(DataProperty p)
        {
            try
            {
                var v = p.Value; if (v == null) return 0;
                if (v.IsDoubleLength) return v.ToDoubleLength();
                if (v.IsDouble) return v.ToDouble();
                if (v.IsInt32) return v.ToInt32();
            }
            catch { }
            return 0;
        }

        private static string ValueStr(DataProperty p)
        {
            try
            {
                var v = p.Value; if (v == null) return null;
                if (v.IsDisplayString) return v.ToDisplayString();
                if (v.IsNamedConstant) return v.ToNamedConstant().DisplayName;
                if (v.IsDouble) return v.ToDouble().ToString(CultureInfo.InvariantCulture);
                if (v.IsInt32) return v.ToInt32().ToString(CultureInfo.InvariantCulture);
                if (v.IsBoolean) return v.ToBoolean().ToString();
                return v.ToString();
            }
            catch { return null; }
        }

        private static string Clip(string s) => string.IsNullOrEmpty(s) ? s : (s.Length <= 60 ? s : s.Substring(0, 60));
        private static string SafeStr(Func<string> f) { try { return f(); } catch { return null; } }

        private static void F(StringBuilder sb, string name, string val)
        {
            Str(sb, name); sb.Append(':'); Str(sb, val);
        }
        private static void Str(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            sb.Append('"');
        }
    }
}
