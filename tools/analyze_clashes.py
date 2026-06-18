#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
analyze_clashes.py - mine the batch clash dataset into importable rules + diagnostics.

WHAT IT READS
  The JSONL produced by the headless batch extractor (Plugin/BatchClashExtractPlugin.cs,
  driven by tools/run-batch-extract.ps1). Default: %USERPROFILE%\\Desktop\\clash_kinds.jsonl.
  Each line is one aggregated bucket:
    {
      "file","test",
      "a":{"cat","kind","fam","type","leaf","sys","dia","diaMm":{"min","max"}},
      "b":{ ... same ... },
      "assignee","status","grid","level",
      "gap":{"band","min","max"},
      "count"
    }
  Works on the OLD schema too (missing fam/type/leaf/diaMm are tolerated).

WHAT IT WRITES  (next to the input, on the Desktop by default)
  1. clashre_kind_rules.generated.json   - drop straight into the Clash Rule Engine via
     Import. Schema "clashre-kind-rules/1": per-test two-tier `testRules` (fine tree rules
     first, category fallback second), per-test `tests` defaults (the last-resort matrix
     fallback), and an `approve` block (per-trade-pair clearance floors + always-approve
     kinds/assignees) calibrated from the gap x status data.
  2. clash_analysis_report.txt           - human diagnostics: per-test assignee mix and
     rule coverage, approve calibration, the SERVICE-TYPE x CLEARANCE breakdown (for close
     clashes, which service types you assigned where and how often you approved), and
     diameter-split suggestions (e.g. "Waste >75mm -> other trade").

DESIGN (matches how the engine runs)
  - Rules are mined per CANONICAL trade pair (e.g. "MECH vs STR"), so MC v SC / _MECH vs _STR
    across different files all merge - and the engine's fuzzy test matcher re-applies them.
  - NO blanket rules: a specific rule is emitted only where a pair DEVIATES from the test's
    dominant assignee. The dominant assignee is the per-test DEFAULT (the safety net that
    keeps every clash assigned), not a rule. The report shows how much real-rule coverage
    you get vs how much falls through to the default.

USAGE
    python tools/analyze_clashes.py [path-to-clash_kinds.jsonl]
"""

import json
import os
import sys
from collections import defaultdict

# ---------------------------------------------------------------------------
# Tunables - adjust to taste, then re-run (cheap, no re-extract needed).
# ---------------------------------------------------------------------------
CAT_MIN_SUPPORT   = 6      # min weighted clashes for a category-tier (fallback) rule
CAT_MIN_PURITY    = 0.55   # dominant assignee must own this fraction of the pair
TREE_MIN_SUPPORT  = 4      # fine (family/leaf) rules can fire on less - they're specific
TREE_MIN_PURITY   = 0.70
DEFAULT_MIN_PURITY = 0.30  # only emit a per-test default if a trade is at least this dominant
MAX_RULES_PER_TEST = 60    # perf guard - cap specific rules per test (keep highest-support)

CLOSE_MM          = 25.0   # "clearance was close": gap at/under this (incl. penetrations)
APPROVE_RATE_GATE = 0.20   # gap band counts as "commonly approved" above this approval rate
ASSIGNEE_APPROVE_RATE = 0.80   # assignee auto-approved regardless of gap above this
ASSIGNEE_APPROVE_SUPPORT = 15
DIA_SEPARATION_MM = 30.0   # min median-bore gap between two assignees to suggest a size split
DIA_MIN_SUPPORT   = 8

NOISE_TOKENS = {
    "", "null", "none", "solid", "standard", "default", "internal",
    "<not shared>", "generic models", "direct shape", "?",
}

# Trade canonicalisation - mirrors the C# side (ApprovePolicy.ParseTestTrades +
# TestRuleSet trade synonyms) so emitted pair floors match what the engine parses
# from a live test name at run time.
TRADE_CODES = {"MC": "MECH", "EC": "ELEC", "FC": "FIRE", "HC": "HYD", "SC": "STR", "IC": "ICT"}
TRADE_WORDS = [  # (substring, canonical) - longer/more-specific first
    ("MECHAN", "MECH"), ("HVAC", "MECH"), ("MECH", "MECH"),
    ("ELECTR", "ELEC"), ("ELEC", "ELEC"),
    ("SPRINK", "FIRE"), ("FIRE", "FIRE"),
    ("HYDRAUL", "HYD"), ("PLUMB", "HYD"), ("DRAIN", "HYD"), ("HYD", "HYD"),
    ("STRUCT", "STR"), ("STR", "STR"),
    ("COMMS", "ICT"), ("COMM", "ICT"), ("DATA", "ICT"), ("ICT", "ICT"),
]


# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------
def clean(v):
    """Trim a token; treat noise/empty as None."""
    if v is None:
        return None
    s = str(v).strip()
    return None if s.lower() in NOISE_TOKENS else s


def canon_trade(tok):
    """Letters-only trade code -> canonical short trade (MECH/ELEC/FIRE/HYD/STR/ICT)."""
    if not tok:
        return None
    t = "".join(ch for ch in str(tok).upper() if ch.isalpha())
    if not t:
        return None
    if t in TRADE_CODES:
        return TRADE_CODES[t]
    for sub, canon in TRADE_WORDS:
        if sub in t:
            return canon
    return t  # unknown trade - keep verbatim


def parse_test_trades(name):
    """Split a clash-test name into two trade tokens, mirroring C# ParseTestTrades."""
    if not name:
        return None, None
    low = name.lower()
    idx, skip = low.find(" vs "), 4
    if idx < 0:
        p = low.find(" v ")
        if p >= 0:
            idx, skip = p, 3
    if idx < 0:
        # standalone "vs" on non-letter boundaries
        for i in range(len(name) - 1):
            if name[i] in "vV" and name[i + 1] in "sS":
                before = (i == 0) or (not name[i - 1].isalpha())
                after = (i + 2 >= len(name)) or (not name[i + 2].isalpha())
                if before and after:
                    idx, skip = i, 2
                    break
    if idx < 0:
        return None, None
    return name[:idx], name[idx + skip:]


