using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace ClashRuleEngine.Models
{
    /// <summary>
    /// Rules for a single clash test (e.g., "MC vs EC").
    /// Each test has its own independent rule hierarchy.
    /// </summary>
    [Serializable]
    public class TestRuleSet
    {
        /// <summary>Name of the clash test this applies to</summary>
        public string TestName { get; set; } = string.Empty;

        /// <summary>Which discipline is responsible by default for this test pair</summary>
        public string DefaultResponsibleDiscipline { get; set; } = string.Empty;

        /// <summary>Default assignee for unmatched clashes in this test</summary>
        public string DefaultAssignee { get; set; } = "Unassigned";

        /// <summary>Ordered rules — position in list = priority</summary>
        public List<ClashRule> Rules { get; set; } = new List<ClashRule>();

        public void ReindexPriorities()
        {
            for (int i = 0; i < Rules.Count; i++)
                Rules[i].Priority = i;
        }

        public void MoveUp(ClashRule rule)
        {
            int idx = Rules.IndexOf(rule);
            if (idx > 0)
            {
                Rules.RemoveAt(idx);
                Rules.Insert(idx - 1, rule);
                ReindexPriorities();
            }
        }

        public void MoveDown(ClashRule rule)
        {
            int idx = Rules.IndexOf(rule);
            if (idx >= 0 && idx < Rules.Count - 1)
            {
                Rules.RemoveAt(idx);
                Rules.Insert(idx + 1, rule);
                ReindexPriorities();
            }
        }
    }

    /// <summary>
    /// How active clashes are bundled into Clash Detective groups when rules run.
    ///   None          — no grouping; every clash stands alone.
    ///   SharedElement — link clashes that touch the same model element.
    ///   Proximity     — link clashes whose centres are within the threshold.
    ///   Grid          — bucket by nearest grid intersection (e.g. G-21).
    ///   Level         — bucket by nearest grid level.
    ///   ByAssignee    — bucket by the assignee each clash resolved to.
    ///   Hybrid        — shared element OR proximity (the original default).
    ///   GridTrade     — bucket by grid cell AND assignee: one trade's clashes in
    ///                   one grid location per bundle (best with per-test rules).
    /// </summary>
    public enum ClashGroupingMode
    {
        None, SharedElement, Proximity, Grid, Level, ByAssignee, Hybrid, GridTrade
    }

    /// <summary>
    /// The entire project configuration — contains per-test rule sets
    /// and shared settings.
    /// </summary>
    [Serializable]
    [XmlRoot("ClashRuleEngineConfig")]
    public class ProjectConfig
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>How active clashes are clustered into Clash Detective groups.
        /// Default None — grouping is opt-in, chosen in the addin's Hierarchy tab.</summary>
        public ClashGroupingMode GroupingMode { get; set; } = ClashGroupingMode.None;

        /// <summary>Proximity threshold (metres) for proximity/hybrid grouping.</summary>
        public double ProximityThreshold { get; set; } = 1.0;

        /// <summary>
        /// When true, after clustering each group is given ONE assignee (the most
        /// common among its members) so the whole bundle goes to a single trade —
        /// the "group first, then assign" workflow.
        /// </summary>
        public bool AssignByGroup { get; set; } = false;

        /// <summary>
        /// Global element-KIND rules (Fire Flex, Hyd Drainage >75mm, Mech Clearance…),
        /// derived from the coordinated NWDs. Ordered, first-match-wins, applied to
        /// EVERY test after per-test rules and before the trade hierarchy. This is the
        /// element-kind assignment layer.
        /// </summary>
        public List<KindRule> KindRules { get; set; } = new List<KindRule>();

        /// <summary>When true, the global KindRules are evaluated during a run.</summary>
        public bool UseKindRules { get; set; } = true;

        /// <summary>Incremental mode: a run only assigns NEW + unassigned clashes and leaves
        /// every already-triaged clash (Active/Approved/assigned) and existing group as-is.</summary>
        public bool OnlyAssignNewClashes { get; set; } = false;

        /// <summary>The auto-approve policy — after assignment, sign off within-tolerance
        /// soft clashes between negotiable trades (never structure). Off by default.</summary>
        public ApprovePolicy ApprovePolicy { get; set; } = new ApprovePolicy();

        /// <summary>Per-test rule sets — one entry per clash test</summary>
        public List<TestRuleSet> TestRuleSets { get; set; } = new List<TestRuleSet>();

        /// <summary>Shared list of assignees across all tests</summary>
        public List<string> Assignees { get; set; } = new List<string>();

        /// <summary>Shared list of group names across all tests</summary>
        public List<string> GroupNames { get; set; } = new List<string>();

        /// <summary>Claude API key (stored locally, never shared)</summary>
        public string ApiKey { get; set; } = string.Empty;

        /// <summary>Get or create the rule set for a specific clash test</summary>
        public TestRuleSet GetOrCreateTestRuleSet(string testName)
        {
            var existing = TestRuleSets.FirstOrDefault(
                t => string.Equals(t.TestName, testName, StringComparison.OrdinalIgnoreCase));

            if (existing != null) return existing;

            var newSet = new TestRuleSet { TestName = testName };
            TestRuleSets.Add(newSet);
            return newSet;
        }

        /// <summary>Get all unique assignees from rules and the shared list</summary>
        public List<string> GetAllAssignees()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.Assignee);
            return Assignees
                .Concat(fromRules)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a).ToList();
        }

        /// <summary>Get all unique group names from rules and the shared list</summary>
        public List<string> GetAllGroupNames()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.GroupName);
            return GroupNames
                .Concat(fromRules)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g).ToList();
        }

        public string ToXml()
        {
            LastModified = DateTime.Now;
            var serializer = new XmlSerializer(typeof(ProjectConfig));
            using (var writer = new StringWriter())
            {
                serializer.Serialize(writer, this);
                return writer.ToString();
            }
        }

        public static ProjectConfig FromXml(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return new ProjectConfig();
            try
            {
                var serializer = new XmlSerializer(typeof(ProjectConfig));
                using (var reader = new StringReader(xml))
                {
                    return (ProjectConfig)serializer.Deserialize(reader);
                }
            }
            catch { return new ProjectConfig(); }
        }
    }
}
