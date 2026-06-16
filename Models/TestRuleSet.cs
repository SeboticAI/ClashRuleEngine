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
    /// </summary>
    public enum ClashGroupingMode
    {
        None, SharedElement, Proximity, Grid, Level, ByAssignee, Hybrid
    }

    /// <summary>
    /// A single discipline in the system hierarchy. Carries both how to DETECT it
    /// on a model element (Keywords, matched against the element's source file name
    /// and key property values) and who OWNS clashes it is responsible for
    /// (Assignee / GroupName).
    /// </summary>
    [Serializable]
    public class DisciplineDefinition
    {
        public string Name { get; set; } = "";

        /// <summary>
        /// Keywords used to classify an element into this discipline. Matched
        /// case-insensitively against the element's source/model file name AND a
        /// handful of key properties (Workset, Category, Type, Family, Layer...).
        /// </summary>
        public List<string> Keywords { get; set; } = new List<string>();

        /// <summary>Who gets clashes this discipline is responsible for resolving.</summary>
        public string Assignee { get; set; } = "";

        /// <summary>
        /// How the clash is assigned when THIS discipline is the responsible (lower-
        /// precedence) side of a clash:
        ///   Named       — assign to <see cref="Assignee"/> (or this trade's name).
        ///   OwningTrade — assign to this discipline itself (same as Named for a discipline).
        ///   OtherTrade  — assign to the OPPOSITE clashing trade, in ANY test. This is
        ///                 how e.g. a "Hydraulic Drainage" sub-discipline can always be
        ///                 routed to the other service.
        /// </summary>
        public AssigneeMode AssigneeMode { get; set; } = AssigneeMode.Named;

        /// <summary>Optional Clash Detective group name (defaults to the discipline name).</summary>
        public string GroupName { get; set; } = "";

        /// <summary>Accent colour for the results breakdown.</summary>
        public string Color { get; set; } = "#7C3AED";

        /// <summary>Comma-separated view of <see cref="Keywords"/> for easy UI editing.</summary>
        [XmlIgnore]
        public string KeywordsCsv
        {
            get { return string.Join(", ", Keywords ?? new List<string>()); }
            set
            {
                Keywords = (value ?? "")
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
            }
        }
    }

    /// <summary>
    /// System hierarchy — defines which building systems take precedence.
    /// Higher position (lower index) = harder to move = gets right-of-way.
    /// The LOWER-precedence discipline in a clash is the one assigned responsibility
    /// (it must move). Based on standard Australian BIM coordination practice.
    /// </summary>
    [Serializable]
    public class SystemHierarchy
    {
        /// <summary>
        /// Legacy ordered name list. Retained for backward compatibility with
        /// older .clashre files; <see cref="Disciplines"/> is the live structure.
        /// </summary>
        public List<string> Systems { get; set; } = new List<string>();

        /// <summary>
        /// Ordered disciplines (index 0 = highest precedence). Order defines who
        /// yields in a clash. Seed defaults via <see cref="EnsureSeeded"/>.
        /// </summary>
        public List<DisciplineDefinition> Disciplines { get; set; } = new List<DisciplineDefinition>();

        /// <summary>
        /// Populate <see cref="Disciplines"/> with the standard default order if it
        /// is empty (new config, or an old file that predates discipline support).
        /// Migrates legacy <see cref="Systems"/> names where present.
        /// </summary>
        public void EnsureSeeded()
        {
            if (Disciplines != null && Disciplines.Count > 0) return;
            Disciplines = DefaultDisciplines();

            // Carry over any custom names from a legacy Systems list (keywords/
            // assignees still need filling in via the Settings editor).
            if (Systems != null && Systems.Count > 0)
            {
                foreach (var s in Systems)
                    if (!Disciplines.Any(d => string.Equals(d.Name, s, StringComparison.OrdinalIgnoreCase)))
                        Disciplines.Add(new DisciplineDefinition { Name = s });
            }
        }

        public static List<DisciplineDefinition> DefaultDisciplines()
        {
            return new List<DisciplineDefinition>
            {
                Disc("Structure",          "#6B7280", "STR", "STRUCT", "SC", "structural", "beam", "column", "slab", "footing"),
                Disc("Architecture",       "#92400E", "ARCH", "AR", "architectural", "wall", "door", "window", "ceiling", "floor"),
                Disc("Mechanical (HVAC)",  "#2563EB", "MECH", "HVAC", "MC", "duct", "mechanical", "vav", "ahu", "diffuser"),
                Disc("Hydraulic (Plumbing)","#0891B2","HYD", "HC", "PLUMB", "plumbing", "sanitary", "retic", "pipe", "water", "drainage"),
                Disc("Fire Services",      "#DC2626", "FIRE", "FC", "FP", "sprinkler", "hydrant", "fire"),
                Disc("Electrical",         "#CA8A04", "ELEC", "EC", "electrical", "cable", "conduit", "tray", "lighting", "switchboard"),
                Disc("Communications",     "#7C3AED", "COMMS", "COMM", "data", "ICT", "telecom", "comms"),
                Disc("Landscape",          "#16A34A", "LAND", "LSC", "landscape", "soft", "planting"),
            };
        }

        private static DisciplineDefinition Disc(string name, string color, params string[] keywords)
        {
            return new DisciplineDefinition { Name = name, Color = color, Keywords = keywords.ToList() };
        }

        /// <summary>
        /// Index of a discipline by name (lower = higher precedence).
        /// Returns int.MaxValue if not found.
        /// </summary>
        public int GetPriority(string disciplineName)
        {
            for (int i = 0; i < Disciplines.Count; i++)
                if (string.Equals(Disciplines[i].Name, disciplineName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return int.MaxValue;
        }

        /// <summary>
        /// Of two clashing disciplines, the one responsible for resolving (moving)
        /// is the LOWER-precedence one (higher index). Returns null only if neither
        /// is known. Ties (same discipline) return that discipline.
        /// </summary>
        public DisciplineDefinition GetResponsible(DisciplineDefinition a, DisciplineDefinition b)
        {
            if (a == null && b == null) return null;
            if (a == null) return b;
            if (b == null) return a;
            int ia = Disciplines.IndexOf(a);
            int ib = Disciplines.IndexOf(b);
            return ib >= ia ? b : a;
        }
    }

    /// <summary>
    /// The entire project configuration — contains per-test rule sets,
    /// system hierarchy, and shared settings.
    /// </summary>
    [Serializable]
    [XmlRoot("ClashRuleEngineConfig")]
    public class ProjectConfig
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public DateTime LastModified { get; set; } = DateTime.Now;

        /// <summary>System hierarchy for the project</summary>
        public SystemHierarchy Hierarchy { get; set; } = new SystemHierarchy();

        /// <summary>
        /// When true, clashes not caught by any rule are auto-assigned using the
        /// discipline hierarchy (lower discipline is responsible). Rules always win
        /// when they match — the hierarchy only fills the gaps.
        /// </summary>
        public bool UseHierarchyFallback { get; set; } = true;

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

        /// <summary>Get all unique assignees from rules, discipline owners, and the shared list</summary>
        public List<string> GetAllAssignees()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.Assignee);
            var fromDisciplines = (Hierarchy?.Disciplines ?? new List<DisciplineDefinition>())
                .Select(d => d.Assignee);
            return Assignees
                .Concat(fromRules)
                .Concat(fromDisciplines)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a).ToList();
        }

        /// <summary>Get all unique group names from rules, disciplines, and the shared list</summary>
        public List<string> GetAllGroupNames()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.GroupName);
            var fromDisciplines = (Hierarchy?.Disciplines ?? new List<DisciplineDefinition>())
                .Select(d => d.GroupName);
            return GroupNames
                .Concat(fromRules)
                .Concat(fromDisciplines)
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
                    var cfg = (ProjectConfig)serializer.Deserialize(reader);
                    cfg.Hierarchy?.EnsureSeeded();   // fill defaults for pre-discipline files
                    return cfg;
                }
            }
            catch { return new ProjectConfig(); }
        }
    }
}