def canon_test(name):
    """Canonical 'T1 vs T2' (sorted) for a test, or the raw name if it won't parse."""
    a, b = parse_test_trades(name)
    ca, cb = canon_trade(a), canon_trade(b)
    if ca and cb:
        x, y = sorted([ca, cb])
        return "%s vs %s" % (x, y), (x, y)
    return (name or "?").strip(), None


def side_cat(s):
    return clean((s or {}).get("cat"))


def side_fine(s):
    """Most specific within-trade token: leaf > type > family > kind > system."""
    s = s or {}
    for k in ("leaf", "type", "fam", "kind", "sys"):
        v = clean(s.get(k))
        if v:
            return v
    return None


def side_dia(s):
    """Representative bore (mm) for a side from the diaMm range, else 0."""
    d = (s or {}).get("diaMm") or {}
    try:
        mx = float(d.get("max") or 0)
        mn = float(d.get("min") or 0)
    except (TypeError, ValueError):
        return 0.0
    return mx or mn or 0.0


def assignee_of(row):
    a = clean(row.get("assignee"))
    if a and a.lower() not in ("(unassigned)", "unassigned"):
        return a
    return None


def weight(row):
    try:
        return float(row.get("count") or 1)
    except (TypeError, ValueError):
        return 1.0


def is_approved(row):
    return "approv" in (row.get("status") or "").lower()


def gap_mid(row):
    g = row.get("gap") or {}
    mn, mx = g.get("min"), g.get("max")
    try:
        if mn is None and mx is None:
            return None
        if mn is None:
            return float(mx)
        if mx is None:
            return float(mn)
        return (float(mn) + float(mx)) / 2.0
    except (TypeError, ValueError):
        return None


def order_pair(a, b):
    """Unordered pair canonical: non-empty first, then alphabetical."""
    xs = [x for x in (a, b) if x]
    xs.sort()
    if len(xs) == 2:
        return xs[0], xs[1]
    if len(xs) == 1:
        return xs[0], None
    return None, None


def wmedian(pairs):
    """Weighted median of [(value, weight), ...]."""
    pairs = sorted((v, w) for v, w in pairs if w > 0)
    if not pairs:
        return None
    total = sum(w for _, w in pairs)
    acc = 0.0
    for v, w in pairs:
        acc += w
        if acc >= total / 2.0:
            return v
    return pairs[-1][0]


def pct(n, d):
    return (100.0 * n / d) if d else 0.0


