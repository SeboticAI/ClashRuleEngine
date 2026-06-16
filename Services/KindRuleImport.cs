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
            public List<DisciplineDefinition> Trades { get; set; } = new List<DisciplineDefinition>();
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

            if (root.TryGetValue("trades", out var tradesObj) && tradesObj is object[] trades)
            {
                foreach (var t in trades.OfType<Dictionary<string, object>>())
                {
                    string name = Str(t, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    res.Trades.Add(new DisciplineDefinition
                    {
                        Name = name,
                        Assignee = Str(t, "assignee") ?? name,
                        GroupName = Str(t, "group") ?? name,
                        Keywords = StrList(t, "keywords"),
                        Color = Str(t, "color") ?? "#6B7280"
                    });
                }
            }

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

            return res;
        }

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
