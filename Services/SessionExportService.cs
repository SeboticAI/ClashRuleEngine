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