# ---------------------------------------------------------------------------
# Load
# ---------------------------------------------------------------------------
def load_rows(path):
    rows = []
    with open(path, "r", encoding="utf-8-sig") as f:
        for ln in f:
            ln = ln.strip()
            if not ln:
                continue
            try:
                r = json.loads(ln)
            except ValueError:
                continue
            tname, _ = canon_test(r.get("test"))
            r["_test"] = tname
            rows.append(r)
    return rows


# ---------------------------------------------------------------------------
# Mining
# ---------------------------------------------------------------------------
def test_defaults(rows):
    """Per-test dominant assignee (the last-resort matrix fallback)."""
    bucket = defaultdict(lambda: defaultdict(float))
    for r in rows:
        a = assignee_of(r)
        if a:
            bucket[r["_test"]][a] += weight(r)
    out = {}
    for test, asgs in bucket.items():
        total = sum(asgs.values())
        if total <= 0:
            continue
        asg, top = max(asgs.items(), key=lambda kv: kv[1])
        if top / total >= DEFAULT_MIN_PURITY:
            out[test] = (asg, top, total, top / total)
    return out


def mine_pairs(rows, token_fn, min_support, min_purity, defaults, tier):
    """Emit deviation rules: a (test, tokenA, tokenB) pair whose dominant assignee differs
    from the test default (so it carries real signal beyond the matrix fallback)."""
    bucket = defaultdict(lambda: defaultdict(float))
    for r in rows:
        asg = assignee_of(r)
        if not asg:
            continue
        a, b = order_pair(token_fn(r.get("a")), token_fn(r.get("b")))
        if not a and not b:
            continue
        bucket[(r["_test"], a, b)][asg] += weight(r)

    rules = []
    for (test, a, b), asgs in bucket.items():
        total = sum(asgs.values())
        if total < min_support:
            continue
        asg, top = max(asgs.items(), key=lambda kv: kv[1])
        purity = top / total
        if purity < min_purity:
            continue
        default_asg = defaults.get(test, (None,))[0]
        if asg == default_asg:
            continue  # covered by the per-test default - not worth a rule (keeps it lean)
        rules.append({
            "_test": test, "tier": tier, "a": a, "b": b,
            "assign": asg, "support": total, "purity": purity,
        })
    return rules


def mine_approve(rows):
    """Calibrate the approve engine from gap x status: per-trade-pair clearance floors,
    plus always-approve assignees and element kinds."""
    pair = defaultdict(lambda: {"appr": 0.0, "tot": 0.0, "appr_gaps": []})
    asg_stat = defaultdict(lambda: {"appr": 0.0, "tot": 0.0})
    band_stat = defaultdict(lambda: {"appr": 0.0, "tot": 0.0})

    for r in rows:
        w = weight(r)
        ap = is_approved(r)
        # per trade pair (from the test name)
        _, trades = canon_test(r.get("test"))
        if trades:
            key = tuple(sorted(trades))
            pair[key]["tot"] += w
            if ap:
                pair[key]["appr"] += w
                gm = gap_mid(r)
                if gm is not None:
                    pair[key]["appr_gaps"].append((gm, w))
        # per assignee (gap-independent always-approve detection)
        a = assignee_of(r)
        if a:
            asg_stat[a]["tot"] += w
            if ap:
                asg_stat[a]["appr"] += w
        # per gap band (global approval-vs-gap curve)
        band = (r.get("gap") or {}).get("band") or "?"
        band_stat[band]["tot"] += w
        if ap:
            band_stat[band]["appr"] += w

    # Per-pair floor = 20th-percentile of approved-clash gaps, clamped to >= 0
    # (structure pairs are left to the protected-trade guard, no floor emitted).
    floors = []
    for (x, y), d in sorted(pair.items()):
        if "STR" in (x, y):
            continue
        if d["appr"] < 6 or not d["appr_gaps"]:
            continue
        gaps = sorted(g for g, _ in d["appr_gaps"])
        floor = gaps[max(0, int(0.20 * (len(gaps) - 1)))]
        floor = round(max(0.0, floor))
        floors.append({"a": x, "b": y, "minGapMm": floor,
                       "_rate": pct(d["appr"], d["tot"]), "_n": int(d["tot"])})

    approve_assignees = [
        a for a, d in sorted(asg_stat.items(), key=lambda kv: -kv[1]["tot"])
        if d["tot"] >= ASSIGNEE_APPROVE_SUPPORT and d["appr"] / d["tot"] >= ASSIGNEE_APPROVE_RATE
    ]

    return floors, approve_assignees, asg_stat, band_stat, pair


