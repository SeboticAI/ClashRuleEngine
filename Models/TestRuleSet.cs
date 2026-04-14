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
    /// System hierarchy — defines which building systems take precedence.
    /// Higher position = harder to move = gets right-of-way.
    /// Based on standard BIM coordination practice.
    /// </summary>
    [Serializable]
    public class SystemHierarchy
    {
        /// <summary>
        /// Ordered list from highest priority (hardest to move) to lowest.
        /// Default follows Australian BIM coordination standards.
        /// </summary>
        public List<string> Systems { get; set; } = new List<string>
        {
            "Structure",
            "Architecture",
            "Mechanical (HVAC)",
            "Hydraulic (Plumbing)",
            "Fire Services",
            "Electrical",
            "Communications",
            "Landscape"
        };

        /// <summary>
        /// Get the priority index for a system (lower = higher priority).
        /// Returns int.MaxValue if not found.
        /// </summary>
        public int GetPriority(string systemName)
        {
            for (int i = 0; i < Systems.Count; i++)
            {
                if (Systems[i].IndexOf(systemName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    systemName.IndexOf(Systems[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return int.MaxValue;
        }

        /// <summary>
        /// Given two systems in a clash, which discipline should yield?
        /// Returns the lower-priority system name (the one that should move).
        /// </summary>
        public string GetResponsibleDiscipline(string systemA, string systemB)
        {
            int pA = GetPriority(systemA);
            int pB = GetPriority(systemB);
            return pA <= pB ? systemB : systemA;
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

        /// <summary>Get all unique assignees from all test rule sets</summary>
        public List<string> GetAllAssignees()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.Assignee)
                .Where(a => !string.IsNullOrWhiteSpace(a));
            return Assignees.Union(fromRules, StringComparer.OrdinalIgnoreCase)
                           .OrderBy(a => a).ToList();
        }

        /// <summary>Get all unique group names from all test rule sets</summary>
        public List<string> GetAllGroupNames()
        {
            var fromRules = TestRuleSets
                .SelectMany(t => t.Rules)
                .Select(r => r.GroupName)
                .Where(g => !string.IsNullOrWhiteSpace(g));
            return GroupNames.Union(fromRules, StringComparer.OrdinalIgnoreCase)
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
                    return (ProjectConfig)serializer.Deserialize(reader);
            }
            catch { return new ProjectConfig(); }
        }
    }
}
