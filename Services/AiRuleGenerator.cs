using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Turns plain-English coordination instructions + example clashes into
    /// deterministic <see cref="ClashRule"/> objects, using Claude as the author.
    /// The engine still executes the rules — Claude only writes them — so the
    /// result is auditable, free to re-run, and editable.
    /// </summary>
    public static class AiRuleGenerator
    {
        private static readonly string[] CoreCategories =
        {
            "Item", "Element", "Identity Data", "Dimensions", "Mechanical",
            "Mechanical - Flow", "Constraints", "Phasing", "Other", "Revit Type"
        };

        // ── Prompt construction ────────────────────────────────────────────────

        public static string BuildSystemPrompt(ProjectConfig config)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a BIM clash-coordination expert. Convert the user's plain-English coordination");
            sb.AppendLine("rules into a precise, ordered list of machine rules for one clash test.");
            sb.AppendLine();
            sb.AppendLine("How the rule engine works (match what it can actually do):");
            sb.AppendLine("- Each clash has two items: Item A and Item B, each belonging to a trade/discipline.");
            sb.AppendLine("- Rules are evaluated top-to-bottom; the FIRST rule whose conditions match wins.");
            sb.AppendLine("  So put SPECIFIC exceptions BEFORE general rules.");
            sb.AppendLine("- A rule's conditions test item properties: target (ItemA/ItemB/Either) + category + property + operator + value.");
            sb.AppendLine("- Conditions combine with logic AND or OR.");
            sb.AppendLine("- The assignee is ALWAYS one of the two trades in the clash. Express it relatively:");
            sb.AppendLine("    assignTo = \"OwningTrade\" (the trade of the item the rule is about, given by subjectItem),");
            sb.AppendLine("    assignTo = \"OtherTrade\" (the opposite item's trade),");
            sb.AppendLine("    assignTo = \"Named\" (only if the user names a specific person/company in 'assignee').");
            sb.AppendLine("- subjectItem is ItemA or ItemB — the item the rule is primarily about (anchors Owning/Other).");
            sb.AppendLine();
            sb.AppendLine("Project disciplines (precedence order; lower in the list = yields/responsible):");
            var discs = config?.Hierarchy?.Disciplines ?? new List<DisciplineDefinition>();
            for (int i = 0; i < discs.Count; i++)
            {
                var kw = discs[i].Keywords != null ? string.Join(", ", discs[i].Keywords) : "";
                sb.AppendLine($"  {i + 1}. {discs[i].Name}  (keywords: {kw})");
            }
            sb.AppendLine();
            sb.AppendLine("Output STRICT JSON only — no prose, no markdown fences. Shape:");
            sb.AppendLine(@"{
  ""rules"": [
    {
      ""name"": ""short rule name"",
      ""description"": ""why this rule exists"",
      ""logic"": ""AND"" | ""OR"",
      ""conditions"": [
        { ""target"": ""ItemA""|""ItemB""|""Either"", ""category"": ""Item"", ""property"": ""Workset"", ""operator"": ""Contains"", ""value"": ""HYD"" }
      ],
      ""assignTo"": ""OwningTrade""|""OtherTrade""|""Named"",
      ""subjectItem"": ""ItemA""|""ItemB"",
      ""assignee"": ""(only when assignTo=Named)"",
      ""group"": ""(optional group name)"",
      ""status"": ""Active""|""Reviewed""|""Approved""|""Resolved""
    }
  ]
}");
            sb.AppendLine();
            sb.AppendLine("Valid operators: Equals, NotEquals, Contains, DoesNotContain, StartsWith,");
            sb.AppendLine("GreaterThan, LessThan, GreaterThanOrEqual, LessThanOrEqual.");
            sb.AppendLine();
            sb.AppendLine("ELEMENT TYPE lives in the tree path (Category / Family / Type), shown as 'path'");
            sb.AppendLine("on each example item — NOT in a property. To match it, use a Tree Path condition:");
            sb.AppendLine(@"  { ""target"":""ItemB"", ""category"":""Tree"", ""property"":""Path"", ""operator"":""Contains"", ""value"":""Conduit"" }");
            sb.AppendLine("e.g. value \"Cable Tray\" for cable trays, \"Clearance Zone\" for clearance zones,");
            sb.AppendLine("\"Pipe\" for pipework. PREFER Tree Path for element-type rules; use Material/");
            sb.AppendLine("properties only for finishes or sizes.");
            sb.AppendLine("Dimension values are in METRES (50mm = 0.05). Use 'Outside Diameter' for pipe sizes, not 'Size'.");
            sb.AppendLine("Prefer few robust rules. Order specific-before-general. Use the example clashes to ground property names/values.");
            return sb.ToString();
        }

        public static string BuildUserPrompt(string testName, string instructions,
            IEnumerable<ClashResultInfo> clashes, int maxExamples = 40)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Clash test: {testName}");
            sb.AppendLine();
            sb.AppendLine("My coordination rules (plain English):");
            sb.AppendLine(string.IsNullOrWhiteSpace(instructions) ? "(none given — infer from the examples)" : instructions.Trim());
            sb.AppendLine();
            sb.AppendLine("Example clashes from this test (with any assignments I've already made). Use these to");
            sb.AppendLine("learn the property names/values and to match my existing decisions:");
            sb.AppendLine(BuildExamplesJson(clashes, maxExamples));
            sb.AppendLine();
            sb.AppendLine("Return the rules as JSON per the schema. No prose.");
            return sb.ToString();
        }

        /// <summary>Compact JSON sample of clashes — assigned ones first (they're the teaching examples).</summary>
        public static string BuildExamplesJson(IEnumerable<ClashResultInfo> clashes, int maxExamples)
        {
            var list = (clashes ?? Enumerable.Empty<ClashResultInfo>())
                .Where(c => c?.SourceResult != null)
                .ToList();

            // Assigned examples carry the signal; take them first, then fill with unassigned.
            var ordered = list.Where(c => !string.IsNullOrWhiteSpace(GetAssignee(c.SourceResult)))
                              .Concat(list.Where(c => string.IsNullOrWhiteSpace(GetAssignee(c.SourceResult))))
                              .Take(maxExamples)
                              .ToList();

            var examples = new List<object>();
            foreach (var info in ordered)
            {
                var cr = info.SourceResult;
                examples.Add(new Dictionary<string, object>
                {
                    ["name"] = SafeName(cr),
                    ["status"] = SafeStatus(cr),
                    ["myAssignee"] = GetAssignee(cr) ?? "",
                    ["itemA"] = ItemProps(SafeItem(cr, true)),
                    ["itemB"] = ItemProps(SafeItem(cr, false)),
                });
            }
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Serialize(new Dictionary<string, object> { ["examples"] = examples });
        }

        private static Dictionary<string, object> ItemProps(ModelItem item)
        {
            var result = new Dictionary<string, object>();
            if (item == null) return result;
            try { result["name"] = item.DisplayName ?? ""; } catch { }
            try { result["source"] = RootName(item); } catch { }

            var props = new Dictionary<string, object>();
            try
            {
                foreach (PropertyCategory cat in item.PropertyCategories)
                {
                    if (!CoreCategories.Contains(cat.DisplayName, StringComparer.OrdinalIgnoreCase)) continue;
                    var bag = new Dictionary<string, object>();
                    foreach (DataProperty p in cat.Properties)
                    {
                        string v = ValueToString(p);
                        if (!string.IsNullOrEmpty(v)) bag[p.DisplayName] = v;
                    }
                    if (bag.Count > 0) props[cat.DisplayName] = bag;
                }
            }
            catch { }
            result["properties"] = props;
            return result;
        }

        // ── Response parsing ───────────────────────────────────────────────────

        public static List<ClashRule> ParseRules(string responseText)
        {
            var rules = new List<ClashRule>();
            if (string.IsNullOrWhiteSpace(responseText)) return rules;

            string json = ExtractJsonObject(responseText);
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> root;
            try { root = ser.Deserialize<Dictionary<string, object>>(json); }
            catch { throw new ClaudeApiException("Could not parse the rules Claude returned (invalid JSON)."); }

            object rulesObj;
            if (root == null || !root.TryGetValue("rules", out rulesObj) || !(rulesObj is System.Collections.IEnumerable))
                throw new ClaudeApiException("Claude's response did not contain a 'rules' array.");

            foreach (var item in (System.Collections.IEnumerable)rulesObj)
            {
                var r = item as Dictionary<string, object>;
                if (r == null) continue;
                rules.Add(MapRule(r));
            }
            return rules;
        }

        private static ClashRule MapRule(Dictionary<string, object> r)
        {
            var rule = new ClashRule
            {
                Name = Str(r, "name", "AI Rule"),
                Description = Str(r, "description", ""),
                ConditionLogic = Str(r, "logic", "AND").Equals("OR", StringComparison.OrdinalIgnoreCase)
                    ? LogicOperator.Or : LogicOperator.And,
                AssigneeMode = ParseAssigneeMode(Str(r, "assignTo", "OwningTrade")),
                SubjectItem = ParseTarget(Str(r, "subjectItem", "ItemA"), ClashItemTarget.Item1),
                Assignee = Str(r, "assignee", ""),
                GroupName = Str(r, "group", ""),
                ClashStatus = NormalizeStatus(Str(r, "status", "Active")),
                Color = "#7C3AED",   // mark AI-authored rules
            };

            object condsObj;
            if (r.TryGetValue("conditions", out condsObj) && condsObj is System.Collections.IEnumerable)
            {
                foreach (var c in (System.Collections.IEnumerable)condsObj)
                {
                    var cd = c as Dictionary<string, object>;
                    if (cd == null) continue;
                    rule.Conditions.Add(new RuleCondition
                    {
                        Target = ParseTarget(Str(cd, "target", "Either"), ClashItemTarget.Either),
                        PropertyCategory = Str(cd, "category", ""),
                        PropertyName = Str(cd, "property", ""),
                        Operator = ParseOperator(Str(cd, "operator", "Equals")),
                        Value = Str(cd, "value", ""),
                    });
                }
            }
            return rule;
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private static AssigneeMode ParseAssigneeMode(string s)
        {
            s = (s ?? "").Replace(" ", "");
            if (s.IndexOf("Owning", StringComparison.OrdinalIgnoreCase) >= 0) return AssigneeMode.OwningTrade;
            if (s.IndexOf("Other", StringComparison.OrdinalIgnoreCase) >= 0) return AssigneeMode.OtherTrade;
            return AssigneeMode.Named;
        }

        private static ClashItemTarget ParseTarget(string s, ClashItemTarget fallback)
        {
            s = (s ?? "").Replace(" ", "");
            if (s.EndsWith("A", StringComparison.OrdinalIgnoreCase) || s.EndsWith("1")) return ClashItemTarget.Item1;
            if (s.EndsWith("B", StringComparison.OrdinalIgnoreCase) || s.EndsWith("2")) return ClashItemTarget.Item2;
            if (s.Equals("Either", StringComparison.OrdinalIgnoreCase)) return ClashItemTarget.Either;
            return fallback;
        }

        private static ConditionOperator ParseOperator(string s)
        {
            ConditionOperator op;
            if (Enum.TryParse(s, true, out op)) return op;
            switch ((s ?? "").Trim())
            {
                case "=": return ConditionOperator.Equals;
                case "!=": case "≠": return ConditionOperator.NotEquals;
                case ">": return ConditionOperator.GreaterThan;
                case "<": return ConditionOperator.LessThan;
                case ">=": case "≥": return ConditionOperator.GreaterThanOrEqual;
                case "<=": case "≤": return ConditionOperator.LessThanOrEqual;
                default: return ConditionOperator.Contains;
            }
        }

        private static string NormalizeStatus(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "reviewed": return "Reviewed";
                case "approved": return "Approved";
                case "resolved": return "Resolved";
                default: return "Active";
            }
        }

        private static string ExtractJsonObject(string text)
        {
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            return (start >= 0 && end > start) ? text.Substring(start, end - start + 1) : text;
        }

        private static string Str(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            {
                string s = Convert.ToString(v);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return fallback;
        }

        private static string GetAssignee(ClashResult cr)
        {
            try
            {
                var p = cr.GetType().GetProperty("AssignedTo");
                if (p != null)
                {
                    var v = p.GetValue(cr);
                    if (v != null)
                    {
                        string s = v.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && s != v.GetType().FullName) return s;
                    }
                }
            }
            catch { }
            // Fall back to our own "[Assignee: X]" tag in the description.
            try
            {
                string d = cr.Description ?? "";
                int i = d.IndexOf("[Assignee:", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    int e = d.IndexOf(']', i);
                    if (e > i) return d.Substring(i + 10, e - i - 10).Trim();
                }
            }
            catch { }
            return null;
        }

        private static string SafeName(ClashResult cr) { try { return cr.DisplayName ?? ""; } catch { return ""; } }
        private static string SafeStatus(ClashResult cr) { try { return cr.Status.ToString(); } catch { return ""; } }
        private static ModelItem SafeItem(ClashResult cr, bool first)
        {
            try { return first ? cr.Item1 : cr.Item2; } catch { return null; }
        }

        private static string RootName(ModelItem item)
        {
            var cur = item; ModelItem root = item; int depth = 0;
            while (cur != null && depth < 12) { root = cur; cur = cur.Parent; depth++; }
            try { return root != null ? (root.DisplayName ?? "") : ""; } catch { return ""; }
        }

        private static string ValueToString(DataProperty prop)
        {
            try
            {
                var val = prop.Value;
                if (val == null) return null;
                if (val.IsDisplayString) return val.ToDisplayString();
                if (val.IsNamedConstant) return val.ToNamedConstant().DisplayName;
                if (val.IsDouble) return val.ToDouble().ToString(CultureInfo.InvariantCulture);
                if (val.IsInt32) return val.ToInt32().ToString(CultureInfo.InvariantCulture);
                if (val.IsBoolean) return val.ToBoolean().ToString();
                if (val.IsDoubleLength) return val.ToDoubleLength().ToString(CultureInfo.InvariantCulture);
                return val.ToString();
            }
            catch { return null; }
        }
    }
}