def detect_flex_kinds(rows):
    """Always-approve element kinds: curated 'flex' + any token strongly tied to approval."""
    kinds = [{"name": "Flexible pipe/duct", "keywords": ["flex"]}]
    tok_stat = defaultdict(lambda: {"appr": 0.0, "tot": 0.0})
    for r in rows:
        w = weight(r)
        ap = is_approved(r)
        seen = set()
        for sd in (r.get("a"), r.get("b")):
            for k in ("leaf", "type", "fam", "kind"):
                v = clean((sd or {}).get(k))
                if v:
                    seen.add(v.lower())
        for t in seen:
            tok_stat[t]["tot"] += w
            if ap:
                tok_stat[t]["appr"] += w
    extra = []
    for t, d in tok_stat.items():
        if "flex" in t:
            continue
        if d["tot"] >= 30 and d["appr"] / d["tot"] >= 0.85:
            extra.append((t, d["appr"] / d["tot"], int(d["tot"])))
    extra.sort(key=lambda x: -x[2])
    for t, _, _ in extra[:6]:
        kinds.append({"name": t, "keywords": [t]})
    return kinds, extra[:6]


def service_clearance(rows):
    """SERVICE-TYPE x CLEARANCE: for close clashes (gap <= CLOSE_MM incl. penetrations),
    which service types appear and how they were assigned/approved - per test."""
    # bucket[test][service-token] = {tot, appr, assignees{name:w}}
    out = defaultdict(lambda: defaultdict(lambda: {"tot": 0.0, "appr": 0.0,
                                                   "asg": defaultdict(float)}))
    for r in rows:
        gm = gap_mid(r)
        if gm is None or gm > CLOSE_MM:
            continue
        w = weight(r)
        ap = is_approved(r)
        a = assignee_of(r)
        toks = set()
        for sd in (r.get("a"), r.get("b")):
            t = side_fine(sd) or side_cat(sd)
            if t:
                toks.add(t)
        for t in toks:
            cell = out[r["_test"]][t]
            cell["tot"] += w
            if ap:
                cell["appr"] += w
            if a:
                cell["asg"][a] += w
    return out


def detect_dia_splits(rows):
    """Suggest size thresholds: a fine service token whose assignee flips with bore."""
    # bucket[(test, token)][assignee] = [(dia,w), ...]
    bucket = defaultdict(lambda: defaultdict(list))
    for r in rows:
        a = assignee_of(r)
        if not a:
            continue
        w = weight(r)
        for sd in (r.get("a"), r.get("b")):
            tok = side_fine(sd)
            dia = side_dia(sd)
            if tok and dia > 0:
                bucket[(r["_test"], tok)][a].append((dia, w))
    suggestions = []
    for (test, tok), asgs in bucket.items():
        meds = []
        for a, vals in asgs.items():
            tot = sum(w for _, w in vals)
            if tot >= DIA_MIN_SUPPORT:
                meds.append((wmedian(vals), tot, a))
        if len(meds) < 2:
            continue
        meds.sort()
        lo, hi = meds[0], meds[-1]
        if hi[0] - lo[0] >= DIA_SEPARATION_MM:
            thresh = round((lo[0] + hi[0]) / 2.0)
            suggestions.append({
                "test": test, "token": tok, "threshold": thresh,
                "small_assign": lo[2], "small_med": round(lo[0]), "small_n": int(lo[1]),
                "large_assign": hi[2], "large_med": round(hi[0]), "large_n": int(hi[1]),
            })
    suggestions.sort(key=lambda s: -(s["small_n"] + s["large_n"]))
    return suggestions


