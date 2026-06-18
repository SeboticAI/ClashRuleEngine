using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using ClashRuleEngine.Models;

namespace ClashRuleEngine.Services
{
    /// <summary>
    /// Parses the element-kind rule list (the "summary response" derived from the
    /// batch JSONL by Claude) into KindRules + the trade taxonomy used to resolve
    /// owner/other. Schema "clashre-kind-rules/1":
    ///
    /// {
    ///   "schema":"clashre-kind-rules/1",
    ///   "trades":[ {"name":"Fire","keywords":["CON_FIRE","RVT-FIRE-"]}, ... ],
    ///   "rules":[
    ///     {"name":"Hyd Drainage &gt; 75mm","side":"either",
    ///      "keywords":["Waste Drain","Sanitary","Vent"],"minDiaMm":75,"assign":"other","group":"Hyd Drainage"},
    ///     {"name":"Fire Flex","keywords":["flex"],"assign":"Fire"},
    ///     {"name":"Mech Clearance","keywords":["Clearance Zone"],"assign":"Mech"}
    ///   ]
    /// }
    ///
    /// "assign" = "owner" | "other" | "&lt;trade name&gt;".
    /// </summary>
    public static class KindRuleImport
    {
        public sealed class Result
        {
            public List<KindRule> Rules { get; set; } = new List<KindRule>();
            /// <summary>Optional approve policy from the file's "approve" block (null if absent).</summary>
            public ApprovePolicy Approve { get; set; }
            /// <summary>Per-test default assignee (the learned clash-matrix responsibility).</summary>
            public List<TestDefault> TestDefaults { get; set; } = new List<TestDefault>();
            /// <summary>Per-test element-PAIR rules (category A vs category B → trade).</summary>
            public List<TestPairRule> TestRules { get; set; } = new List<TestPairRule>();
        }

        public sealed class TestDefault
        {
            public string Test { get; set; } = "";
            public string Assignee { get; set; } = "";
        }

        /// <summary>A learned element-pair rule scoped to one clash test: when an element
        /// whose category contains A clashes one whose category contains B, assign to a trade.</summary>
        public sealed class TestPairRule
        {
            public string Test { get; set; } = "";
            public ClashRule Rule { get; set; }
        }

