using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Exports a "coordination session snapshot" to JSON: every clash result's
    /// current disposition (group, status, assignee, description) together with
    /// the property bags of both clashing items.
    ///
    /// This one format serves three purposes:
    ///   1. Training data for AI rule inference ("the user assigned all of these
    ///      to Hydraulics — what do they have in common?")
    ///   2. The payload for the planned Smart Assign / Claude API integration
    ///   3. An audit trail of how a coordination pass was dispositioned
    ///
    /// The export STREAMS to disk (constant memory) — real models produce
    /// hundreds of MB of JSON, which must never be built as one in-memory
    /// string inside Navisworks. Progress/cancel callbacks keep the UI alive
    /// (the Navisworks API is main-thread-only, so this cannot run in the
    /// background).
    /// </summary>
    public static class SessionExportService
    {
        /// <summary>
        /// The handful of property NAMES worth exporting — the "main values" a
        /// coordinator (or the AI) reasons about. Matched by DisplayName across all
        /// categories; the first non-empty hit per name wins. Everything else
        /// (geometry, transforms, dozens of Revit fields) is dropped — that bloat
        /// is what produced the unusable 22 MB export.
        /// </summary>
        private static readonly string[] KeyProperties =
        {
            // identity / family / type
            "Category", "Family", "Family Name", "Family and Type", "Type Name", "Type", "Type Mark",
            // organisation
            "Workset", "Layer", "Level", "Reference Level", "Base Level",
            // size — the values rules actually test (metres in the API)
            "Outside Diameter", "Inside Diameter", "Diameter", "Size", "Width", "Height", "Length",
            // system / material
            "System Name", "System Type", "System Classification", "Material",
            // ids
            "Id", "GUID",
        };

        private static readonly HashSet<string> KeyPropertySet =
            new HashSet<string>(KeyProperties, StringComparer.OrdinalIgnoreCase);

        // Pump the UI roughly this often (clash count between progress reports).
        private const int ProgressEvery = 25;

        /// <summary>
        /// Exports clash tests to a JSON file, streaming.
        /// </summary>
        /// <param name="filePath">Destination .session.json path.</param>
        /// <param name="onlyTestName">Restrict to one test (null = all tests).</param>
        /// <param name="progress">Optional status callback (also pumps the UI).</param>
        /// <param name="cancelled">Optional cancellation probe, checked frequently.</param>
        /// <returns>Human-readable summary for the UI.</returns>
        public static string ExportTests(string filePath, string onlyTestName = null,
            Action<string> progress = null, Func<bool> cancelled = null)
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            var clash = doc.GetClash();
            if (clash == null) throw new InvalidOperationException("Clash plugin not available.");

            var tests = ClashApiCompat.GetAllTests(clash.TestsData)
                .Where(t => t.Children.Count > 0)
                .Where(t => onlyTestName == null
                         || string.Equals(t.DisplayName, onlyTestName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (tests.Count == 0)
                throw new InvalidOperationException(onlyTestName == null
                    ? "No clash tests with results found."
                    : $"Test '{onlyTestName}' not found or has no results.");

            int testCount = 0, clashCount = 0, errorCount = 0;
            bool wasCancelled = false;

            try
            {
                // 64 KB buffered stream — constant memory regardless of model size
                using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    w.Write('{');
                    WriteField(w, "schema", "clashre-session/2-lean"); w.Write(',');
                    WriteField(w, "exportedAt", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss")); w.Write(',');
                    WriteField(w, "document", doc.Title ?? ""); w.Write(',');
                    w.Write("\"tests\":[");

                    for (int ti = 0; ti < tests.Count; ti++)
                    {
                        var test = tests[ti];
                        if (cancelled != null && cancelled()) { wasCancelled = true; break; }

                        if (ti > 0) w.Write(',');
                        testCount++;

                        int testTotal = CountResults(test.Children);
                        progress?.Invoke($"Test {ti + 1}/{tests.Count}: {test.DisplayName}  ({testTotal} clashes)");

                        w.Write('{');
                        WriteField(w, "name", test.DisplayName); w.Write(',');
                        w.Write("\"clashes\":[");
                        bool first = true;
                        int inTest = 0;
                        WriteResults(w, test.Children, null, ref first, ref inTest, ref errorCount,
                            test.DisplayName, ti + 1, tests.Count, testTotal, progress, cancelled, ref wasCancelled);
                        clashCount += inTest;
                        w.Write("]}");

                        if (wasCancelled) break;
                    }

                    w.Write("]}");
                }
            }
            catch
            {
                TryDelete(filePath);
                throw;
            }

            if (wasCancelled)
            {
                TryDelete(filePath);   // partial JSON is syntactically broken — don't leave it around
                return "Export cancelled — no file written.";
            }

            string note = errorCount > 0 ? $"\n({errorCount} clash(es) skipped due to read errors)" : "";
            return $"Exported {clashCount} clashes across {testCount} test(s) to:\n{filePath}{note}";
        }

        // ───────────────────────────────────────────────────────────────────
        //  Lean SUMMARY export — every test, how clashes were assigned, the
        //  element-type patterns behind each assignee. Aggregates only (counts),
        //  so the whole document collapses to a few KB that fits in a prompt.
        // ───────────────────────────────────────────────────────────────────

        private const int MaxPatternsPerAssignee = 20;
        private const int MaxGroupsPerTest = 8;

        /// <summary>
        /// Exports a compact per-test assignment summary across ALL tests (or one).
        /// For each test: totals, status breakdown, and — per assignee — how many
        /// clashes and the dominant "type A ↔ type B" element pairings. This is the
        /// nimble payload for AI rule inference: it shows HOW you assign without the
        /// per-clash bulk that bloats the full session export.
        /// </summary>
        public static string ExportSummary(string filePath, string onlyTestName = null,
            Action<string> progress = null, Func<bool> cancelled = null)
        {
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("No active document.");

            var clash = doc.GetClash();
            if (clash == null) throw new InvalidOperationException("Clash plugin not available.");

            var tests = ClashApiCompat.GetAllTests(clash.TestsData)
                .Where(t => t.Children.Count > 0)
                .Where(t => onlyTestName == null
                         || string.Equals(t.DisplayName, onlyTestName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (tests.Count == 0)
                throw new InvalidOperationException(onlyTestName == null
                    ? "No clash tests with results found."
                    : $"Test '{onlyTestName}' not found or has no results.");

            var sb = new StringBuilder(64 * 1024);
            sb.Append('{');
            AppendField(sb, "schema", "clashre-summary/1"); sb.Append(',');
            AppendField(sb, "exportedAt", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss")); sb.Append(',');
            AppendField(sb, "document", doc.Title ?? ""); sb.Append(',');
            sb.Append("\"tests\":[");

            int testCount = 0, clashCount = 0;
            bool wasCancelled = false;

            for (int ti = 0; ti < tests.Count; ti++)
            {
                var test = tests[ti];
                if (cancelled != null && cancelled()) { wasCancelled = true; break; }

                int testTotal = CountResults(test.Children);
                progress?.Invoke($"Summarising {ti + 1}/{tests.Count}: {test.DisplayName}  ({testTotal} clashes)");

                var agg = new TestSummary();
                SummariseResults(test.Children, null, agg, progress, cancelled, ref wasCancelled,
                    test.DisplayName, ti + 1, tests.Count, testTotal);
                if (wasCancelled) break;

                if (testCount > 0) sb.Append(',');
                AppendTestSummary(sb, test.DisplayName, agg);
                testCount++;
                clashCount += agg.Total;
            }

            sb.Append("]}");

            if (wasCancelled) return "Summary cancelled — no file written.";

            try { File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { throw new InvalidOperationException("Could not write summary: " + ex.Message); }

            return $"Summarised {clashCount} clashes across {testCount} test(s) to:\n{filePath}\n\n" +
                   "This file is small enough to paste into a prompt.";
        }

        private sealed class TestSummary
        {
            public int Total;
            public readonly Dictionary<string, int> Status = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> Groups = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // assignee -> clash count
            public readonly Dictionary<string, int> AssigneeCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // assignee -> (element-type token -> # of its clashes that contain that token)
            public readonly Dictionary<string, Dictionary<string, int>> AssigneeTokens =
                new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            // Raw ancestor chain of the first clash's two items — ground-truth sample
            // of the tree structure (so rules can be written even if tokenising misses).
            public string SampleA;
            public string SampleB;
        }

        private static void SummariseResults(SavedItemCollection children, string groupName,
            TestSummary agg, Action<string> progress, Func<bool> cancelled, ref bool wasCancelled,
            string testName, int testIndex, int testTotal, int clashesInTest)
        {
            foreach (SavedItem si in children)
            {
                if (wasCancelled) return;
                if (si is ClashResultGroup grp)
                {
                    SummariseResults(grp.Children, grp.DisplayName, agg, progress, cancelled, ref wasCancelled,
                        testName, testIndex, testTotal, clashesInTest);
                }
                else if (si is ClashResult cr)
                {
                    try
                    {
                        agg.Total++;

                        string status = cr.Status.ToString();
                        Bump(agg.Status, status);

                        if (!string.IsNullOrEmpty(groupName)) Bump(agg.Groups, groupName);

                        string assignee = SafeString(() => ReflectString(cr, "AssignedTo"));
                        if (string.IsNullOrWhiteSpace(assignee)) assignee = "(unassigned)";

                        Bump(agg.AssigneeCount, assignee);

                        // Harvest the element-type words from BOTH items' ancestor names
                        // (the clash node itself is a geometry "Solid" with no type —
                        // the type lives in the tree). Distinct per clash so each token
                        // counts at most once per clash.
                        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CollectTypeTokens(SafeGet(() => cr.Item1), tokens);
                        CollectTypeTokens(SafeGet(() => cr.Item2), tokens);

                        if (!agg.AssigneeTokens.TryGetValue(assignee, out var tokMap))
                            agg.AssigneeTokens[assignee] = tokMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        foreach (var t in tokens) Bump(tokMap, t);

                        if (agg.SampleA == null)
                        {
                            agg.SampleA = SafeString(() => RawAncestors(cr.Item1)) ?? "";
                            agg.SampleB = SafeString(() => RawAncestors(cr.Item2)) ?? "";
                        }
                    }
                    catch { /* skip unreadable clash */ }

                    if (agg.Total % ProgressEvery == 0)
                    {
                        progress?.Invoke($"Summarising {testIndex}/{testTotal}: {testName}  ({agg.Total}/{clashesInTest})");
                        if (cancelled != null && cancelled()) { wasCancelled = true; return; }
                    }
                }
            }
        }

        // Ancestor DisplayNames that carry no element-type meaning (model files,
        // Revit level/site/grouping nodes, the geometry leaf, IFC noise).
        private static readonly HashSet<string> NoiseTokens =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Solid", "Internal", "<Not Shared>", "Default Site", "Site", "Project",
            "Geometry", "Composite", "Element", "Body", "Faces", "Mesh",
            // discipline codes — not useful WITHIN a trade
            "ELEC", "MECH", "FIRE", "HYD", "ICT", "COMMS", "SEC", "STR",
        };

        // Key property names whose VALUES carry within-trade meaning, mapped to a
        // short prefix so the export shows which property gave the signal (and thus
        // which rule condition to write). Searched across the item AND its ancestors.
        private static readonly Dictionary<string, string> SignalProps =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "System Name", "sys" }, { "System Classification", "sys" }, { "System Type", "sys" },
            { "System Abbreviation", "sys" },
            { "Workset", "ws" },
            { "Family", "fam" }, { "Family Name", "fam" }, { "Family and Type", "fam" },
            { "Type Name", "type" },
            { "Material", "mat" },
        };

        private static readonly HashSet<string> DiameterProps =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Outside Diameter", "Diameter", "Inside Diameter", "Nominal Diameter", "Size" };

        /// <summary>
        /// Harvests every within-trade signal token for an item into <paramref name="set"/>:
        ///   - ancestor DisplayName words ("t:Conduit", "t:Clearance Zone"...)
        ///   - key property values prefixed by source ("sys:Sanitary", "ws:SANITARY",
        ///     "fam:Tundish", "type:...", "mat:...")
        ///   - a diameter band ("dia:&lt;=40mm" ...) — separates small retic from drainage
        /// The clashing node is a geometry "Solid" with no type/properties of its own,
        /// so we walk the ancestor chain (where Revit puts category/family/system).
        /// </summary>
        private static void CollectTypeTokens(ModelItem item, HashSet<string> set)
        {
            if (item == null) return;
            var cur = item;
            int depth = 0;
            double? diaMeters = null;

            while (cur != null && depth < 16)
            {
                string dn = null;
                try { dn = cur.DisplayName; } catch { }
                string tok = CleanToken(dn);
                if (tok != null) set.Add("t:" + tok);

                try
                {
                    foreach (PropertyCategory cat in cur.PropertyCategories)
                        foreach (DataProperty p in cat.Properties)
                        {
                            string nm = p.DisplayName;
                            if (nm == null) continue;

                            if (SignalProps.TryGetValue(nm, out string prefix))
                            {
                                string v = SafeString(() => ValueToString(p));
                                v = CleanValue(v);
                                if (v != null) set.Add(prefix + ":" + v);
                            }
                            else if (diaMeters == null && DiameterProps.Contains(nm))
                            {
                                double d = SafeDouble(p);
                                if (d > 0) diaMeters = d;
                            }
                        }
                }
                catch { /* keep whatever we gathered */ }

                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
            }

            if (diaMeters.HasValue) set.Add(DiameterBand(diaMeters.Value));
        }

        /// <summary>Diameter band in mm (API values are metres). Separates small
        /// reticulation from large drainage/mains without exposing every size.</summary>
        private static string DiameterBand(double meters)
        {
            double mm = meters * 1000.0;
            if (mm <= 0) return "dia:?";
            if (mm <= 40) return "dia:<=40mm";
            if (mm <= 80) return "dia:40-80mm";
            if (mm <= 150) return "dia:80-150mm";
            if (mm <= 300) return "dia:150-300mm";
            return "dia:>300mm";
        }

        private static double SafeDouble(DataProperty p)
        {
            try
            {
                var v = p.Value;
                if (v == null) return 0;
                if (v.IsDoubleLength) return v.ToDoubleLength();
                if (v.IsDouble) return v.ToDouble();
                if (v.IsInt32) return v.ToInt32();
            }
            catch { }
            return 0;
        }

        /// <summary>Trim/clip a property value for use as a token; null if empty/noise.</summary>
        private static string CleanValue(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string s = v.Trim();
            if (s.Length < 2) return null;
            if (NoiseTokens.Contains(s)) return null;
            return Clip(s, 48);
        }

        /// <summary>Full leaf-to-root ancestor DisplayName chain, raw (capped). A
        /// ground-truth sample of the tree so rules can be authored by inspection.</summary>
        private static string RawAncestors(ModelItem item)
        {
            if (item == null) return null;
            var names = new List<string>();
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 16)
            {
                string n = null;
                try { n = cur.DisplayName; } catch { }
                if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                try { cur = cur.Parent; } catch { cur = null; }
                depth++;
            }
            return Clip(string.Join(" / ", names), 300);
        }

        /// <summary>
        /// Normalises one ancestor DisplayName to a comparable type word, or null if
        /// it is noise. Strips trailing instance ids ("Conduit 12345", "Pipe [9876]")
        /// so instances collapse onto their type.
        /// </summary>
        private static string CleanToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();

            // Model-file and federated "file : id : location" nodes.
            if (s.IndexOf(".rvt", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(".ifc", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (s.IndexOf(" : ", StringComparison.Ordinal) >= 0) return null;
            if (s.StartsWith("location ", StringComparison.OrdinalIgnoreCase)) return null;
            if (s.StartsWith("Level ", StringComparison.OrdinalIgnoreCase)) return null;

            // Strip a trailing "[123]" or " 12345" instance id.
            int br = s.IndexOf('[');
            if (br > 0) s = s.Substring(0, br).Trim();
            s = StripTrailingNumber(s);

            if (s.Length < 2) return null;
            if (IsAllDigits(s)) return null;
            if (NoiseTokens.Contains(s)) return null;

            return Clip(s, 48);
        }

        private static string StripTrailingNumber(string s)
        {
            int i = s.Length;
            while (i > 0 && (char.IsDigit(s[i - 1]) || s[i - 1] == ' ' || s[i - 1] == '-' || s[i - 1] == ':'))
                i--;
            // Only strip if it actually removed a number and leaves a real word.
            string trimmed = s.Substring(0, i).Trim();
            return (trimmed.Length >= 2 && trimmed.Length < s.Trim().Length) ? trimmed : s.Trim();
        }

        private static bool IsAllDigits(string s)
        {
            foreach (char c in s) if (!char.IsDigit(c)) return false;
            return s.Length > 0;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max);
        }

        private static void AppendTestSummary(StringBuilder sb, string testName, TestSummary agg)
        {
            sb.Append('{');
            AppendField(sb, "name", testName); sb.Append(',');
            sb.Append("\"total\":").Append(agg.Total).Append(',');

            sb.Append("\"status\":{");
            int i = 0;
            foreach (var kv in agg.Status.OrderByDescending(k => k.Value))
            {
                if (i++ > 0) sb.Append(',');
                AppendString(sb, kv.Key); sb.Append(':').Append(kv.Value);
            }
            sb.Append("},");

            sb.Append("\"assignments\":[");
            i = 0;
            foreach (var ass in agg.AssigneeCount.OrderByDescending(k => k.Value))
            {
                if (i++ > 0) sb.Append(',');
                sb.Append('{');
                AppendField(sb, "assignedTo", ass.Key); sb.Append(',');
                sb.Append("\"count\":").Append(ass.Value).Append(',');

                // The element-type words seen in this bucket's clashes, with the
                // number of its clashes containing each — the within-trade signal.
                sb.Append("\"types\":[");
                int j = 0;
                if (agg.AssigneeTokens.TryGetValue(ass.Key, out var tokMap))
                {
                    var ordered = tokMap.OrderByDescending(k => k.Value).ToList();
                    foreach (var tk in ordered.Take(MaxPatternsPerAssignee))
                    {
                        if (j++ > 0) sb.Append(',');
                        sb.Append('{');
                        AppendField(sb, "type", tk.Key); sb.Append(',');
                        sb.Append("\"in\":").Append(tk.Value);
                        sb.Append('}');
                    }
                    sb.Append(']');
                    int more = ordered.Count - MaxPatternsPerAssignee;
                    if (more > 0) { sb.Append(",\"moreTypes\":").Append(more); }
                }
                else sb.Append(']');

                sb.Append('}');
            }
            sb.Append(']');

            if (!string.IsNullOrEmpty(agg.SampleA) || !string.IsNullOrEmpty(agg.SampleB))
            {
                sb.Append(",\"sampleTreeA\":"); AppendString(sb, agg.SampleA);
                sb.Append(",\"sampleTreeB\":"); AppendString(sb, agg.SampleB);
            }

            if (agg.Groups.Count > 0)
            {
                sb.Append(",\"topGroups\":[");
                i = 0;
                foreach (var grp in agg.Groups.OrderByDescending(k => k.Value).Take(MaxGroupsPerTest))
                {
                    if (i++ > 0) sb.Append(',');
                    sb.Append('{');
                    AppendField(sb, "group", grp.Key); sb.Append(',');
                    sb.Append("\"count\":").Append(grp.Value);
                    sb.Append('}');
                }
                sb.Append(']');
            }

            sb.Append('}');
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            map.TryGetValue(key, out int c);
            map[key] = c + 1;
        }

        /// <summary>
        /// Recursively writes every ClashResult in a children tree, tagging each
        /// with the display name of the group it sits in (null at top level).
        /// One bad clash never kills the export — it is counted and skipped.
        /// </summary>
        private static void WriteResults(TextWriter w, SavedItemCollection children,
            string groupName, ref bool first, ref int count, ref int errors,
            string testName, int testIndex, int testTotal, int clashesInTest,
            Action<string> progress, Func<bool> cancelled, ref bool wasCancelled)
        {
            foreach (SavedItem si in children)
            {
                if (wasCancelled) return;
                if (si is ClashResultGroup grp)
                {
                    WriteResults(w, grp.Children, grp.DisplayName, ref first, ref count, ref errors,
                        testName, testIndex, testTotal, clashesInTest, progress, cancelled, ref wasCancelled);
                }
                else if (si is ClashResult cr)
                {
                    var sb = new StringBuilder(4096);
                    try
                    {
                        AppendClash(sb, cr, groupName);   // build ONE clash in memory, then flush
                    }
                    catch
                    {
                        errors++;
                        continue;
                    }
                    if (!first) w.Write(',');
                    first = false;
                    w.Write(sb.ToString());
                    count++;

                    if (count % ProgressEvery == 0)
                    {
                        progress?.Invoke($"Test {testIndex}/{testTotal}: {testName}  ({count}/{clashesInTest} clashes)");
                        if (cancelled != null && cancelled()) { wasCancelled = true; return; }
                    }
                }
            }
        }

        private static int CountResults(SavedItemCollection children)
        {
            int n = 0;
            foreach (SavedItem si in children)
            {
                if (si is ClashResultGroup grp) n += CountResults(grp.Children);
                else if (si is ClashResult) n++;
            }
            return n;
        }

        private static void AppendClash(StringBuilder sb, ClashResult cr, string groupName)
        {
            sb.Append('{');
            AppendField(sb, "name", cr.DisplayName); sb.Append(',');
            AppendField(sb, "status", cr.Status.ToString()); sb.Append(',');
            AppendField(sb, "group", groupName); sb.Append(',');

            // The user's disposition (who's responsible) — the learning signal.
            // AssignedTo's type varies across API versions, so reflect + ToString.
            AppendField(sb, "assignedTo", SafeString(() => ReflectString(cr, "AssignedTo")));

            string distance = SafeString(() => ReflectString(cr, "Distance"));
            if (!string.IsNullOrEmpty(distance)) { sb.Append(','); AppendField(sb, "distance", distance); }

            sb.Append(",\"a\":"); AppendItem(sb, SafeGet(() => cr.Item1));
            sb.Append(",\"b\":"); AppendItem(sb, SafeGet(() => cr.Item2));
            sb.Append('}');
        }

        private static void AppendItem(StringBuilder sb, ModelItem item)
        {
            if (item == null) { sb.Append("null"); return; }

            sb.Append('{');
            AppendField(sb, "name", SafeString(() => item.DisplayName)); sb.Append(',');
            // "which model it comes from" — the root ancestor is the linked file.
            AppendField(sb, "model", SafeString(() => RootName(item)));

            // Tree path (Category / Family / Type ...) — in coordination NWCs the
            // element TYPE lives in the hierarchy, not in properties. This is what
            // distinguishes cable tray / pipe / duct / hanger-rod, especially when
            // the leaf has no material/name of its own.
            string path = SafeString(() => AncestorPath(item));
            if (!string.IsNullOrEmpty(path)) { sb.Append(','); AppendField(sb, "path", path); }

            string guid = SafeString(() =>
                item.InstanceGuid != Guid.Empty ? item.InstanceGuid.ToString() : null);
            if (!string.IsNullOrEmpty(guid)) { sb.Append(','); AppendField(sb, "guid", guid); }

            // Flat key/value of just the whitelisted properties (first non-empty per name).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    foreach (DataProperty prop in cat.Properties)
                    {
                        string name = prop.DisplayName;
                        if (name == null || !KeyPropertySet.Contains(name) || seen.Contains(name)) continue;
                        string val = SafeString(() => ValueToString(prop));
                        if (string.IsNullOrEmpty(val)) continue;
                        seen.Add(name);
                        sb.Append(',');
                        AppendField(sb, name, val);
                    }
                }
            }
            catch { /* partial bag is better than none */ }

            sb.Append('}');
        }

        private static string RootName(ModelItem item)
        {
            var cur = item;
            ModelItem root = item;
            int depth = 0;
            while (cur != null && depth < 12) { root = cur; cur = cur.Parent; depth++; }
            return root != null ? root.DisplayName : null;
        }

        /// <summary>
        /// The element's ancestor names BETWEEN the leaf and the root file —
        /// typically Category / Family / Type in a Revit-exported NWC. Captures the
        /// element type (cable tray, pipe, duct, hanger, rod...) that isn't in any
        /// property. Top-down, leaf and root excluded, capped for size.
        /// </summary>
        private static string AncestorPath(ModelItem item)
        {
            var names = new List<string>();
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 12)
            {
                if (!string.IsNullOrWhiteSpace(cur.DisplayName)) names.Add(cur.DisplayName);
                cur = cur.Parent;
                depth++;
            }
            if (names.Count <= 2) return null;             // only leaf + root → nothing in between
            var middle = names.GetRange(1, names.Count - 2); // drop leaf (have it as name) + root (have it as model)
            middle.Reverse();                                // top-down: Category / Family / Type
            return string.Join(" / ", middle);
        }

        private static string ValueToString(DataProperty prop)
        {
            if (prop.Value == null) return null;
            var val = prop.Value;
            if (val.IsDisplayString) return val.ToDisplayString();
            if (val.IsDouble) return val.ToDouble().ToString(CultureInfo.InvariantCulture);
            if (val.IsInt32) return val.ToInt32().ToString(CultureInfo.InvariantCulture);
            if (val.IsBoolean) return val.ToBoolean().ToString();
            if (val.IsDoubleLength) return val.ToDoubleLength().ToString(CultureInfo.InvariantCulture);
            if (val.IsNamedConstant) return val.ToNamedConstant().DisplayName;
            return val.ToString();
        }

        private static string ReflectString(object obj, string propertyName)
        {
            var p = obj.GetType().GetProperty(propertyName);
            if (p == null) return null;
            var v = p.GetValue(obj);
            if (v == null) return null;
            var s = v.ToString();
            // An empty wrapper object stringifies to its type name — not useful
            return s == v.GetType().FullName ? null : s;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ── tiny JSON helpers (no external dependency) ──────────────────────

        private static void WriteField(TextWriter w, string name, string value)
        {
            var sb = new StringBuilder(128);
            AppendField(sb, name, value);
            w.Write(sb.ToString());
        }

        private static void AppendField(StringBuilder sb, string name, string value)
        {
            AppendString(sb, name);
            sb.Append(':');
            AppendString(sb, value);
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static string Num(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

        private static T SafeGet<T>(Func<T> getter) where T : class
        {
            try { return getter(); } catch { return null; }
        }

        private static string SafeString(Func<string> getter)
        {
            try { return getter(); } catch { return null; }
        }
    }
}
