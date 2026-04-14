using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    public class ClashProcessingService
    {
        public ProcessingResult LastResult { get; private set; }

        /// <summary>
        /// Process a specific clash test using its associated rule set
        /// </summary>
        public ProcessingResult ProcessSingleTest(string testName, TestRuleSet ruleSet)
        {
            var result = new ProcessingResult { TestName = testName };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            var orderedRules = ruleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

            // Find the specific clash test
            ClashTest targetTest = null;
            foreach (SavedItem item in clashPlugin.TestsData.Tests)
            {
                if (item is ClashTest ct && string.Equals(ct.DisplayName, testName, StringComparison.OrdinalIgnoreCase))
                {
                    targetTest = ct;
                    break;
                }
            }

            if (targetTest == null)
            {
                result.Errors.Add($"Clash test '{testName}' not found.");
                LastResult = result;
                return result;
            }

            result.TestsProcessed = 1;
            ProcessTest(targetTest, orderedRules, ruleSet, result, doc);

            LastResult = result;
            return result;
        }

        /// <summary>
        /// Process all clash tests using the project config
        /// </summary>
        public ProcessingResult ProcessAllTests(ProjectConfig config)
        {
            var result = new ProcessingResult { TestName = "All Tests" };
            var doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null) { result.Errors.Add("No active document."); return result; }

            var clashPlugin = doc.GetClash();
            if (clashPlugin == null) { result.Errors.Add("Clash plugin not available."); return result; }

            foreach (SavedItem item in clashPlugin.TestsData.Tests)
            {
                if (!(item is ClashTest ct)) continue;
                result.TestsProcessed++;

                var testRuleSet = config.GetOrCreateTestRuleSet(ct.DisplayName);
                var orderedRules = testRuleSet.Rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

                if (orderedRules.Count == 0)
                {
                    result.Errors.Add($"No rules for test '{ct.DisplayName}' — skipped.");
                    continue;
                }

                ProcessTest(ct, orderedRules, testRuleSet, result, doc);
            }

            LastResult = result;
            return result;
        }

        private void ProcessTest(ClashTest test, List<ClashRule> orderedRules, TestRuleSet ruleSet, ProcessingResult result, Document doc)
        {
            foreach (SavedItem resultItem in test.Children)
            {
                if (!(resultItem is ClashResult clashResult)) continue;
                if (clashResult.Status == ClashResultStatus.Resolved)
                {
                    result.Skipped++;
                    continue;
                }

                result.ClashesProcessed++;
                bool matched = false;

                foreach (var rule in orderedRules)
                {
                    try
                    {
                        if (EvaluateClash(clashResult, rule, doc))
                        {
                            ApplyRule(clashResult, rule, test);
                            result.Assigned++;
                            result.RecordAssignment(rule.Name, rule.GroupName, rule.Assignee);
                            matched = true;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Rule '{rule.Name}': {ex.Message}");
                    }
                }

                if (!matched)
                {
                    // Apply default assignment from test rule set
                    if (!string.IsNullOrWhiteSpace(ruleSet.DefaultAssignee))
                    {
                        clashResult.Description = $"[Unmatched] Default assignee: {ruleSet.DefaultAssignee}";
                    }
                    result.Unmatched++;
                    result.UnmatchedClashes.Add(clashResult.DisplayName);
                }
            }
        }

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

        private void ApplyRule(ClashResult clash, ClashRule rule, ClashTest test)
        {
            try
            {
                switch (rule.ClashStatus.ToLowerInvariant())
                {
                    case "active": clash.Status = ClashResultStatus.Active; break;
                    case "reviewed": clash.Status = ClashResultStatus.Reviewed; break;
                    case "approved": clash.Status = ClashResultStatus.Approved; break;
                    case "resolved": clash.Status = ClashResultStatus.Resolved; break;
                }
                clash.Description = $"[Group: {rule.GroupName}] [Assignee: {rule.Assignee}] Rule: {rule.Name}";
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to apply rule: {ex.Message}", ex);
            }
        }
    }

    public class ProcessingResult
    {
        public string TestName { get; set; } = "";
        public int TestsProcessed { get; set; }
        public int ClashesProcessed { get; set; }
        public int Assigned { get; set; }
        public int Unmatched { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> UnmatchedClashes { get; set; } = new List<string>();
        public Dictionary<string, AssignmentSummary> AssignmentsByRule { get; set; } = new Dictionary<string, AssignmentSummary>();

        public void RecordAssignment(string ruleName, string groupName, string assignee)
        {
            if (!AssignmentsByRule.ContainsKey(ruleName))
                AssignmentsByRule[ruleName] = new AssignmentSummary { RuleName = ruleName, GroupName = groupName, Assignee = assignee };
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
                $"Skipped (resolved): {Skipped}", ""
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
        public int Count { get; set; }
    }
}