# ---------------------------------------------------------------------------
# Emit
# ---------------------------------------------------------------------------
def build_rules_json(defaults, cat_rules, tree_rules, floors, approve_assignees, flex_kinds):
    # Order per test: tree (fine) rules first, then category fallback; highest support first.
    per_test = defaultdict(list)
    for r in tree_rules:
        per_test[r["_test"]].append(r)
    for r in cat_rules:
        per_test[r["_test"]].append(r)

    test_rules_json = []
    for test in sorted(per_test):
        rs = sorted(per_test[test], key=lambda r: (r["tier"], -r["support"]))[:MAX_RULES_PER_TEST]
        for r in rs:
            entry = {
                "test": test,
                "name": "%s | %s%s%s" % (
                    test,
                    r["a"] or "*",
                    (" + " + r["b"]) if r["b"] else "",
                    "" if r["tier"] == 0 else " (cat)"),
                "a": r["a"] or "",
                "b": r["b"] or "",
                "assign": r["assign"],
                "group": r["assign"],
            }
            if r["tier"] == 0:
                entry["match"] = "tree"
            test_rules_json.append(entry)

    tests_json = [{"test": t, "assign": d[0]} for t, d in sorted(defaults.items())]

    approve = {
        "enabled": True,
        "minGapMm": 25,
        "maxGapMm": 0,
        "useTestNameGuard": True,
        "protectedTrades": ["STR", "Structure", "Structural"],
        "pairFloors": [{"a": f["a"], "b": f["b"], "minGapMm": f["minGapMm"]} for f in floors],
        "approveAssignees": approve_assignees,
        "approveKinds": flex_kinds,
    }

    return {
        "schema": "clashre-kind-rules/1",
        "_generatedBy": "tools/analyze_clashes.py",
        "tests": tests_json,
        "testRules": test_rules_json,
        "approve": approve,
    }


