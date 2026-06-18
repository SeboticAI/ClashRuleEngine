using System;
using System.Collections.Generic;
using System.Text;

namespace ClashRuleEngine.Models
{
    /// <summary>A trade-pair-specific clearance floor: clashes between trades A and B are
    /// auto-approved once their gap ≥ <see cref="MinGapMm"/> (order-insensitive). Derived
    /// from the data — e.g. ELEC·MECH needs 50 mm, FIRE·MECH only 15 mm, HYD·MECH any.</summary>
    [Serializable]
    public class PairFloor
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public double MinGapMm { get; set; }
    }

    /// <summary>An element kind that is auto-approved regardless of clearance gap
    /// (e.g. Flex Pipe, Flex Duct). Any keyword matching either element's kind text approves.</summary>
    [Serializable]
    public class ApproveKind
    {
        public string Name { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }

    /// <summary>
    /// The "approve engine": after a clash is assigned a trade, auto-set its status to
    /// Approved when it falls inside the clearance tolerance AND the trade pairing is
    /// negotiable. Mirrors the observed coordination workflow — small soft clashes
    /// between two services get signed off, but anything touching structure never does.
    ///
    /// Decision per clash (all must hold):
    ///   • clearance gap (mm) ≥ <see cref="MinGapMm"/> AND (<see cref="MaxGapMm"/> ≤ 0 OR gap ≤ MaxGapMm)
    ///     (gap = ClashResult.Distance × 1000; negative = hard penetration, positive = clear space),
    ///   • neither side's trade is in <see cref="ProtectedTrades"/>,
    ///   • the test name doesn't name a protected trade (e.g. "_MECH vs _STR")
    ///     when <see cref="UseTestNameGuard"/> is on,
    ///   • the clash already has an assignee, when <see cref="RequireAssignee"/> is on.
    ///
    /// CALIBRATION (from the gap×status data): approval is a MINIMUM-CLEARANCE gate, not
    /// a small window. Touching clashes (gap 0–10 mm) and penetrations are approved ~4%
    /// of the time; once there is ≥10 mm of clear space approval jumps to ~25–34% and
    /// keeps rising with gap. Structure is never approved (0%). Hence the default is
    /// "≥ 30 mm of clearance, no upper bound" — tune MinGapMm up to be stricter, or set
    /// MaxGapMm > 0 if you want a strict [Min,Max] band instead.
    /// </summary>
    [Serializable]
    public class ApprovePolicy
    {
        public bool Enabled { get; set; } = false;

        /// <summary>Default minimum clearance gap (mm) for trade pairs NOT listed in
        /// <see cref="PairFloors"/>. Objects must be at least this far apart. A positive
        /// value never approves a penetration (gap &lt; 0).</summary>
        public double MinGapMm { get; set; } = 30.0;

        /// <summary>Per-trade-pair clearance floors (override the default MinGapMm for
        /// that pairing). This is where the approve decision gets specific: ELEC·MECH
        /// needs ~50 mm, FIRE·MECH ~15 mm, HYD·MECH approves at any gap.</summary>
        public List<PairFloor> PairFloors { get; set; } = new List<PairFloor>();
        /// <summary>Optional upper gap bound (mm). 0 (default) = no maximum — approval
        /// keeps applying for any clearance ≥ MinGapMm. Set &gt; 0 only for a strict band.</summary>
        public double MaxGapMm { get; set; } = 0.0;

        /// <summary>Trades that block auto-approval when either side belongs to one.
        /// Matched case-insensitively as a substring of the trade/test token.</summary>
        public List<string> ProtectedTrades { get; set; } = new List<string> { "STR", "Structure", "Structural" };

        /// <summary>Also block approval when the TEST name names a protected trade
        /// (e.g. "_FIRE vs _STR"). Cheap, robust, matches the data (0% approved vs structure).</summary>
        public bool UseTestNameGuard { get; set; } = true;

        /// <summary>Only approve clashes that already carry an assignee (assign-then-approve).</summary>
        public bool RequireAssignee { get; set; } = false;

        /// <summary>Name recorded as the approver (Clash Detective "Approved by"), so an
        /// auto-approval is a complete approval, not a status with a blank approver.</summary>
        public string ApprovedBy { get; set; } = "Clash Rule Engine";

        /// <summary>Assignees that are auto-approved REGARDLESS of clearance gap (incl.
        /// hard clashes). Data-driven: e.g. TUNDISH was ~90% approved historically.</summary>
        public List<string> ApproveAssignees { get; set; } = new List<string>();

        /// <summary>Element KINDS that are auto-approved regardless of gap (a clash is
        /// approved if EITHER element matches). For flexible elements (flex pipe/duct,
        /// ~91–94% approved) a hard clash is fine — they bend around. Keyword = substring
        /// of the element's kind text (category/family/type/system).</summary>
        public List<ApproveKind> ApproveKinds { get; set; } = new List<ApproveKind>();

        /// <summary>HARD SAFETY GATE: never auto-approve a hard clash (gap &lt; 0 = the two
        /// objects overlap/penetrate), no matter what a pair floor says. A penetration is a
        /// real clash. Default true; only a deliberate negative pair-floor + this=false
        /// would ever approve an overlap.</summary>
        public bool NeverApprovePenetration { get; set; } = true;

        public bool GapInZone(double gapMm) => gapMm >= MinGapMm && (MaxGapMm <= 0 || gapMm <= MaxGapMm);

        public bool HasAlwaysApprove =>
            (ApproveAssignees != null && ApproveAssignees.Count > 0) ||
            (ApproveKinds != null && ApproveKinds.Count > 0);

        /// <summary>True if this clash's assignee is in the always-approve list.</summary>
        public bool IsApproveAssignee(string assignee)
        {
            if (string.IsNullOrWhiteSpace(assignee) || ApproveAssignees == null) return false;
            foreach (var a in ApproveAssignees)
                if (!string.IsNullOrWhiteSpace(a) &&
                    string.Equals(a.Trim(), assignee.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>True if either element's kind text matches an always-approve kind.</summary>
        public bool KindApproved(string kindTextA, string kindTextB)
        {
            if (ApproveKinds == null) return false;
            foreach (var ak in ApproveKinds)
            {
                if (ak?.Keywords == null) continue;
                foreach (var kw in ak.Keywords)
                {
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    string k = kw.Trim();
                    if ((kindTextA != null && kindTextA.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (kindTextB != null && kindTextB.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                        return true;
                }
            }
            return false;
        }

        /// <summary>The clearance floor (mm) for a trade pair: the matching
        /// <see cref="PairFloor"/> if listed (order-insensitive), else <see cref="MinGapMm"/>.</summary>
        public double FloorFor(string a, string b)
        {
            if (PairFloors != null && !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b))
                foreach (var pf in PairFloors)
                {
                    if (pf == null) continue;
                    if ((TradeMatch(pf.A, a) && TradeMatch(pf.B, b)) ||
                        (TradeMatch(pf.A, b) && TradeMatch(pf.B, a)))
                        return pf.MinGapMm;
                }
            return MinGapMm;
        }

        /// <summary>True if a clash with this gap and trade pair should be approved
        /// (gap ≥ the pair's floor, within any upper bound).</summary>
        public bool GapApproved(double gapMm, string tradeA, string tradeB)
        {
            if (NeverApprovePenetration && gapMm < 0) return false;   // never sign off a hard clash
            double floor = FloorFor(tradeA, tradeB);
            return gapMm >= floor && (MaxGapMm <= 0 || gapMm <= MaxGapMm);
        }

        /// <summary>Two short trade tokens are the same trade (case-insensitive, either
        /// contains the other — tolerates "FIRE" vs "Fire Services").</summary>
        private static bool TradeMatch(string x, string y)
        {
            if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)) return false;
            x = x.Trim(); y = y.Trim();
            return x.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0
                || y.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Pulls the two trade tokens out of a clash-test name like
        /// "_FIRE vs _MECH" → ("FIRE","MECH"). Letters only, upper-cased.</summary>
        public static void ParseTestTrades(string testName, out string a, out string b)
        {
            a = null; b = null;
            if (string.IsNullOrWhiteSpace(testName)) return;
            int v = testName.IndexOf(" vs ", StringComparison.OrdinalIgnoreCase);
            int skip = 4;
            if (v < 0)
            {
                // single-"v" separator (e.g. the test-model "MC v FC")
                int p = testName.IndexOf(" v ", StringComparison.OrdinalIgnoreCase);
                if (p >= 0) { v = p; skip = 3; }
            }
            if (v < 0)
            {
                // Fallback: a standalone "vs" token (non-letter boundaries) so we never
                // match "vs" buried inside a trade name.
                for (int i = 0; i + 2 <= testName.Length; i++)
                {
                    if ((testName[i] == 'v' || testName[i] == 'V') && (testName[i + 1] == 's' || testName[i + 1] == 'S')
                        && (i == 0 || !char.IsLetter(testName[i - 1]))
                        && (i + 2 >= testName.Length || !char.IsLetter(testName[i + 2])))
                    { v = i; skip = 2; break; }
                }
            }
            if (v < 0) return;
            a = LettersUpper(testName.Substring(0, v));
            b = LettersUpper(testName.Substring(v + skip));
        }

        private static string LettersUpper(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetter(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>True if the given trade/test token contains any protected-trade keyword.</summary>
        public bool IsProtected(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || ProtectedTrades == null) return false;
            foreach (var p in ProtectedTrades)
                if (!string.IsNullOrWhiteSpace(p) &&
                    token.IndexOf(p.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
