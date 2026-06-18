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

        /// <summary>Global element-kind rules (ordered, first-match-wins).</summary>
        public List<KindRule> KindRules { get; set; }
        public bool UseKindRules { get; set; } = true;

        /// <summary>Auto-approve policy (gap + trade gate). Applied after assignment.</summary>
        public ApprovePolicy ApprovePolicy { get; set; }

        /// <summary>Incremental mode: only process NEW + unassigned clashes; leave every
        /// already-triaged clash and existing group exactly as it is.</summary>
        public bool OnlyNewClashes { get; set; } = false;

        /// <summary>Copies grouping + kind-rule settings off a ProjectConfig before processing.</summary>
        public void ApplyGroupingSettings(ProjectConfig config)
        {
            if (config == null) return;
            GroupingMode = config.GroupingMode;
            ProximityThreshold = config.ProximityThreshold > 0 ? config.ProximityThreshold : 1.0;
            AssignByGroup = config.AssignByGroup;
            KindRules = config.KindRules;
            UseKindRules = config.UseKindRules;
            ApprovePolicy = config.ApprovePolicy;
            OnlyNewClashes = config.OnlyAssignNewClashes;
        }

        // Per-run element-kind cache (keyed by model-item identity).
        private Dictionary<string, ElementKindInfo> _kindCache;
        // Per-run rule-matching caches (keyed by model-item identity): the tree path and
        // each (category|property) value are computed ONCE per element and reused across
        // every clash and every rule — the big speed win when there are many rules.
        private Dictionary<string, string> _treePathCache;
        private Dictionary<string, Dictionary<string, string>> _propValCache;

        private string CachedTreePath(string key, ModelItem item)
        {
            if (item == null) return null;
            if (key == null || _treePathCache == null) return BuildTreePath(item);
            if (_treePathCache.TryGetValue(key, out var v)) return v;
            v = BuildTreePath(item); _treePathCache[key] = v; return v;
        }

        private string CachedProp(string key, ModelItem item, string category, string property)
        {
            if (item == null) return null;
            if (key == null || _propValCache == null) return GetPropertyValue(item, category, property);
            if (!_propValCache.TryGetValue(key, out var d))
                _propValCache[key] = d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string vk = (category ?? "") + "" + (property ?? "");
            if (d.TryGetValue(vk, out var v)) return v;
            v = GetPropertyValue(item, category, property); d[vk] = v; return v;
        }

        /// <summary>Builds the (cached) property accessor for one clash — element keys are
        /// computed ONCE here, not per rule, so all of a clash's rules share the lookups.</summary>
        private Func<ClashItemTarget, string, string, string> MakeGetProp(ClashResult clash)
        {
            ModelItem i1 = null, i2 = null;
            try { i1 = clash.Item1; } catch { }
            try { i2 = clash.Item2; } catch { }
            string k1 = GetModelItemKey(i1), k2 = GetModelItemKey(i2);
            return (target, category, property) =>
            {
                ModelItem item; string key;
                if (target == ClashItemTarget.Item1) { item = i1; key = k1; }
                else if (target == ClashItemTarget.Item2) { item = i2; key = k2; }
                else return null;
                if (item == null) return null;
                return RuleCondition.IsTreePathRef(category, property)
                    ? CachedTreePath(key, item)
                    : CachedProp(key, item, category, property);
            };
        }

        private ElementKindInfo KindCached(ModelItem item)
        {
            if (item == null) return null;
            string key = GetModelItemKey(item);
            if (key == null || _kindCache == null) return ElementKind.Compute(item);
            if (_kindCache.TryGetValue(key, out var cached)) return cached;
            var info = ElementKind.Compute(item);
            _kindCache[key] = info;
            return info;
        }

        /// <summary>
        /// Element-kind assignment: the first KindRule whose detection matches a side
        /// wins; the clash is assigned to that rule's named trade. Edits the
        /// detached copy only.
        /// </summary>
        private bool TryAssignByKind(ClashResult clash, ProcessingResult result)
        {
            if (!UseKindRules || KindRules == null || KindRules.Count == 0) return false;

            ElementKindInfo a = null, b = null;
            try { a = KindCached(clash.Item1); } catch { }
            try { b = KindCached(clash.Item2); } catch { }
            if (a == null && b == null) return false;

            foreach (var kr in KindRules)
            {
                if (kr == null || !kr.IsEnabled) continue;

                ModelItem matched = null;
                if ((kr.Side == KindMatchSide.Either || kr.Side == KindMatchSide.ItemA) && a != null && kr.MatchesItem(a))
                { matched = clash.Item1; }
                else if ((kr.Side == KindMatchSide.Either || kr.Side == KindMatchSide.ItemB) && b != null && kr.MatchesItem(b))
                { matched = clash.Item2; }

                if (matched == null) continue;

                // Without the discipline hierarchy, Owner/Other can't resolve a relative
                // trade — fall back to the rule's named assignee for every mode.
                string assignee = kr.Assignee;
                if (string.IsNullOrWhiteSpace(assignee)) continue;   // can't resolve — let a later rule try

                string grp = string.IsNullOrWhiteSpace(kr.GroupName) ? assignee : kr.GroupName;
                clash.Description = $"[Group: {grp}] [Assignee: {assignee}] Kind: {kr.Name}";
                TrySetAssignedTo(clash, assignee, result);
                result.Assigned++;
                result.RecordAssignment("◆ " + (string.IsNullOrWhiteSpace(kr.Name) ? "kind rule" : kr.Name), grp, assignee, "#0EA5E9");
                return true;
            }
            return false;
        }

        /// <summary>
        /// The approve engine. After assignment, sets Status = Approved on each detached
        /// copy whose clearance gap is inside the policy zone AND whose trade pairing is
        /// negotiable (never structure). Edits copies only — survives the atomic write-back
        /// exactly like an assignment. Returns the number approved.
        /// </summary>
        private int ApproveWithinTolerance(List<ClashResult> active, string testLabel,
            ProcessingResult result)
        {
            var pol = ApprovePolicy;
            if (pol == null || !pol.Enabled || active == null || active.Count == 0) return 0;

            // Cheap, robust guard: a test that names a protected trade (e.g. "_MECH vs _STR")
            // approves nothing — matches the data (0% approved in any vs-structure test).
            if (pol.UseTestNameGuard && pol.IsProtected(testLabel)) return 0;

            // The trade pair comes from the test name ("_FIRE vs _MECH" → FIRE,MECH) — that's
            // the pairing used to pick the per-pair clearance floor.
            ApprovePolicy.ParseTestTrades(testLabel, out string pa, out string pb);

            int approved = 0;
            foreach (var clash in active)
            {
                try
                {
                    if (clash.Status == ClashResultStatus.Approved ||
                        clash.Status == ClashResultStatus.Resolved) continue;

                    if (pol.RequireAssignee && string.IsNullOrWhiteSpace(SafeAssignee(clash))) continue;

                    // Always-approve layer (data-driven, gap-independent): flexible elements
                    // (flex pipe/duct — they bend) and certain assignees (tundish) are approved
                    // even on a hard clash.
                    if (pol.HasAlwaysApprove)
                    {
                        if (pol.IsApproveAssignee(SafeAssignee(clash)))
                        { clash.Status = ClashResultStatus.Approved; approved++; continue; }

                        string ka = null, kb = null;
                        try { ka = KindCached(clash.Item1)?.Text; } catch { }
                        try { kb = KindCached(clash.Item2)?.Text; } catch { }
                        if (pol.KindApproved(ka, kb))
                        { clash.Status = ClashResultStatus.Approved; approved++; continue; }
                    }

                    double gapMm;
                    try { gapMm = clash.Distance * 1000.0; } catch { continue; }

                    // Per-pair clearance floor: approve only once the gap clears this pairing's
                    // threshold (ELEC·MECH 50 mm, FIRE·MECH 15 mm, HYD·MECH any, …).
                    if (!pol.GapApproved(gapMm, pa, pb)) continue;

                    clash.Status = ClashResultStatus.Approved;
                    approved++;
                }
                catch { /* leave this clash untouched */ }
            }

            if (approved > 0) result.Approved += approved;
            return approved;
        }

        // ── Progress / cancel (UI-thread; the panel pumps the progress window) ──
        private Action<string> _progress;
        private Func<bool> _cancelled;
        private void Report(string msg) { try { _progress?.Invoke(msg); } catch { } }
        private bool IsCancelled { get { try { return _cancelled != null && _cancelled(); } catch { return false; } } }

        // Reports progress roughly every this many clashes during evaluation.
        private const int ProgressEvery = 250;

        /// <summary>
        /// Process a specific clash test using its associated rule set
        /// </summary>
        public ProcessingResult ProcessSingleTest(string testName, TestRuleSet ruleSet,
            Action<string> progress = null, Func<bool> cancelled = null)
        {
            _assignedToUnavailableReported = false;
            _progress = progress; _cancelled = cancelled;
            _kindCache = new Dictionary<string, ElementKindInfo>(StringComparer.OrdinalIgnoreCase);
            _treePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _propValCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
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
            ProcessTest(targetTest, orderedRules, ruleSet, result, doc, clashPlugin.TestsData, testName);

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
            _kindCache = new Dictionary<string, ElementKindInfo>(StringComparer.OrdinalIgnoreCase);
            _treePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _propValCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var result = new ProcessingResult { TestName = "All Tests" };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            // Snapshot the test list first — we swap tests while iterating otherwise
            var tests = ClashApiCompat.GetAllTests(clashPlugin.TestsData);

            for (int ti = 0; ti < tests.Count; ti++)
            {
                if (IsCancelled) { result.Errors.Add("Cancelled — remaining tests not processed."); break; }
                var ct = tests[ti];
                result.TestsProcessed++;

                // Fuzzy-match the live test to an imported rule set (handles "_MECH vs _FIRE"
                // vs "FIRE vs MECH" vs "MC v FC" etc.) — does NOT create empty sets.
                var testRuleSet = config.FindRuleSet(ct.DisplayName);
                var orderedRules = testRuleSet?.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList()
                                   ?? new List<ClashRule>();

                // A test with no matching rules and no per-test default has nothing to do — skip.
                bool hasDefault = testRuleSet != null
                    && !string.IsNullOrWhiteSpace(testRuleSet.DefaultAssignee)
                    && !string.Equals(testRuleSet.DefaultAssignee, "Unassigned", StringComparison.OrdinalIgnoreCase);
                if (orderedRules.Count == 0 && !hasDefault &&
                    !(UseKindRules && KindRules != null && KindRules.Count > 0))
                {
                    result.Errors.Add($"No rules for test '{ct.DisplayName}' — skipped.");
                    continue;
                }
                if (testRuleSet == null) testRuleSet = new TestRuleSet { TestName = ct.DisplayName };

                string label = $"Test {ti + 1}/{tests.Count}: {ct.DisplayName}";
                ProcessTest(ct, orderedRules, testRuleSet, result, doc, clashPlugin.TestsData, label);
            }

            LastResult = result;
            return result;
        }

        private void ProcessTest(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, string testLabel)
        {
            try
            {
                ProcessTestCore(test, orderedRules, ruleSet, result, doc, testsData, testLabel);
            }
            catch (Exception ex)
            {
                // Nothing has been written unless the final swap succeeded — the
                // document is untouched if we land here before TestsEditTestFromCustom.
                result.Errors.Add($"'{test.DisplayName}': {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        private void ProcessTestCore(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet,
            ProcessingResult result, Document doc, DocumentClashTests testsData, string testLabel)
        {
            if (test.Children.Count == 0) return;
            Report($"{testLabel} — reading clashes…");

            // ── 1. Flatten the LIVE test into detached, re-insertable copies ──
            // Explode existing groups (ours or manual) so a re-run regroups from
            // scratch instead of nesting groups in groups. Each copy gets a fresh
            // empty GUID so re-inserting it can't duplicate a result GUID.
            int originalCount = CountResults(test.Children);
            var active = new List<ClashResult>();
            // Preserved unchanged — Resolved always, plus (in incremental mode) everything
            // already triaged: whole groups and triaged/assigned results.
            var passThrough = new List<SavedItem>();
            FlattenResults(test.Children, active, passThrough, topLevel: true);

            if (active.Count == 0) { result.Skipped += passThrough.Sum(CountItem); return; }

            // Sanity guard: if the copies lost their model item references we
            // can neither evaluate rules nor group — abort BEFORE any write.
            if (active.All(c => c.Item1 == null && c.Item2 == null))
            {
                result.Errors.Add($"'{test.DisplayName}': copied results have no item references — aborted, nothing written.");
                return;
            }

            // ── 2. Evaluate rules and edit the detached copies ─────────────
            result.Skipped += passThrough.Sum(CountItem);
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

                // Build the cached property accessor ONCE per clash (element keys + tree
                // path + property values are memoised across all rules for this clash).
                var getProp = MakeGetProp(clash);
                foreach (var rule in orderedRules)
                {
                    try
                    {
                        if (rule.Evaluate(getProp)) { matched = rule; break; }
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
                    ResolveRuleAssignment(clash, matched, out rAssignee, out rGroup);
                    EditClashCopy(clash, matched, rAssignee, rGroup, result);
                    result.RecordAssignment(matched.Name, rGroup, rAssignee, matched.Color);
                    anyEdits = true;
                }
                else if (TryAssignByKind(clash, result))
                {
                    anyEdits = true;
                }
                else if (!string.IsNullOrWhiteSpace(ruleSet.DefaultAssignee) &&
                         !string.Equals(ruleSet.DefaultAssignee, "Unassigned", StringComparison.OrdinalIgnoreCase))
                {
                    // Per-test default = the trade this clash test normally goes to (the
                    // learned clash-matrix responsibility). This IS the assignment for the
                    // test-driven model, not a fallback — so count it as assigned.
                    string assignee = ruleSet.DefaultAssignee;
                    clash.Description = $"[Group: {assignee}] [Assignee: {assignee}] Per-test default";
                    TrySetAssignedTo(clash, assignee, result);
                    result.Assigned++;
                    result.RecordAssignment("Per-test default", assignee, assignee, "#2563EB");
                    anyEdits = true;
                }
                else
                {
                    result.Unmatched++;
                    result.UnmatchedClashes.Add(clash.DisplayName);
                }
            }

            // ── 2b. Auto-approve within-tolerance clashes (assign-then-approve) ──
            int approved = ApproveWithinTolerance(active, testLabel, result);
            if (approved > 0)
            {
                anyEdits = true;
                Report($"{testLabel} — approved {approved:N0} within tolerance…");
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
            int rebuiltCount = groups.Sum(g => g.Children.Count) + ungrouped.Count + passThrough.Sum(CountItem);
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
            List<ClashResultGroup> groups, List<ClashResult> ungrouped, List<SavedItem> passThrough)
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
        private void FlattenResults(SavedItemCollection children,
            List<ClashResult> active, List<SavedItem> passThrough, bool topLevel)
        {
            foreach (SavedItem si in children)
            {
                if (si is ClashResultGroup grp)
                {
                    // Incremental mode: leave already-coordinated groups exactly as they
                    // are (preserve the whole top-level group, untouched).
                    if (OnlyNewClashes && topLevel)
                        passThrough.Add(CopyGroupDetached(grp));
                    else
                        FlattenResults(grp.Children, active, passThrough, false);
                }
                else if (si is ClashResult cr)
                {
                    var copy = (ClashResult)cr.CreateCopy();
                    copy.Guid = Guid.Empty;
                    // Always pass Resolved through. In incremental mode also pass through
                    // anything already triaged — only NEW + unassigned clashes get processed.
                    bool preserve = copy.Status == ClashResultStatus.Resolved
                        || (OnlyNewClashes && !(copy.Status == ClashResultStatus.New
                                                && string.IsNullOrWhiteSpace(SafeAssignee(copy))));
                    if (preserve) passThrough.Add(copy);
                    else active.Add(copy);
                }
            }
        }

        /// <summary>Deep-copies a group with fresh-GUID child results so it can be
        /// re-inserted unchanged (re-adding a result with its original GUID corrupts
        /// Clash Detective — quirk #0).</summary>
        private static ClashResultGroup CopyGroupDetached(ClashResultGroup grp)
        {
            var g = new ClashResultGroup { DisplayName = grp.DisplayName };
            foreach (SavedItem child in grp.Children)
            {
                if (child is ClashResultGroup sub) g.Children.Add(CopyGroupDetached(sub));
                else if (child is ClashResult cr)
                {
                    var cc = (ClashResult)cr.CreateCopy();
                    cc.Guid = Guid.Empty;
                    g.Children.Add(cc);
                }
            }
            return g;
        }

        /// <summary>Number of ClashResults a passed-through item represents (a result = 1,
        /// a group = its result count) — used by the no-data-loss guard.</summary>
        private static int CountItem(SavedItem si)
        {
            if (si is ClashResultGroup g) return CountResults(g.Children);
            return si is ClashResult ? 1 : 0;
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
        /// Resolves a matched rule's effective assignee + group for a specific clash —
        /// the literal rule fields (rules are always Named now).
        /// </summary>
        private void ResolveRuleAssignment(ClashResult clash, ClashRule rule,
            out string assignee, out string group)
        {
            assignee = rule.Assignee;
            group = rule.GroupName;
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
                    GroupByGrid(active, splitByTrade: false, out groups, out ungrouped);
                    return;
                case ClashGroupingMode.GridTrade:
                    GroupByGrid(active, splitByTrade: true, out groups, out ungrouped);
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

            // Group everything (even singletons) so the report leaves nothing ungrouped.
            foreach (var kv in buckets.OrderByDescending(b => b.Value.Count))
            {
                var grp = new ClashResultGroup { DisplayName = $"{kv.Key} ({kv.Value.Count})" };
                foreach (var c in kv.Value) grp.Children.Add(c);
                groups.Add(grp);
            }
        }

        private static string SafeAssignee(ClashResult cr)
        {
            try { return cr.AssignedTo?.DisplayName; } catch { return null; }
        }

        /// <summary>
        /// Groups by grid cell, named by the bare GRID INTERSECTION only (e.g. "H-22" —
        /// level stripped, no count). APPROVED clashes in a cell are split into their own
        /// sibling group suffixed " (1)" so a trade reviews active (to-do) vs approved
        /// (done) separately; the main group keeps the active clashes only. With
        /// <paramref name="splitByTrade"/> each cell is first split per trade. Colliding
        /// names get further " (2)", " (3)" suffixes.
        /// </summary>
        private void GroupByGrid(List<ClashResult> active, bool splitByTrade,
            out List<ClashResultGroup> groups, out List<ClashResult> ungrouped)
        {
            groups = new List<ClashResultGroup>();
            ungrouped = new List<ClashResult>();

            var buckets = new Dictionary<string, List<ClashResult>>(StringComparer.OrdinalIgnoreCase);
            var bucketGrid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in active)
            {
                string grid = null;
                try { grid = GridKey(c); } catch { }
                if (string.IsNullOrWhiteSpace(grid)) grid = "Off-grid";
                string key = splitByTrade ? grid + "" + (SafeAssignee(c) ?? "Unassigned") : grid;
                if (!buckets.TryGetValue(key, out var list)) { buckets[key] = list = new List<ClashResult>(); bucketGrid[key] = grid; }
                list.Add(c);
            }

            // Ensure unique display names; collisions get " (2)", " (3)"…
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, string> unique = name =>
            {
                if (used.Add(name)) return name;
                for (int n = 2; ; n++) { string cand = name + " (" + n + ")"; if (used.Add(cand)) return cand; }
            };

            foreach (var kv in buckets.OrderByDescending(b => b.Value.Count))
            {
                // Group everything — even a single clash gets its own grid group so the
                // report leaves nothing ungrouped.
                string gridName = GridName(bucketGrid[kv.Key]);

                var approved = kv.Value.Where(IsApprovedStatus).ToList();
                var actives = kv.Value.Where(c => !IsApprovedStatus(c)).ToList();

                if (approved.Count > 0 && actives.Count > 0)
                {
                    // Mixed cell → active in the main group, approved in a " (1)" sibling
                    // so the trade reviews to-do vs done separately.
                    groups.Add(MakeGroup(unique(gridName), actives));
                    groups.Add(MakeGroup(unique(gridName + " (1)"), approved));
                }
                else
                {
                    groups.Add(MakeGroup(unique(gridName), kv.Value));   // all one kind
                }
            }
        }

        private static bool IsApprovedStatus(ClashResult cr)
        {
            try { return cr.Status == ClashResultStatus.Approved; } catch { return false; }
        }

        private static ClashResultGroup MakeGroup(string name, List<ClashResult> members)
        {
            var grp = new ClashResultGroup { DisplayName = name };
            foreach (var c in members) grp.Children.Add(c);
            return grp;
        }

        /// <summary>Bare grid-intersection label: strips a trailing " : &lt;level&gt;" if present.</summary>
        private static string GridName(string gridLabel)
        {
            if (string.IsNullOrWhiteSpace(gridLabel)) return "Off-grid";
            int idx = gridLabel.IndexOf(" : ", StringComparison.Ordinal);
            return idx > 0 ? gridLabel.Substring(0, idx).Trim() : gridLabel.Trim();
        }

        // Ancestor names that aren't a meaningful element "service" (geometry leaf,
        // generic containers, model/file nodes). The first ancestor token that ISN'T
        // one of these is the element's service signature.
        private static readonly HashSet<string> ServiceNoise =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Solid", "Standard", "Default", "Internal", "<Not Shared>", "Default Site" };

        /// <summary>
        /// A clash's "service" signature = the specific element-type token of each
        /// side (nearest-leaf meaningful ancestor name), sorted + joined. Two clashes
        /// only proximity-group if these match, so like bundles with like (sprinkler
        /// with sprinkler, not sprinkler with extinguisher).
        /// </summary>
        private static string ServiceKey(ClashResult cr)
        {
            ModelItem i1 = null, i2 = null;
            try { i1 = cr.Item1; } catch { }
            try { i2 = cr.Item2; } catch { }
            string a = ServiceToken(i1);
            string b = ServiceToken(i2);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + "||" + b : b + "||" + a;
        }

        private static string ServiceToken(ModelItem item)
        {
            if (item == null) return "?";
            var cur = item;
            int depth = 0;
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
                if (s.IndexOf(" : ", StringComparison.Ordinal) >= 0) continue;
                // strip a trailing "[123]" / " 12345" instance id
                int br = s.IndexOf('[');
                if (br > 0) s = s.Substring(0, br).Trim();
                int e = s.Length;
                while (e > 0 && (char.IsDigit(s[e - 1]) || s[e - 1] == ' ')) e--;
                if (e >= 2 && e < s.Length) s = s.Substring(0, e).Trim();
                if (s.Length < 2) continue;
                if (ServiceNoise.Contains(s)) continue;
                return s;
            }
            return "?";
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

            // Service signature per clash — groups only ever contain ONE service, so a
            // bundle never mixes (e.g.) cold-water and waste-drain clashes that happen
            // to touch the same pipe. That keeps assignment correct even with
            // group-then-assign on.
            var svc = new string[n];
            for (int i = 0; i < n; i++) svc[i] = ServiceKey(active[i]);

            // ── Pass 1: link clashes sharing a model element (same service only) ────
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
                {
                    // Union members that share this element AND have the same service.
                    var firstBySvc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (int idx in list)
                    {
                        if (firstBySvc.TryGetValue(svc[idx], out int first)) union(first, idx);
                        else firstBySvc[svc[idx]] = idx;
                    }
                }

            // ── Pass 2: spatial proximity via a uniform grid hash ──────────
            // Bucket centres into cells of edge = threshold; a clash can only be
            // within threshold of clashes in its own + 26 neighbouring cells. This
            // turns the old O(n²) sweep into ~O(n) for large tests (e.g. 12k clashes).
            // Proximity only links clashes of the SAME SERVICE (element-type signature)
            // so a sprinkler run and an extinguisher 1 m apart don't chain into one
            // mega-group — they stay separate bundles.
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
                                    if (svc[i] != svc[j]) continue;   // same service only
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
            // Also fold in the Family / Type property values so fine (tree-path) rules
            // match the SAME family/type tokens the batch extractor keyed on, even when
            // the family isn't its own tree node.
            foreach (var pn in new[] { "Family", "Family Name", "Type", "Type Name" })
            {
                var v = GetPropertyValue(item, "", pn);
                if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
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
        public int Unmatched { get; set; }
        public int Skipped { get; set; }
        public int GroupsCreated { get; set; }
        public int Approved { get; set; }
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
                $"Unmatched: {Unmatched}",
                $"Skipped (resolved): {Skipped}",
                $"Auto-approved (within tolerance): {Approved}",
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
