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
    /// Clash Detective using the SDK-supported transaction pattern proven by
    /// Autodesk's own ClashGrouper sample (api\...\ClashDetective\ClashGrouper):
    ///
    ///   1. Flatten the LIVE test into detached ClashResult copies, each with
    ///      Guid = Guid.Empty (re-inserting a result with its original GUID is
    ///      what corrupted Clash Detective and crashed Navisworks).
    ///   2. Evaluate rules on the copies and set Status/AssignedTo/Description
    ///      (plain property sets on detached copies — touches nothing live).
    ///   3. Cluster into ClashResultGroups in memory.
    ///   4. ONE Transaction per test: CreateCopyWithoutChildren →
    ///      TestsReplaceWithCopy(parent, i, newTest) → TestsAddCopy each group /
    ///      result into the live test (TestsAddCopy deep-copies a group with its
    ///      children) → Commit. An uncommitted transaction rolls back on dispose,
    ///      so any failure leaves the document untouched.
    ///
    /// TestsEditTestFromCopy is SETTINGS-ONLY (rename/selections) and CANNOT be
    /// used to swap in regrouped children — that was the root cause of the
    /// earlier crashes. See memory: navisworks-sdk-clash-api.
    /// </summary>
    public class ClashProcessingService
    {
        public ProcessingResult LastResult { get; private set; }

        // ── Grouping settings (set from ProjectConfig before a run) ─────────────
        /// <summary>How active clashes are clustered. Default = Hybrid (original).</summary>
        public ClashGroupingMode GroupingMode { get; set; } = ClashGroupingMode.Hybrid;
        /// <summary>Proximity threshold (metres) for Proximity/Hybrid grouping.</summary>
        public double ProximityThreshold { get; set; } = 1.0;
        /// <summary>Group first, then give each group one assignee (majority of members).</summary>
        public bool AssignByGroup { get; set; } = false;

        /// <summary>Copies grouping settings off a ProjectConfig before processing.</summary>
        public void ApplyGroupingSettings(ProjectConfig config)
        {
            if (config == null) return;
            GroupingMode = config.GroupingMode;
            ProximityThreshold = config.ProximityThreshold > 0 ? config.ProximityThreshold : 1.0;
            AssignByGroup = config.AssignByGroup;
        }

        // ── Progress / cancel (UI-thread; the panel pumps the progress window) ──
        private Action<string> _progress;
        private Func<bool> _cancelled;
        private void Report(string msg) { try { _progress?.Invoke(msg); } catch { } }
        private bool IsCancelled { get { try { return _cancelled != null && _cancelled(); } catch { return false; } } }

        // Per-run discipline-classification cache (keyed by model-item identity), so a
        // model element shared across many clashes is classified once, not repeatedly.
        private Dictionary<string, DisciplineDefinition> _classifyCache;

        // Reports progress roughly every this many clashes during evaluation.
        private const int ProgressEvery = 250;

        /// <summary>
        /// Process a specific clash test using its associated rule set
        /// </summary>
        public ProcessingResult ProcessSingleTest(string testName, TestRuleSet ruleSet,
            SystemHierarchy hierarchy = null, bool useHierarchyFallback = false,
            Action<string> progress = null, Func<bool> cancelled = null)
        {
            _assignedToUnavailableReported = false;
            _progress = progress; _cancelled = cancelled;
            _classifyCache = new Dictionary<string, DisciplineDefinition>(StringComparer.OrdinalIgnoreCase);
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
            ProcessTest(targetTest, orderedRules, ruleSet, result, doc, clashPlugin.TestsData, fallback, testName);

            LastResult = result;
            return result;
        }

        /// <summary>
        /// Process all clash tests using the project config
        /// </summary>
        public ProcessingResult ProcessAllTests(ProjectConfig config,
            Action<string> progress = null, Func<bool> cancelled = null)
        {
            _assignedToUnavailableReported = false;
            _progress = progress; _cancelled = cancelled;
            _classifyCache = new Dictionary<string, DisciplineDefinition>(StringComparer.OrdinalIgnoreCase);
            var result = new ProcessingResult { TestName = "All Tests" };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            // Snapshot the test list first — we swap tests while iterating otherwise
            var tests = ClashApiCompat.GetAllTests(clashPlugin.TestsData);
            var fallback = new HierarchyFallback(config.Hierarchy, config.UseHierarchyFallback);

            for (int ti = 0; ti < tests.Count; ti++)
            {
                if (IsCancelled) { result.Errors.Add("Cancelled — remaining tests not processed."); break; }
                var ct = tests[ti];
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

                string label = $"Test {ti + 1}/{tests.Count}: {ct.DisplayName}";
                ProcessTest(ct, orderedRules, testRuleSet, result, doc, clashPlugin.TestsData, fallback, label);
            }

            LastResult = result;
            return result;
        }

        private void ProcessTest(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, HierarchyFallback fallback, string testLabel)
        {
            try
            {
                ProcessTestCore(test, orderedRules, ruleSet, result, doc, testsData, fallback, testLabel);
            }
            catch (Exception ex)
            {
                // Nothing has been written unless the final swap succeeded — the
                // document is untouched if we land here before TestsEditTestFromCustom.
                result.Errors.Add($"'{test.DisplayName}': {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void ProcessTestCore(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, HierarchyFallback fallback, string testLabel)
        {
            if (test.Children.Count == 0) return;
            Report($"{testLabel} — reading clashes…");

            // ── 1. Flatten the LIVE test into detached, re-insertable copies ──
            // Explode existing groups (ours or manual) so a re-run regroups from
            // scratch instead of nesting groups in groups. Each copy gets a fresh
            // empty GUID so re-inserting it can't duplicate a result GUID.
            int originalCount = CountResults(test.Children);
            var active = new List<ClashResult>();
            var passThrough = new List<ClashResult>();   // resolved — untouched by rules/grouping
            FlattenResults(test.Children, active, passThrough);

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

            int evaluated = 0, activeTotal = active.Count;
            foreach (var clash in active)
            {
                if ((evaluated % ProgressEvery) == 0)
                {
                    Report($"{testLabel} — assigning {evaluated:N0}/{activeTotal:N0}…");
                    if (IsCancelled) { result.Errors.Add($"'{test.DisplayName}': cancelled before write — nothing written."); return; }
                }
                evaluated++;

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
            Report($"{testLabel} — grouping {active.Count:N0} clashes…");
            if (IsCancelled) { result.Errors.Add($"'{test.DisplayName}': cancelled before write — nothing written."); return; }
            List<ClashResultGroup> groups;
            List<ClashResult> ungrouped;
            BuildGroups(active, out groups, out ungrouped);

            // Group-then-assign: give each bundle a single trade (majority of members).
            if (AssignByGroup && groups.Count > 0)
                UnifyGroupAssignees(groups, result);

            if (!anyEdits && groups.Count == 0) return;   // nothing to write

            // Guard: never write back fewer results than we started with.
            int rebuiltCount = groups.Sum(g => g.Children.Count) + ungrouped.Count + passThrough.Count;
            if (rebuiltCount != originalCount)
            {
                result.Errors.Add($"'{test.DisplayName}': rebuilt {rebuiltCount} of {originalCount} results — aborted, nothing written.");
                return;
            }

            // ── 4. Single atomic write-back (transaction) ──────────────────
            Report($"{testLabel} — writing {groups.Count:N0} group(s)…");
            WriteBack(doc, testsData, test, groups, ungrouped, passThrough);
            result.GroupsCreated += groups.Count;
            result.TestsWritten++;
        }

        /// <summary>
        /// Atomically replaces a test's results with the regrouped/edited copies,
        /// using the SDK-supported transaction pattern (ClashGrouper sample):
        /// replace the live test with a childless copy, then TestsAddCopy each
        /// group/result into it. TestsAddCopy deep-copies a group together with
        /// its children, so one call per top-level item is enough. The live
        /// parent is re-fetched by index on every call (the collection mutates
        /// as we add). If anything throws, the transaction is never committed and
        /// dispose rolls the whole thing back — the document is left untouched.
        /// </summary>
        private static void WriteBack(Document doc, DocumentClashTests testsData, ClashTest test,
            List<ClashResultGroup> groups, List<ClashResult> ungrouped, List<ClashResult> passThrough)
        {
            using (var t = doc.BeginTransaction("Clash Rule Engine — assign & group"))
            {
                var newTest = (ClashTest)test.CreateCopyWithoutChildren();
                var parent = test.Parent;
                int i = parent.Children.IndexOf(test);

                // Replacing disposes the original `test`; do not touch it afterwards.
                testsData.TestsReplaceWithCopy(parent, i, newTest);

                foreach (var g in groups)
                    testsData.TestsAddCopy((GroupItem)parent.Children[i], g);
                foreach (var c in ungrouped)
                    testsData.TestsAddCopy((GroupItem)parent.Children[i], c);
                foreach (var c in passThrough)
                    testsData.TestsAddCopy((GroupItem)parent.Children[i], c);

                t.Commit();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Copy helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Recursively pulls every ClashResult out of the live test's children
        /// tree, re-copying each one as a detached, re-insertable result. Every
        /// copy gets Guid = Guid.Empty: re-adding a result that keeps its original
        /// GUID duplicates it and corrupts Clash Detective (the old crash). Active
        /// results go to <paramref name="active"/>, resolved ones (left untouched
        /// by rules/grouping) to <paramref name="passThrough"/>.
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
                    copy.Guid = Guid.Empty;
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
        private DisciplineDefinition ResolveRelativeTrade(ClashResult clash, ClashRule rule, HierarchyFallback fallback)
        {
            var hierarchy = fallback?.Hierarchy;
            if (hierarchy?.Disciplines == null || hierarchy.Disciplines.Count == 0) return null;

            ModelItem subject = rule.SubjectItem == ClashItemTarget.Item2 ? clash.Item2 : clash.Item1;
            ModelItem other   = rule.SubjectItem == ClashItemTarget.Item2 ? clash.Item1 : clash.Item2;
            ModelItem target  = rule.AssigneeMode == AssigneeMode.OtherTrade ? other : subject;
            return ClassifyCached(target, hierarchy);
        }

        /// <summary>
        /// Discipline classification with a per-run cache keyed by model-item identity.
        /// A beam clashing 40 pipes is classified once, not 40×. Falls back to a direct
        /// classify when the item has no stable key.
        /// </summary>
        private DisciplineDefinition ClassifyCached(ModelItem item, SystemHierarchy hierarchy)
        {
            if (item == null) return null;
            string key = GetModelItemKey(item);
            if (key == null || _classifyCache == null)
                return DisciplineClassifier.Classify(item, hierarchy);

            if (_classifyCache.TryGetValue(key, out var cached)) return cached;
            var disc = DisciplineClassifier.Classify(item, hierarchy);
            _classifyCache[key] = disc;
            return disc;
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

            var dA = ClassifyCached(clash.Item1, fallback.Hierarchy);
            var dB = ClassifyCached(clash.Item2, fallback.Hierarchy);
            if (dA == null || dB == null) return false;

            var responsible = fallback.Hierarchy.GetResponsible(dA, dB);
            if (responsible == null) return false;

            // The responsible (lower-precedence) trade normally takes the clash. But a
            // discipline can be set to route to the OTHER trade in any test (e.g. a
            // "Hydraulic Drainage" sub-discipline → the other service). OwningTrade /
            // Named both resolve to the responsible discipline's own owner.
            var other = responsible == dA ? dB : dA;
            var target = responsible.AssigneeMode == AssigneeMode.OtherTrade ? other : responsible;

            string assignee = string.IsNullOrWhiteSpace(target.Assignee) ? target.Name : target.Assignee;
            string grp = string.IsNullOrWhiteSpace(target.GroupName) ? target.Name : target.GroupName;

            string how = responsible.AssigneeMode == AssigneeMode.OtherTrade
                ? $"{responsible.Name} → other trade {target.Name}"
                : $"{responsible.Name} responsible";
            clash.Description =
                $"[Group: {grp}] [Assignee: {assignee}] Hierarchy: {how} ({dA.Name} vs {dB.Name})";
            TrySetAssignedTo(clash, assignee, result);

            result.HierarchyAssigned++;
            result.RecordAssignment($"⇣ {target.Name} (hierarchy)", grp, assignee, target.Color);
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
        /// Sets the AssignedTo field on a detached copy using the first-class
        /// Assignee type (verified RW on ClashResult in NW 2027 via the API dump).
        /// Failure is non-fatal: the assignee always also lands in Description, so
        /// no information is lost.
        /// </summary>
        private void TrySetAssignedTo(ClashResult clash, string assignee, ProcessingResult result)
        {
            if (string.IsNullOrWhiteSpace(assignee)) return;
            try
            {
                clash.AssignedTo = new Assignee(assignee);
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
        /// Clusters active clashes into groups per the selected <see cref="GroupingMode"/>.
        /// Components with ≥2 members become Clash Detective groups; the rest are ungrouped.
        /// </summary>
        private void BuildGroups(List<ClashResult> active,
            out List<ClashResultGroup> groups, out List<ClashResult> ungrouped)
        {
            switch (GroupingMode)
            {
                case ClashGroupingMode.None:
                    groups = new List<ClashResultGroup>();
                    ungrouped = new List<ClashResult>(active);
                    return;
                case ClashGroupingMode.ByAssignee:
                    GroupByKey(active, SafeAssignee, "Unassigned", out groups, out ungrouped);
                    return;
                case ClashGroupingMode.Grid:
                    GroupByKey(active, GridKey, "Off-grid", out groups, out ungrouped);
                    return;
                case ClashGroupingMode.Level:
                    GroupByKey(active, LevelKey, "No level", out groups, out ungrouped);
                    return;
                case ClashGroupingMode.SharedElement:
                    UnionFindGroups(active, linkShared: true, linkProximity: false, out groups, out ungrouped);
                    return;
                case ClashGroupingMode.Proximity:
                    UnionFindGroups(active, linkShared: false, linkProximity: true, out groups, out ungrouped);
                    return;
                default: // Hybrid
                    UnionFindGroups(active, linkShared: true, linkProximity: true, out groups, out ungrouped);
                    return;
            }
        }

        /// <summary>
        /// Buckets clashes by a string key (assignee / grid cell / level). Buckets of
        /// ≥2 become groups named by the key; singletons are returned ungrouped.
        /// </summary>
        private void GroupByKey(List<ClashResult> active, Func<ClashResult, string> keyFn,
            string nullLabel, out List<ClashResultGroup> groups, out List<ClashResult> ungrouped)
        {
            groups = new List<ClashResultGroup>();
            ungrouped = new List<ClashResult>();

            var buckets = new Dictionary<string, List<ClashResult>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in active)
            {
                string key = null;
                try { key = keyFn(c); } catch { }
                if (string.IsNullOrWhiteSpace(key)) key = nullLabel;
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<ClashResult>();
                list.Add(c);
            }

            foreach (var kv in buckets.OrderByDescending(b => b.Value.Count))
            {
                if (kv.Value.Count < 2) { ungrouped.Add(kv.Value[0]); continue; }
                var grp = new ClashResultGroup { DisplayName = $"{kv.Key} ({kv.Value.Count})" };
                foreach (var c in kv.Value) grp.Children.Add(c);
                groups.Add(grp);
            }
        }

        private static string SafeAssignee(ClashResult cr)
        {
            try { return cr.AssignedTo?.DisplayName; } catch { return null; }
        }

        private static string GridKey(ClashResult cr)
        {
            try
            {
                var sys = Autodesk.Navisworks.Api.Application.MainDocument?.Grids?.ActiveSystem;
                return sys?.ClosestIntersection(cr.Center)?.DisplayName;
            }
            catch { return null; }
        }

        private static string LevelKey(ClashResult cr)
        {
            try
            {
                var sys = Autodesk.Navisworks.Api.Application.MainDocument?.Grids?.ActiveSystem;
                return sys?.ClosestIntersection(cr.Center)?.Level?.DisplayName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Connected-components clustering. Two clashes are linked if (when enabled)
        /// they share a model element, OR their centres are within
        /// <see cref="ProximityThreshold"/>. Components with ≥2 members become groups.
        /// </summary>
        private void UnionFindGroups(List<ClashResult> active, bool linkShared, bool linkProximity,
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

            if (linkShared)
                foreach (var list in elementToIndices.Values)
                    for (int k = 1; k < list.Count; k++)
                        union(list[0], list[k]);

            // ── Pass 2: spatial proximity via a uniform grid hash ──────────
            // Bucket centres into cells of edge = threshold; a clash can only be
            // within threshold of clashes in its own + 26 neighbouring cells. This
            // turns the old O(n²) sweep into ~O(n) for large tests (e.g. 12k clashes).
            if (linkProximity)
            {
                var centers = new Point3D[n];
                for (int i = 0; i < n; i++) centers[i] = SafeGetCenter(active[i]);

                double threshold = ProximityThreshold > 0 ? ProximityThreshold : 1.0;
                double thresholdSq = threshold * threshold;
                double inv = 1.0 / threshold;

                var cells = new Dictionary<long, List<int>>();
                long CellKey(double x, double y, double z)
                {
                    // Pack three 21-bit cell coords into one long (enough for any real model extent).
                    long cx = (long)Math.Floor(x * inv) & 0x1FFFFF;
                    long cy = (long)Math.Floor(y * inv) & 0x1FFFFF;
                    long cz = (long)Math.Floor(z * inv) & 0x1FFFFF;
                    return (cx << 42) | (cy << 21) | cz;
                }

                for (int i = 0; i < n; i++)
                {
                    var c = centers[i];
                    if (c == null) continue;
                    long key = CellKey(c.X, c.Y, c.Z);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<int>();
                    list.Add(i);
                }

                for (int i = 0; i < n; i++)
                {
                    var ci = centers[i];
                    if (ci == null) continue;
                    long bx = (long)Math.Floor(ci.X * inv);
                    long by = (long)Math.Floor(ci.Y * inv);
                    long bz = (long)Math.Floor(ci.Z * inv);

                    for (int ox = -1; ox <= 1; ox++)
                        for (int oy = -1; oy <= 1; oy++)
                            for (int oz = -1; oz <= 1; oz++)
                            {
                                long nk = (((bx + ox) & 0x1FFFFF) << 42) | (((by + oy) & 0x1FFFFF) << 21) | ((bz + oz) & 0x1FFFFF);
                                if (!cells.TryGetValue(nk, out var bucket)) continue;
                                foreach (int j in bucket)
                                {
                                    if (j <= i) continue;
                                    var cj = centers[j];
                                    if (cj == null) continue;
                                    if (find(i) == find(j)) continue;
                                    double dx = ci.X - cj.X, dy = ci.Y - cj.Y, dz = ci.Z - cj.Z;
                                    if (dx * dx + dy * dy + dz * dz <= thresholdSq) union(i, j);
                                }
                            }
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

        /// <summary>
        /// Gives every member of a group the SAME assignee — the one most members
        /// already resolved to (rules/hierarchy). Ties fall to the first seen. This
        /// is the "group first, then assign" behaviour: one bundle → one trade.
        /// </summary>
        private void UnifyGroupAssignees(List<ClashResultGroup> groups, ProcessingResult result)
        {
            foreach (var g in groups)
            {
                var tally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (SavedItem si in g.Children)
                    if (si is ClashResult cr)
                    {
                        string a = SafeAssignee(cr);
                        if (!string.IsNullOrWhiteSpace(a)) { tally.TryGetValue(a, out int c); tally[a] = c + 1; }
                    }
                if (tally.Count == 0) continue;

                string winner = tally.OrderByDescending(kv => kv.Value).First().Key;
                foreach (SavedItem si in g.Children)
                    if (si is ClashResult cr)
                    {
                        TrySetAssignedTo(cr, winner, result);
                        cr.Description = $"[Group: {g.DisplayName}] [Assignee: {winner}] (group-assigned)";
                    }
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
                if (RuleCondition.IsTreePathRef(category, property))
                    return BuildTreePath(item);
                return GetPropertyValue(item, category, property);
            };
            return rule.Evaluate(getProp);
        }

        /// <summary>
        /// The element's full ancestor path (leaf → root display names) as one
        /// string. Element TYPE (Conduit, Cable Tray, Clearance Zone, Hanger Rod…)
        /// lives here in coordination NWCs, not in the solid's properties, so this
        /// is what Tree-Path rule conditions match against.
        /// </summary>
        private string BuildTreePath(ModelItem item)
        {
            var parts = new List<string>();
            var cur = item;
            int depth = 0;
            while (cur != null && depth < 16)
            {
                if (!string.IsNullOrWhiteSpace(cur.DisplayName)) parts.Add(cur.DisplayName);
                cur = cur.Parent;
                depth++;
            }
            return parts.Count > 0 ? string.Join(" / ", parts) : null;
        }

        private string GetPropertyValue(ModelItem item, string categoryName, string propertyName)
        {
            if (item == null) return null;
            // Empty category = match the property in ANY category. Lets imported/AI
            // rules key on a property (e.g. "Material") without knowing its tab.
            bool anyCategory = string.IsNullOrWhiteSpace(categoryName);
            foreach (PropertyCategory cat in item.PropertyCategories)
            {
                if (anyCategory || string.Equals(cat.DisplayName, categoryName, StringComparison.OrdinalIgnoreCase))
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
