using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using ClashRuleEngine.Services;

namespace ClashRuleEngine.Models
{
    /// <summary>Which clashing side a kind-rule must match.</summary>
    public enum KindMatchSide { Either, ItemA, ItemB }

    /// <summary>Who a matched kind-rule assigns the clash to.</summary>
    ///   Owner — the trade of the matched element.
    ///   Other — the trade of the OPPOSITE element.
    ///   Named — a fixed trade/owner (<see cref="KindRule.Assignee"/>).
    public enum KindAssign { Owner, Other, Named }

    /// <summary>
    /// A single element-KIND assignment rule — the unit of the kind hierarchy derived
    /// from the coordinated NWDs. Detects a kind by keywords (matched against an
    /// element's category/family/type/system/tree text) plus an optional diameter
    /// band, on one or either side, then assigns owner / other / a named trade.
    /// e.g. "Hyd Drainage > 75 mm -> other", "Fire Flex -> Fire", "Mech Clearance -> Mech".
    /// Evaluated in list order, first match wins.
    /// </summary>
    [Serializable]
    public class KindRule
    {
        public string Name { get; set; } = "";

        /// <summary>Keywords matched (case-insensitive substring, ANY) against the
        /// element's kind text. Empty = match on size alone.</summary>
        public List<string> Keywords { get; set; } = new List<string>();

        /// <summary>Min diameter in mm (0 = no minimum).</summary>
        public double MinDiameterMm { get; set; }
        /// <summary>Max diameter in mm (0 = no maximum).</summary>
        public double MaxDiameterMm { get; set; }

        public KindMatchSide Side { get; set; } = KindMatchSide.Either;
        public KindAssign Assign { get; set; } = KindAssign.Named;

        /// <summary>Trade/owner for Named (also the fallback if Owner/Other can't classify).</summary>
        public string Assignee { get; set; } = "";
        public string GroupName { get; set; } = "";
        public bool IsEnabled { get; set; } = true;

        [XmlIgnore]
        public string KeywordsCsv
        {
            get { return string.Join(", ", Keywords ?? new List<string>()); }
            set
            {
                Keywords = (value ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            }
        }

        /// <summary>True if an element of this kind satisfies the rule's detection.</summary>
        public bool MatchesItem(ElementKindInfo info)
        {
            if (info == null) return false;
            bool hasKeywords = Keywords != null && Keywords.Count > 0;
            bool hasSize = MinDiameterMm > 0 || MaxDiameterMm > 0;
            if (!hasKeywords && !hasSize) return false;   // a rule must constrain something

            if (hasKeywords && !info.ContainsAny(Keywords)) return false;

            if (MinDiameterMm > 0)
            {
                if (info.DiameterMm <= 0 || info.DiameterMm < MinDiameterMm) return false;
            }
            if (MaxDiameterMm > 0)
            {
                if (info.DiameterMm <= 0 || info.DiameterMm > MaxDiameterMm) return false;
            }
            return true;
        }

        public override string ToString()
        {
            string size = MinDiameterMm > 0 || MaxDiameterMm > 0
                ? $" [{(MinDiameterMm > 0 ? MinDiameterMm + "mm" : "")}{(MaxDiameterMm > 0 ? "-" + MaxDiameterMm + "mm" : "+")}]"
                : "";
            string to = Assign == KindAssign.Owner ? "owner" : Assign == KindAssign.Other ? "other trade" : Assignee;
            return $"{Name}: {KeywordsCsv}{size} -> {to}";
        }
    }
}
