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

                // (test | kindA | kindB | assignee | status | gapBand | grid | level) -> count
                var agg = new Dictionary<string, int[]>(StringComparer.Ordinal);
                var meta = new Dictionary<string, string[]>(StringComparer.Ordinal);
                // Per-bucket raw gap range (mm), so band boundaries can be re-tuned in
                // analysis WITHOUT re-running the batch.
                var gaps = new Dictionary<string, double[]>(StringComparer.Ordinal);
                // Per-DOCUMENT element-kind cache: the same element clashes many times, so
                // compute its kind once (the property walk is the bottleneck). Keyed by
                // InstanceGuid. Cleared per document (this whole method runs once per NWD).
                var kindCache = new Dictionary<string, ClashRuleEngine.Services.ElementKindInfo>(StringComparer.Ordinal);
                // Per-bucket raw diameter range (mm) for each side [aMin,aMax,bMin,bMax],
                // so rules can threshold on actual bore (e.g. small-bore <=25mm), not just the band.
                var dias = new Dictionary<string, double[]>(StringComparer.Ordinal);

                foreach (ClashTest test in ClashRuleEngine.Services.ClashApiCompat.GetAllTests(clash.TestsData))
                {
                    string testName = SafeStr(() => test.DisplayName) ?? "?";
                    WalkResults(test.Children, testName, agg, meta, gaps, kindCache, dias);
                }

                if (agg.Count == 0) return 0;

                var sb = new StringBuilder(64 * 1024);
                foreach (var kv in agg)
                {
                    var m = meta[kv.Key];   // [test, ka_cat, ka_kind, ka_sys, ka_dia, kb_cat, kb_kind, kb_sys, kb_dia, assignee, status, gapBand, grid, level]
                    var g = gaps.TryGetValue(kv.Key, out var gg) ? gg : new[] { 0.0, 0.0 };
                    var dd = dias.TryGetValue(kv.Key, out var ddv) ? ddv : new[] { 0.0, 0.0, 0.0, 0.0 };
                    sb.Append('{');
                    F(sb, "file", docTitle); sb.Append(',');
                    F(sb, "test", m[0]); sb.Append(',');
                    sb.Append("\"a\":{"); F(sb, "cat", m[1]); sb.Append(','); F(sb, "kind", m[2]); sb.Append(','); F(sb, "fam", m[14]); sb.Append(','); F(sb, "type", m[15]); sb.Append(','); F(sb, "leaf", m[16]); sb.Append(','); F(sb, "sys", m[3]); sb.Append(','); F(sb, "dia", m[4]); sb.Append(','); DiaMm(sb, dd[0], dd[1]); sb.Append("},");
                    sb.Append("\"b\":{"); F(sb, "cat", m[5]); sb.Append(','); F(sb, "kind", m[6]); sb.Append(','); F(sb, "fam", m[17]); sb.Append(','); F(sb, "type", m[18]); sb.Append(','); F(sb, "leaf", m[19]); sb.Append(','); F(sb, "sys", m[7]); sb.Append(','); F(sb, "dia", m[8]); sb.Append(','); DiaMm(sb, dd[2], dd[3]); sb.Append("},");
                    F(sb, "assignee", m[9]); sb.Append(',');
                    F(sb, "status", m[10]); sb.Append(',');
                    F(sb, "grid", m[12]); sb.Append(',');
                    F(sb, "level", m[13]); sb.Append(',');
                    sb.Append("\"gap\":{"); F(sb, "band", m[11]); sb.Append(',');
                    sb.Append("\"min\":").Append(Round(g[0])); sb.Append(',');
                    sb.Append("\"max\":").Append(Round(g[1])); sb.Append("},");
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
            Dictionary<string, int[]> agg, Dictionary<string, string[]> meta,
            Dictionary<string, double[]> gaps,
            Dictionary<string, ClashRuleEngine.Services.ElementKindInfo> kindCache,
            Dictionary<string, double[]> dias)
        {
            foreach (SavedItem si in children)
            {
                if (si is ClashResultGroup grp) { WalkResults(grp.Children, testName, agg, meta, gaps, kindCache, dias); continue; }
                if (!(si is ClashResult cr)) continue;

                try
                {
                    ModelItem i1 = null, i2 = null;
                    try { i1 = cr.Item1; } catch { }
                    try { i2 = cr.Item2; } catch { }

                    // Cached per element (same source extractor as the live engine).
                    var ka = KindCached(i1, kindCache);
                    var kb = KindCached(i2, kindCache);
                    string aCat = ka.Category, aSys = ka.System, aKind = ka.Label, aDia = Band(ka.DiameterMm);
                    string aFam = ka.Family, aType = ka.Type, aLeaf = ka.Leaf;
                    string bCat = kb.Category, bSys = kb.System, bKind = kb.Label, bDia = Band(kb.DiameterMm);
                    string bFam = kb.Family, bType = kb.Type, bLeaf = kb.Leaf;

                    string assignee = SafeStr(() => cr.AssignedTo?.DisplayName);
                    if (string.IsNullOrWhiteSpace(assignee)) assignee = "(unassigned)";
                    string status = SafeStr(() => cr.Status.ToString()) ?? "?";

                    // Clearance gap (mm, signed: <0 = hard penetration) + grid cell + level
                    // from ONE spatial query (was two: ClosestIntersection is the expensive bit).
                    double gapMm = GapMm(cr);
                    string gapBand = GapBand(gapMm);
                    string grid = null, level = null;
                    try
                    {
                        var sys = Application.MainDocument?.Grids?.ActiveSystem;
                        var inter = sys?.ClosestIntersection(cr.Center);
                        if (inter != null) { grid = inter.DisplayName; level = inter.Level?.DisplayName; }
                    }
                    catch { }

                    string key = string.Join("", testName, aCat, aKind, aSys, aDia, bCat, bKind, bSys, bDia, assignee, status, gapBand, grid ?? "", level ?? "", aFam, aType, aLeaf, bFam, bType, bLeaf);
                    double aDiaMm = ka.DiameterMm, bDiaMm = kb.DiameterMm;
                    if (agg.TryGetValue(key, out var c))
                    {
                        c[0]++;
                        var gr = gaps[key];
                        if (gapMm < gr[0]) gr[0] = gapMm;
                        if (gapMm > gr[1]) gr[1] = gapMm;
                        var dr = dias[key];
                        Widen(dr, 0, 1, aDiaMm);
                        Widen(dr, 2, 3, bDiaMm);
                    }
                    else
                    {
                        agg[key] = new[] { 1 };
                        meta[key] = new[] { testName, aCat, aKind, aSys, aDia, bCat, bKind, bSys, bDia, assignee, status, gapBand, grid, level, aFam, aType, aLeaf, bFam, bType, bLeaf };
                        gaps[key] = new[] { gapMm, gapMm };
                        dias[key] = new[] { aDiaMm, aDiaMm, bDiaMm, bDiaMm };
                    }
                }
                catch { /* skip unreadable clash */ }
            }
        }

        /// <summary>Clash gap in mm (model length x 1000). Signed: hard-clash
        /// penetration is negative, a clearance gap is positive. 0 if unreadable.</summary>
        private static double GapMm(ClashResult cr)
        {
            try { return cr.Distance * 1000.0; } catch { return 0; }
        }

        /// <summary>Coarse gap band keyed in the aggregate; raw min/max kept alongside
        /// so boundaries can be re-tuned in analysis without re-running the batch.</summary>
        private static string GapBand(double mm)
        {
            if (mm <= -50) return "pen>=50mm";
            if (mm <= -10) return "pen10-50mm";
            if (mm < 0) return "pen<10mm";
            if (mm <= 10) return "gap0-10mm";
            if (mm <= 25) return "gap10-25mm";
            if (mm <= 50) return "gap25-50mm";
            if (mm <= 100) return "gap50-100mm";
            return "gap>100mm";
        }

        /// <summary>ElementKind for an item, computed once per element and cached for the
        /// rest of the document (the same element clashes many times).</summary>
        private static ClashRuleEngine.Services.ElementKindInfo KindCached(
            ModelItem item, Dictionary<string, ClashRuleEngine.Services.ElementKindInfo> cache)
        {
            if (item == null) return new ClashRuleEngine.Services.ElementKindInfo();
            string key = KeyOf(item);
            if (key == null || cache == null) return ClashRuleEngine.Services.ElementKind.Compute(item);
            if (cache.TryGetValue(key, out var hit)) return hit;
            var info = ClashRuleEngine.Services.ElementKind.Compute(item);
            cache[key] = info;
            return info;
        }

        /// <summary>Stable element identity: InstanceGuid when present, else a short path.</summary>
        private static string KeyOf(ModelItem item)
        {
            try { if (item.InstanceGuid != Guid.Empty) return "G:" + item.InstanceGuid; } catch { }
            try
            {
                var sb = new StringBuilder(64); var cur = item; int d = 0;
                while (cur != null && d < 6) { sb.Append(SafeStr(() => cur.DisplayName)).Append('/'); cur = cur.Parent; d++; }
                return sb.Length > 0 ? "P:" + sb : null;
            }
            catch { return null; }
        }

        private static string Round(double mm)
        {
            return mm.ToString("0.#", CultureInfo.InvariantCulture);
        }

        /// <summary>Widen a [min,max] pair (indices lo,hi) with a positive value; 0 = unknown, ignored.</summary>
        private static void Widen(double[] arr, int lo, int hi, double v)
        {
            if (v <= 0) return;
            if (arr[lo] <= 0 || v < arr[lo]) arr[lo] = v;
            if (v > arr[hi]) arr[hi] = v;
        }

        /// <summary>Emits "diaMm":{"min":..,"max":..} (raw bore range, mm; 0 = unknown).</summary>
        private static void DiaMm(StringBuilder sb, double min, double max)
        {
            sb.Append("\"diaMm\":{\"min\":").Append(Round(min)).Append(",\"max\":").Append(Round(max)).Append('}');
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