def write_report(path, rows, defaults, cat_rules, tree_rules, floors, approve_assignees,
                 flex_kinds, flex_extra, svc, dia_splits, asg_stat, band_stat):
    L = []
    w = L.append
    total_clashes = sum(weight(r) for r in rows)
    files = {r.get("file") for r in rows}
    tests = sorted({r["_test"] for r in rows})
    assignees = {assignee_of(r) for r in rows if assignee_of(r)}

    w("=" * 78)
    w("CLASH ASSIGNMENT ANALYSIS")
    w("=" * 78)
    w("rows (aggregated buckets) : %d" % len(rows))
    w("clashes (weighted)        : %d" % round(total_clashes))
    w("source files              : %d" % len(files))
    w("canonical tests           : %d" % len(tests))
    w("distinct assignees        : %d" % len(assignees))
    enriched = any((r.get("a") or {}).get("leaf") or (r.get("a") or {}).get("type") for r in rows)
    w("schema                    : %s" % ("ENRICHED (leaf/type/diaMm present)"
                                          if enriched else "BASIC (no leaf/type/diaMm)"))
    w("")

    w("-" * 78)
    w("PER-TEST: assignee mix, default (fallback) and rule coverage")
    w("-" * 78)
    rule_tests = defaultdict(int)
    for r in cat_rules + tree_rules:
        rule_tests[r["_test"]] += 1
    for test in tests:
        sub = [r for r in rows if r["_test"] == test]
        tot = sum(weight(r) for r in sub)
        mix = defaultdict(float)
        for r in sub:
            a = assignee_of(r) or "(unassigned)"
            mix[a] += weight(r)
        top = sorted(mix.items(), key=lambda kv: -kv[1])[:4]
        d = defaults.get(test)
        w("%-22s n=%-6d rules=%-3d default=%s" % (
            test, round(tot), rule_tests.get(test, 0),
            ("%s (%.0f%%)" % (d[0], 100 * d[3])) if d else "(none - low dominance)"))
        w("    mix: " + ", ".join("%s %.0f%%" % (a, pct(v, tot)) for a, v in top))
    w("")

    w("-" * 78)
    w("APPROVE CALIBRATION")
    w("-" * 78)
    w("Global approval rate by gap band (negative band = hard penetration):")
    band_order = ["pen>=50mm", "pen10-50mm", "pen<10mm", "gap0-10mm", "gap10-25mm",
                  "gap25-50mm", "gap50-100mm", "gap>100mm", "?"]
    for b in band_order:
        d = band_stat.get(b)
        if not d or d["tot"] == 0:
            continue
        flag = "  <- commonly approved" if d["appr"] / d["tot"] >= APPROVE_RATE_GATE else ""
        w("    %-14s %5.1f%% approved (n=%d)%s" % (b, pct(d["appr"], d["tot"]), round(d["tot"]), flag))
    w("")
    w("Per-trade-pair clearance floor (auto-approve once gap >= floor; STR pairs excluded):")
    if floors:
        for f in sorted(floors, key=lambda x: -x["_n"]):
            w("    %-12s minGap=%3dmm   (hist approval %.0f%%, n=%d)" % (
                "%s/%s" % (f["a"], f["b"]), f["minGapMm"], f["_rate"], f["_n"]))
    else:
        w("    (none derived)")
    w("")
    w("Always-approve assignees (>=%.0f%% approved, gap-independent):" % (100 * ASSIGNEE_APPROVE_RATE))
    w("    " + (", ".join(approve_assignees) if approve_assignees else "(none)"))
    w("Always-approve kinds:")
    w("    curated: flex")
    if flex_extra:
        w("    data-detected: " + ", ".join("%s (%.0f%%, n=%d)" % (t, 100 * r, n) for t, r, n in flex_extra))
    w("")

    w("-" * 78)
    w("SERVICE-TYPE x CLEARANCE  (close clashes: gap <= %.0fmm, incl. penetrations)" % CLOSE_MM)
    w("How you handled tight clashes, broken down by the service type involved.")
    w("-" * 78)
    for test in sorted(svc):
        cells = svc[test]
        ranked = sorted(cells.items(), key=lambda kv: -kv[1]["tot"])[:8]
        if not ranked:
            continue
        w("%s:" % test)
        for tok, d in ranked:
            if d["tot"] < 3:
                continue
            top_asg = sorted(d["asg"].items(), key=lambda kv: -kv[1])[:2]
            asg_s = ", ".join("%s %.0f%%" % (a, pct(v, d["tot"])) for a, v in top_asg) or "(unassigned)"
            w("    %-28s n=%-5d approved %3.0f%%  ->  %s" % (
                tok[:28], round(d["tot"]), pct(d["appr"], d["tot"]), asg_s))
    w("")

    w("-" * 78)
    w("DIAMETER-SPLIT SUGGESTIONS  (assignment that flips with bore - vet before adding)")
    w("-" * 78)
    if dia_splits:
        for s in dia_splits[:25]:
            w("    [%s] %s:" % (s["test"], s["token"][:40]))
            w("        <=%dmm -> %s (median %dmm, n=%d)   |   >%dmm -> %s (median %dmm, n=%d)" % (
                s["threshold"], s["small_assign"], s["small_med"], s["small_n"],
                s["threshold"], s["large_assign"], s["large_med"], s["large_n"]))
    else:
        w("    (none found - needs the enriched extract with diaMm to populate)")
    w("")

    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(L))


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
def main():
    default_in = os.path.join(os.path.expanduser("~"), "Desktop", "clash_kinds.jsonl")
    in_path = sys.argv[1] if len(sys.argv) > 1 else default_in
    if not os.path.isfile(in_path):
        print("Input not found: %s" % in_path)
        print("Generate it first:  tools\\run-batch-extract.ps1 -NwdFolder <folder>")
        print("Or pass a path:     python tools/analyze_clashes.py <clash_kinds.jsonl>")
        return 2

    rows = load_rows(in_path)
    if not rows:
        print("No usable rows in %s" % in_path)
        return 2

    defaults = test_defaults(rows)
    cat_rules = mine_pairs(rows, side_cat, CAT_MIN_SUPPORT, CAT_MIN_PURITY, defaults, tier=1)
    tree_rules = mine_pairs(rows, side_fine, TREE_MIN_SUPPORT, TREE_MIN_PURITY, defaults, tier=0)
    floors, approve_assignees, asg_stat, band_stat, _pair = mine_approve(rows)
    flex_kinds, flex_extra = detect_flex_kinds(rows)
    svc = service_clearance(rows)
    dia_splits = detect_dia_splits(rows)

    rules_json = build_rules_json(defaults, cat_rules, tree_rules, floors,
                                  approve_assignees, flex_kinds)

    out_dir = os.path.dirname(os.path.abspath(in_path))
    json_path = os.path.join(out_dir, "clashre_kind_rules.generated.json")
    report_path = os.path.join(out_dir, "clash_analysis_report.txt")

    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(rules_json, f, indent=2, ensure_ascii=False)
    write_report(report_path, rows, defaults, cat_rules, tree_rules, floors,
                 approve_assignees, flex_kinds, flex_extra, svc, dia_splits, asg_stat, band_stat)

    print("Read %d rows (%d weighted clashes) from %s" %
          (len(rows), round(sum(weight(r) for r in rows)), in_path))
    print("Mined: %d default(s), %d fine rule(s), %d category rule(s), %d pair floor(s)" %
          (len(defaults), len(tree_rules), len(cat_rules), len(floors)))
    print("Wrote rules : %s" % json_path)
    print("Wrote report: %s" % report_path)
    print("Import the .json via the Clash Rule Engine -> Import button.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