        public static bool LooksLikeKindRules(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.TrimStart();
            return t.StartsWith("{") &&
                   (text.IndexOf("clashre-kind-rules", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (text.IndexOf("\"rules\"", StringComparison.OrdinalIgnoreCase) >= 0 &&
                     text.IndexOf("\"assign\"", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static Result Parse(string json)
        {
            var res = new Result();
            if (string.IsNullOrWhiteSpace(json)) return res;

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = ser.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Not a JSON object.");

            if (root.TryGetValue("rules", out var rulesObj) && rulesObj is object[] rules)
            {
                foreach (var r in rules.OfType<Dictionary<string, object>>())
                {
                    var kr = new KindRule
                    {
                        Name = Str(r, "name") ?? "",
                        Keywords = StrList(r, "keywords").Concat(StrList(r, "kind"))
                                    .Concat(StrList(r, "system")).Concat(StrList(r, "category"))
                                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
                        MinDiameterMm = Dbl(r, "minDiaMm"),
                        MaxDiameterMm = Dbl(r, "maxDiaMm"),
                        Side = ParseSide(Str(r, "side")),
                        GroupName = Str(r, "group") ?? ""
                    };

                    string assign = (Str(r, "assign") ?? "owner").Trim();
                    if (string.Equals(assign, "owner", StringComparison.OrdinalIgnoreCase)) kr.Assign = KindAssign.Owner;
                    else if (string.Equals(assign, "other", StringComparison.OrdinalIgnoreCase)) kr.Assign = KindAssign.Other;
                    else { kr.Assign = KindAssign.Named; kr.Assignee = assign; }

                    if (kr.Keywords.Count > 0 || kr.MinDiameterMm > 0 || kr.MaxDiameterMm > 0)
                        res.Rules.Add(kr);
                }
            }

            if (root.TryGetValue("approve", out var apprObj) && apprObj is Dictionary<string, object> a)
            {
                var pol = new ApprovePolicy { Enabled = true };
                if (a.TryGetValue("enabled", out var en) && en is bool enb) pol.Enabled = enb;
                if (a.ContainsKey("minGapMm")) pol.MinGapMm = Dbl(a, "minGapMm");
                if (a.ContainsKey("maxGapMm")) pol.MaxGapMm = Dbl(a, "maxGapMm");
                if (a.ContainsKey("requireAssignee") && a["requireAssignee"] is bool rq) pol.RequireAssignee = rq;
                if (a.ContainsKey("useTestNameGuard") && a["useTestNameGuard"] is bool tg) pol.UseTestNameGuard = tg;
                var prot = StrList(a, "protectedTrades");
                if (prot.Count > 0) pol.ProtectedTrades = prot;

                if (a.TryGetValue("pairFloors", out var pfObj) && pfObj is object[] pfs)
                {
                    foreach (var pf in pfs.OfType<Dictionary<string, object>>())
                    {
                        string pa = Str(pf, "a"), pb = Str(pf, "b");
                        if (string.IsNullOrWhiteSpace(pa) || string.IsNullOrWhiteSpace(pb)) continue;
                        pol.PairFloors.Add(new PairFloor { A = pa, B = pb, MinGapMm = Dbl(pf, "minGapMm") });
                    }
                }

                // Always-approve (gap-independent): assignees + element kinds.
                var appAsg = StrList(a, "approveAssignees");
                if (appAsg.Count > 0) pol.ApproveAssignees = appAsg;
                if (a.TryGetValue("approveKinds", out var akObj) && akObj is object[] aks)
                {
                    foreach (var ak in aks.OfType<Dictionary<string, object>>())
                    {
                        var kws = StrList(ak, "keywords");
                        if (kws.Count == 0) continue;
                        pol.ApproveKinds.Add(new ApproveKind { Name = Str(ak, "name") ?? "", Keywords = kws });
                    }
                }
                res.Approve = pol;
            }

            if (root.TryGetValue("tests", out var testsObj) && testsObj is object[] testArr)
            {
                foreach (var td in testArr.OfType<Dictionary<string, object>>())
                {
                    string test = Str(td, "test");
                    string assign = Str(td, "assign") ?? Str(td, "assignee");
                    if (string.IsNullOrWhiteSpace(test) || string.IsNullOrWhiteSpace(assign)) continue;
                    res.TestDefaults.Add(new TestDefault { Test = test.Trim(), Assignee = assign.Trim() });
                }
            }

            if (root.TryGetValue("testRules", out var trObj) && trObj is object[] trArr)
            {
                foreach (var tr in trArr.OfType<Dictionary<string, object>>())
                {
                    string test = Str(tr, "test");
                    string catA = Str(tr, "a"); string catB = Str(tr, "b");
                    string assign = Str(tr, "assign") ?? Str(tr, "assignee");
                    if (string.IsNullOrWhiteSpace(test) || string.IsNullOrWhiteSpace(assign)) continue;
                    if (string.IsNullOrWhiteSpace(catA) && string.IsNullOrWhiteSpace(catB)) continue;
                    // match = "tree" → fine rule (matches the family/leaf in the tree path);
                    // anything else → category match (the fallback tier).
                    bool tree = string.Equals(Str(tr, "match"), "tree", StringComparison.OrdinalIgnoreCase);
                    res.TestRules.Add(new TestPairRule { Test = test.Trim(), Rule = BuildPairRule(Str(tr, "name"), catA, catB, assign, Str(tr, "group"), tree) });
                }
            }

            return res;
        }

        /// <summary>Builds a per-test element-pair ClashRule: AND of two "Category contains"
        /// conditions. Distinct A/B use Either-target (unordered pair match); when A==B both
        /// items must match (targeted Item1 + Item2) so it's truly that-vs-that.</summary>
        private static ClashRule BuildPairRule(string name, string a, string b, string assign, string group, bool tree = false)
        {
            var conds = new List<RuleCondition>();
            bool same = !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
            if (same)
            {
                conds.Add(FieldCond(a, ClashItemTarget.Item1, tree));
                conds.Add(FieldCond(a, ClashItemTarget.Item2, tree));
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(a)) conds.Add(FieldCond(a, ClashItemTarget.Either, tree));
                if (!string.IsNullOrWhiteSpace(b)) conds.Add(FieldCond(b, ClashItemTarget.Either, tree));
            }
            return new ClashRule
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"{a} vs {b}" : name,
                AssigneeMode = AssigneeMode.Named,
                Assignee = assign.Trim(),
                GroupName = string.IsNullOrWhiteSpace(group) ? assign.Trim() : group.Trim(),
                ClashStatus = "Active",
                ConditionLogic = LogicOperator.And,
                IsEnabled = true,
                Conditions = conds
            };
        }

        /// <summary>A "contains" condition on either the element's Category property
        /// (fallback tier) or its tree path (fine tier — where family/leaf names live).</summary>
        private static RuleCondition FieldCond(string value, ClashItemTarget target, bool tree)
            => new RuleCondition
            {
                PropertyCategory = tree ? RuleCondition.TreeCategory : "",
                PropertyName = tree ? "Path" : "Category",
                Operator = ConditionOperator.Contains,
                Value = (value ?? "").Trim(),
                Target = target
            };

        private static KindMatchSide ParseSide(string s)
        {
            if (string.Equals(s, "a", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "itema", StringComparison.OrdinalIgnoreCase)) return KindMatchSide.ItemA;
            if (string.Equals(s, "b", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "itemb", StringComparison.OrdinalIgnoreCase)) return KindMatchSide.ItemB;
            return KindMatchSide.Either;
        }

        private static string Str(Dictionary<string, object> d, string key)
            => d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : null;

        private static double Dbl(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        private static List<string> StrList(Dictionary<string, object> d, string key)
        {
            var list = new List<string>();
            if (d != null && d.TryGetValue(key, out var v))
            {
                if (v is object[] arr) foreach (var x in arr) { if (x != null) list.Add(x.ToString()); }
                else if (v != null) list.Add(v.ToString());
            }
            return list;
        }
    }
}
