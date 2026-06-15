using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Evaluates rules against clash results and writes the outcome back to
    /// Clash Detective using the documented copy/edit/swap pattern:
    ///
    ///   1. test.CreateCopy()                — detached, fully editable copy
    ///   2. edit + regroup children on copy  — plain property sets, no API writes
    ///   3. TestsEditTestFromCopy            — single atomic write-back per test
    ///      (named TestsEditTestFromCustom before NW 2027; see WriteBack)
    ///
    /// This is the same mechanism the open-source GroupClashes plugin has used
    /// across Navisworks 2015–2027. It never duplicates result GUIDs (the cause
    /// of the earlier TestsAddCopy crash) and never mutates attached items.
    /// </summary>
    public class ClashProcessingService
    {
        public ProcessingResult LastResult { get; private set; }

        // Proximity threshold in model units (Navisworks API is metres).
        // Active clashes whose centres fall within this distance are clustered
        // into the same Clash Detective group.
        private const double ProximityThresholdMeters = 1.0;

        /// <summary>
        /// Process a specific clash test using its associated rule set
        /// </summary>
        public ProcessingResult ProcessSingleTest(string testName, TestRuleSet ruleSet,
            SystemHierarchy hierarchy = null, bool useHierarchyFallback = false)
        {
            _assignedToUnavailableReported = false;
            var result = new ProcessingResult { TestName = testName };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            var orderedRules = ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

            // Find the specific clash test (folder-aware on 2027+)
            ClashTest targetTest = ClashApiCompat.GetAllTests(clashPlugin.TestsData)
                .FirstOrDefault(ct => string.Equals(ct.DisplayName, testName, StringComparison.OrdinalIgnoreCase));

            if (targetTest == null)
            {
                result.Errors.Add($"Clash test '{testName}' not found.");
                LastResult = result;
                return result;
            }

            result.TestsProcessed = 1;
            var fallback = new HierarchyFallback(hierarchy, useHierarchyFallback);
            ProcessTest(targetTest, orderedRules, ruleSet, result, doc, clashPlugin.TestsData, fallback);

            LastResult = result;
            return result;
        }

        /// <summary>
        /// Process all clash tests using the project config
        /// </summary>
        public ProcessingResult ProcessAllTests(ProjectConfig config)
        {
            _assignedToUnavailableReported = false;
            var result = new ProcessingResult { TestName = "All Tests" };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            // Snapshot the test list first — we swap tests while iterating otherwise
            var tests = ClashApiCompat.GetAllTests(clashPlugin.TestsData);
            var fallback = new HierarchyFallback(config.Hierarchy, config.UseHierarchyFallback);

            foreach (var ct in tests)
            {
                result.TestsProcessed++;

                var testRuleSet = config.GetOrCreateTestRuleSet(ct.DisplayName);
                var orderedRules = testRuleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

                // With the hierarchy fallback on, a test with no rules can still be
                // auto-assigned by discipline — so only skip when there's nothing to do.
                if (orderedRules.Count == 0 && !fallback.Enabled)
                {
                    result.Errors.Add($"No rules for test '{ct.DisplayName}' — skipped.");
                    continue;
                }

                ProcessTest(ct, orderedRules, testRuleSet, result, doc, clashPlugin.TestsData, fallback);
            }

            LastResult = result;
            return result;
        }

        private void ProcessTest(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, HierarchyFallback fallback)
        {
            try
            {
                ProcessTestCore(test, orderedRules, ruleSet, result, doc, testsData, fallback);
            }
            catch (Exception ex)
            {
                // Nothing has been written unless the final swap succeeded — the
                // document is untouched if we land here before TestsEditTestFromCustom.
                result.Errors.Add($"'{test.DisplayName}': {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void ProcessTestCore(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, HierarchyFallback fallback)
        {
            if (test.Children.Count == 0) return;

            // ── 1. Detached editable copy of the whole test ────────────────
            var workingCopy = (ClashTest)test.CreateCopy();

            // Flatten the copy: explode existing groups (ours or manual) so a
            // re-run regroups from scratch instead of nesting groups in groups.
            // Each result is re-copied so it survives Children.Clear() below.
            int originalCount = CountResults(workingCopy.Children);
            var active = new List<ClashResult>();
            var passThrough = new List<ClashResult>();   // resolved — untouched by rules/grouping
            FlattenResults(workingCopy.Children, active, passThrough);

            if (active.Count == 0) { result.Skipped += passThrough.Count; return; }

            // Sanity guard: if the copies lost their model item references we
            // can neither evaluate rules nor group — abort BEFORE any write.
            if (active.All(c => c.Item1 == null && c.Item2 == null))
            {
                result.Errors.Add($"'{test.DisplayName}': copied results have no item references — aborted, nothing written.");
                return;
            }

            // ── 2. Evaluate rules and edit the detached copies ─────────────
            result.Skipped += passThrough.Count;
            bool anyEdits = false;

            foreach (var clash in active)
            {
                result.ClashesProcessed++;
                ClashRule matched = null;

                foreach (var rule in orderedRules)
                {
                    try
                    {
                        if (EvaluateClash(clash, rule, doc)) { matched = rule; break; }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Rule '{rule.Name}': {ex.Message}");
                    }
                }

                if (matched != null)
                {
                    result.Assigned++;
                    string rAssignee, rGroup;
                    ResolveRuleAssignment(clash, matched, fallback, out rAssignee, out rGroup);
                    EditClashCopy(clash, matched, rAssignee, rGroup, result);
                    result.RecordAssignment(matched.Name, rGroup, rAssignee, matched.Color);
                    anyEdits = true;
                }
                else if (TryAssignByHierarchy(clash, fallback, result))
                {
                    anyEdits = true;
                }
                else
                {
                    result.Unmatched++;
                    result.UnmatchedClashes.Add(clash.DisplayName);
                    if (!string.IsNullOrWhiteSpace(ruleSet.DefaultAssignee))
                    {
                        clash.Description = $"[Unmatched] Default assignee: {ruleSet.DefaultAssignee}";
                        TrySetAssignedTo(clash, ruleSet.DefaultAssignee, result);
                        anyEdits = true;
                    }
                }
            }

            // ── 3. Cluster active clashes into groups (union-find) ─────────
            List<ClashResultGroup> groups;
            List<ClashResult> ungrouped;
            BuildGroups(active, out groups, out ungrouped);

            if (!anyEdits && groups.Count == 0) return;   // nothing to write

            // ── 4. Rebuild children on the copy ────────────────────────────
            workingCopy.Children.Clear();
            foreach (var g in groups) workingCopy.Children.Add(g);
            foreach (var c in ungrouped) workingCopy.Children.Add(c);
            foreach (var c in passThrough) workingCopy.Children.Add(c);

            // Guard: never write back fewer results than we started with.
            int rebuiltCount = CountResults(workingCopy.Children);
            if (rebuiltCount != originalCount)
            {
                result.Errors.Add($"'{test.DisplayName}': rebuilt {rebuiltCount} of {originalCount} results — aborted, nothing written.");
                return;
            }

            // ── 5. Single atomic write-back ─────────────────────────────────
            WriteBack(testsData, test, workingCopy);
            result.GroupsCreated += groups.Count;
            result.TestsWritten++;
        }

        /// <summary>
        /// Atomically replaces a test's results with the edited copy's.
        /// Navisworks 2027 renamed TestsEditTestFromCustom → TestsEditTestFromCopy
        /// (same (test, copy) shape — verified via tools\Dump-NavisApi.ps1), so we
        /// resolve whichever this version exposes. This is the LAST step of
        /// processing: if it throws, nothing has been written to the document.
        /// </summary>
        private static void WriteBack(DocumentClashTests testsData, ClashTest test, ClashTest copy)
        {
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            var methods = testsData.GetType().GetMethods(flags);
            var swap = methods.FirstOrDefault(m => m.Name == "TestsEditTestFromCopy" && m.GetParameters().Length == 2)
                    ?? methods.FirstOrDefault(m => m.Name == "TestsEditTestFromCustom" && m.GetParameters().Length == 2);
            if (swap == null)
                throw new MissingMethodException(
                    "Neither TestsEditTestFromCopy nor TestsEditTestFromCustom exists on DocumentClashTests — cannot write results.");
            swap.Invoke(testsData, new object[] { test, copy });
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Copy helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recursively pulls every ClashResult out of a (copied) children tree,
        /// re-copying each one so it stays valid after the parent collection is
        /// cleared. Active results go to <paramref name="active"/>, resolved
        /// ones to <paramref name="passThrough"/>.
        /// </summary>
        private static void FlattenResults(SavedItemCollection children,
            List<ClashResult> active, List<ClashResult> passThrough)
        {
            foreach (SavedItem si in children)
            {
                if (si is ClashResultGroup grp)
                {
                    FlattenResults(grp.Children, active, passThrough);
                }
                else if (si is ClashResult cr)
                {
                    var copy = (ClashResult)cr.CreateCopy();
                    if (copy.Status == ClashResultStatus.Resolved) passThrough.Add(copy);
                    else active.Add(copy);
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

        /// <summary>
        /// Resolves a matched rule's effective assignee + group for a specific clash.
        /// For Named rules these are the literal fields. For Owning/Other rules the
        /// trade is classified per-clash (always one of the two clashing trades) and
        /// its owner — or the trade name if no owner is set — is used. A blank group
        /// defaults to the resolved trade's group/name.
        /// </summary>
        private void ResolveRuleAssignment(ClashResult clash, ClashRule rule, HierarchyFallback fallback,
            out string assignee, out string group)
        {
            assignee = rule.Assignee;
            group = rule.GroupName;
            if (rule.AssigneeMode == AssigneeMode.Named) return;

            var disc = ResolveRelativeTrade(clash, rule, fallback);
            if (disc != null)
            {
                assignee = string.IsNullOrWhiteSpace(disc.Assignee) ? disc.Name : disc.Assignee;
                if (string.IsNullOrWhiteSpace(group))
                    group = string.IsNullOrWhiteSpace(disc.GroupName) ? disc.Name : disc.GroupName;
            }
            // else: couldn't classify — keep the literal Assignee fallback (may be empty).
        }

        /// <summary>
        /// Picks the trade for a relative-assignee rule: classify the subject item
        /// (Owning) or the opposite item (Other). Needs a populated hierarchy.
        /// </summary>
        private static DisciplineDefinition ResolveRelativeTrade(ClashResult clash, ClashRule rule, HierarchyFallback fallback)
        {
            var hierarchy = fallback?.Hierarchy;
            if (hierarchy?.Disciplines == null || hierarchy.Disciplines.Count == 0) return null;

            ModelItem subject = rule.SubjectItem == ClashItemTarget.Item2 ? clash.Item2 : clash.Item1;
            ModelItem other   = rule.SubjectItem == ClashItemTarget.Item2 ? clash.Item1 : clash.Item2;
            ModelItem target  = rule.AssigneeMode == AssigneeMode.OtherTrade ? other : subject;
            return DisciplineClassifier.Classify(target, hierarchy);
        }

        /// <summary>
        /// Applies a matched rule to a DETACHED clash result copy with its already-
        /// resolved assignee/group. Plain property sets — nothing touches the document.
        /// </summary>
        private void EditClashCopy(ClashResult clash, ClashRule rule, string assignee, string group, ProcessingResult result)
        {
            clash.Description = $"[Group: {group}] [Assignee: {assignee}] Rule: {rule.Name}";

            if (!string.IsNullOrWhiteSpace(assignee))
                TrySetAssignedTo(clash, assignee, result);

            if (!string.IsNullOrWhiteSpace(rule.ClashStatus))
            {
                switch (rule.ClashStatus.ToLowerInvariant())
                {
                    case "active": clash.Status = ClashResultStatus.Active; break;
                    case "reviewed": clash.Status = ClashResultStatus.Reviewed; break;
                    case "approved": clash.Status = ClashResultStatus.Approved; break;
                    case "resolved": clash.Status = ClashResultStatus.Resolved; break;
                }
            }
        }

        /// <summary>
        /// Fallback assignment for a clash no rule matched: classify both items into
        /// disciplines and assign the clash to the LOWER-precedence (responsible)
        /// discipline's owner. Requires BOTH sides to be classifiable so precedence
        /// is meaningful — otherwise returns false and the caller uses the default
        /// assignee. Edits the detached copy only.
        /// </summary>
        private bool TryAssignByHierarchy(ClashResult clash, HierarchyFallback fallback, ProcessingResult result)
        {
            if (fallback == null || !fallback.Enabled) return false;

            var dA = DisciplineClassifier.Classify(clash.Item1, fallback.Hierarchy);
            var dB = DisciplineClassifier.Classify(clash.Item2, fallback.Hierarchy);
            if (dA == null || dB == null) return false;

            var responsible = fallback.Hierarchy.GetResponsible(dA, dB);
            if (responsible == null) return false;

            // The party who resolves is the responsible TRADE — always one of the two
            // trades in the clash (dA or dB). A configured Assignee (person/company)
            // is optional; fall back to the trade name so the clash is still assigned.
            string assignee = string.IsNullOrWhiteSpace(responsible.Assignee) ? responsible.Name : responsible.Assignee;
            string grp = string.IsNullOrWhiteSpace(responsible.GroupName) ? responsible.Name : responsible.GroupName;
            clash.Description =
                $"[Group: {grp}] [Assignee: {assignee}] Hierarchy: {responsible.Name} responsible ({dA.Name} vs {dB.Name})";
            TrySetAssignedTo(clash, assignee, result);

            result.HierarchyAssigned++;
            result.RecordAssignment($"⇣ {responsible.Name} (hierarchy)", grp, assignee, responsible.Color);
            return true;
        }

        /// <summary>Immutable per-run carrier for the hierarchy fallback settings.</summary>
        private sealed class HierarchyFallback
        {
            public SystemHierarchy Hierarchy { get; }
            public bool Enabled { get; }

            public HierarchyFallback(SystemHierarchy hierarchy, bool enabled)
            {
                Hierarchy = hierarchy;
                Enabled = enabled && hierarchy?.Disciplines != null && hierarchy.Disciplines.Count > 0;
            }
        }

        private bool _assignedToUnavailableReported;

        /// <summary>
        /// Sets the AssignedTo field on a detached copy via reflection, because the
        /// property's type differs across Navisworks versions (string in some, an
        /// Assignee wrapper in others). Failure is non-fatal: the assignee always
        /// also lands in Description, so no information is lost.
        /// </summary>
        private void TrySetAssignedTo(ClashResult clash, string assignee, ProcessingResult result)
        {
            try
            {
                var prop = clash.GetType().GetProperty("AssignedTo");
                if (prop != null && prop.CanWrite)
                {
                    if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(clash, assignee);
                        return;
                    }
                    var ctor = prop.PropertyType.GetConstructor(new[] { typeof(string) });
                    if (ctor != null)
                    {
                        prop.SetValue(clash, ctor.Invoke(new object[] { assignee }));
                        return;
                    }
                }
                ReportAssignedToUnavailable(result);
            }
            catch
            {
                ReportAssignedToUnavailable(result);
            }
        }

        private void ReportAssignedToUnavailable(ProcessingResult result)
        {
            if (_assignedToUnavailableReported) return;
            _assignedToUnavailableReported = true;
            result.Errors.Add("AssignedTo column not settable in this API version — assignee recorded in Description instead.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Grouping: connected components over shared elements + proximity
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clusters clashes by connected components where two clashes are linked if:
        ///   (a) they share a model element (Item1 or Item2), OR
        ///   (b) their centres are within ProximityThresholdMeters of each other.
        /// Handles the "1 steel beam vs 4 pipes" case (shared beam) AND congested
        /// zones with no shared element. Components with ≥2 members become groups;
        /// the rest are returned ungrouped.
        /// </summary>
        private static void BuildGroups(List<ClashResult> active,
            out List<ClashResultGroup> groups, out List<ClashResult> ungrouped)
        {
            groups = new List<ClashResultGroup>();
            ungrouped = new List<ClashResult>();
            int n = active.Count;

            if (n < 2)
            {
                ungrouped.AddRange(active);
                return;
            }

            // ── Union-find ──────────────────────────────────────────
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null;
            find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
            Action<int, int> union = (a, b) =>
            {
                int ra = find(a), rb = find(b);
                if (ra != rb) parent[ra] = rb;
            };

            // ── Pass 1: link clashes sharing a model element ────────
            var elementToIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var elementLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var indexElements = new List<string>[n];

            for (int i = 0; i < n; i++)
            {
                indexElements[i] = new List<string>(2);
                IndexElement(active[i].Item1, i, elementToIndices, elementLabels, indexElements[i]);
                IndexElement(active[i].Item2, i, elementToIndices, elementLabels, indexElements[i]);
            }

            foreach (var list in elementToIndices.Values)
                for (int k = 1; k < list.Count; k++)
                    union(list[0], list[k]);

            // ── Pass 2: spatial proximity (O(n²), n is per-test so small) ──
            var centers = new Point3D[n];
            for (int i = 0; i < n; i++) centers[i] = SafeGetCenter(active[i]);

            double thresholdSq = ProximityThresholdMeters * ProximityThresholdMeters;
            for (int i = 0; i < n; i++)
            {
                var ci = centers[i];
                if (ci == null) continue;
                for (int j = i + 1; j < n; j++)
                {
                    var cj = centers[j];
                    if (cj == null) continue;
                    if (find(i) == find(j)) continue;
                    double dx = ci.X - cj.X;
                    double dy = ci.Y - cj.Y;
                    double dz = ci.Z - cj.Z;
                    if (dx * dx + dy * dy + dz * dz <= thresholdSq)
                        union(i, j);
                }
            }

            // ── Collect components ──────────────────────────────────
            var components = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!components.TryGetValue(root, out var list))
                    components[root] = list = new List<int>();
                list.Add(i);
            }

            // Largest groups first (nicer visual ordering in Clash Detective)
            foreach (var members in components.Values.OrderByDescending(c => c.Count))
            {
                if (members.Count < 2)
                {
                    ungrouped.Add(active[members[0]]);
                    continue;
                }

                var grp = new ClashResultGroup();
                grp.DisplayName = $"{PickGroupLabel(members, indexElements, elementLabels)} ({members.Count})";
                foreach (var idx in members)
                    grp.Children.Add(active[idx]);
                groups.Add(grp);
            }
        }

        private static void IndexElement(ModelItem item, int clashIndex,
            Dictionary<string, List<int>> elementToIndices,
            Dictionary<string, string> elementLabels,
            List<string> perClashKeys)
        {
            if (item == null) return;
            var key = GetModelItemKey(item);
            if (key == null) return;

            if (!elementToIndices.TryGetValue(key, out var list))
            {
                list = new List<int>();
                elementToIndices[key] = list;
                elementLabels[key] = !string.IsNullOrWhiteSpace(item.DisplayName)
                    ? item.DisplayName
                    : key.Split('/').LastOrDefault() ?? key;
            }
            list.Add(clashIndex);
            perClashKeys.Add(key);
        }

        /// <summary>
        /// Pick a display label for a group: the element appearing in the most
        /// members of the component (the "hub" element, e.g. the one beam that
        /// four pipes all hit). Falls back to "Proximity cluster".
        /// </summary>
        private static string PickGroupLabel(List<int> members, List<string>[] indexElements,
            Dictionary<string, string> elementLabels)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in members)
            {
                foreach (var key in indexElements[i])
                {
                    counts.TryGetValue(key, out int c);
                    counts[key] = c + 1;
                }
            }
            if (counts.Count == 0) return "Proximity cluster";

            var best = counts.OrderByDescending(kv => kv.Value).First();
            // Only use an element as the label if it's genuinely a hub (in >1 member).
            if (best.Value >= 2 && elementLabels.TryGetValue(best.Key, out var l))
                return l;
            return "Proximity cluster";
        }

        private static Point3D SafeGetCenter(ClashResult cr)
        {
            try { return cr.Center; }
            catch { return null; }
        }

        /// <summary>
        /// Stable identity key for a model element: InstanceGuid when present,
        /// otherwise a short display-name path (fragile but works on legacy nodes).
        /// </summary>
        private static string GetModelItemKey(ModelItem item)
        {
            if (item == null) return null;

            try
            {
                if (item.InstanceGuid != Guid.Empty)
                    return "G:" + item.InstanceGuid;
            }
            catch { }

            var parts = new List<string>();
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                if (!string.IsNullOrWhiteSpace(cur.DisplayName))
                    parts.Insert(0, cur.DisplayName);
                cur = cur.Parent;
                depth++;
            }
            return parts.Count > 0 ? "P:" + string.Join("/", parts) : null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Rule evaluation (read-only)
        // ─────────────────────────────────────────────────────────────────────

        private bool EvaluateClash(ClashResult clash, ClashRule rule, Document doc)
        {
            Func<ClashItemTarget, string, string, string> getProp = (target, category, property) =>
            {
                ModelItem item = target == ClashItemTarget.Item1 ? clash.Item1 :
                                 target == ClashItemTarget.Item2 ? clash.Item2 : null;
                if (item == null) return null;
                return GetPropertyValue(item, category, property);
            };
            return rule.Evaluate(getProp);
        }

        private string GetPropertyValue(ModelItem item, string categoryName, string propertyName)
        {
            if (item == null) return null;
            foreach (PropertyCategory cat in item.PropertyCategories)
            {
                if (string.Equals(cat.DisplayName, categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (DataProperty prop in cat.Properties)
                    {
                        if (string.Equals(prop.DisplayName, propertyName, StringComparison.OrdinalIgnoreCase))
                            return GetValueString(prop);
                    }
                }
            }
            if (item.Parent != null)
                return GetPropertyValue(item.Parent, categoryName, propertyName);
            return null;
        }

        private string GetValueString(DataProperty prop)
        {
            if (prop.Value == null) return null;
            var val = prop.Value;
            if (val.IsDisplayString) return val.ToDisplayString();
            if (val.IsDouble) return val.ToDouble().ToString();
            if (val.IsInt32) return val.ToInt32().ToString();
            if (val.IsBoolean) return val.ToBoolean().ToString();
            if (val.IsDoubleLength) return val.ToDoubleLength().ToString();
            if (val.IsNamedConstant) return val.ToNamedConstant().DisplayName;
            return val.ToString();
        }
    }

    public class ProcessingResult
    {
        public string TestName { get; set; } = "";
        public int TestsProcessed { get; set; }
        public int TestsWritten { get; set; }
        public int ClashesProcessed { get; set; }
        public int Assigned { get; set; }
        public int HierarchyAssigned { get; set; }
        public int Unmatched { get; set; }
        public int Skipped { get; set; }
        public int GroupsCreated { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> UnmatchedClashes { get; set; } = new List<string>();
        public Dictionary<string, AssignmentSummary> AssignmentsByRule { get; set; } = new Dictionary<string, AssignmentSummary>();

        public void RecordAssignment(string ruleName, string groupName, string assignee, string color = "#2563EB")
        {
            if (!AssignmentsByRule.ContainsKey(ruleName))
                AssignmentsByRule[ruleName] = new AssignmentSummary { RuleName = ruleName, GroupName = groupName, Assignee = assignee, Color = color };
            AssignmentsByRule[ruleName].Count++;
        }

        public string GetSummary()
        {
            var lines = new List<string>
            {
                $"Test: {TestName}",
                $"Clashes evaluated: {ClashesProcessed}",
                $"Assigned by rules: {Assigned}",
                $"Assigned by hierarchy: {HierarchyAssigned}",
                $"Unmatched: {Unmatched}",
                $"Skipped (resolved): {Skipped}",
                $"Groups created: {GroupsCreated}", ""
            };
            if (AssignmentsByRule.Count > 0)
            {
                lines.Add("Assignments:");
                foreach (var kvp in AssignmentsByRule.OrderBy(k => k.Key))
                    lines.Add($"  {kvp.Value.RuleName}: {kvp.Value.Count} -> {kvp.Value.GroupName} ({kvp.Value.Assignee})");
            }
            if (Errors.Count > 0)
            {
                lines.Add($"\nWarnings ({Errors.Count}):");
                foreach (var e in Errors.Take(10)) lines.Add($"  ! {e}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public class AssignmentSummary
    {
        public string RuleName { get; set; }
        public string GroupName { get; set; }
        public string Assignee { get; set; }
        public string Color { get; set; } = "#2563EB";
        public int Count { get; set; }
    }
}
