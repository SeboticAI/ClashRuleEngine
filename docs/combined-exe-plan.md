# Combined EXE — "Clash Control Center" (plan)

> Status: PLAN / brainstorm captured 2026-06-18. Nothing here is built yet.
> The goal: one desktop app that is the be-all-end-all clash-coordination cockpit
> for BIM managers — ingest NWDs, learn & apply assignment rules, tune approvals,
> publish dashboards/reports, and surface how the team generally assigns clashes.

This consolidates everything we already have (the Navisworks addin, the batch
extractor, the Python analyzer, the global rule store) into a single product, plus
the brainstormed control features below.

> A clickable HTML prototype of these screens lives at
> [prototype/clash-control-center.html](prototype/clash-control-center.html) — it reads the
> real generated rules file (confidence, rules, floors, defaults, AI handoff) and mocks the
> rest. Open it to visualise the BIM-manager feedback.

---

## 1. Why / who
BIM managers need to **control the clash report**, not just run it: decide who owns
what, how lenient approvals are, and produce a clean report for the wider team. Today
that is manual + tribal knowledge. This app turns the team's *historical* decisions
into a tunable, transparent, repeatable engine — and packages the output as a
shareable dashboard.

The demo story: **"drop in your coordinated NWDs → it learns how you assign → tune a
couple of sliders → apply to Navisworks and publish a report."**

---

## 2. Architecture

### 2.1 One EXE, shared engine, zero Python
- **.NET desktop app** (extend `tools\NwdClashLearner`, WinForms today — or re-shell
  in WPF). It already does the *extract* half (pick NWDs → run the deployed extractor
  → `clash_kinds.jsonl`).
- **Port `tools\analyze_clashes.py` → C#.** The Python logic is final (specific-trade
  preference, two-tier rules, approve calibration, confidence replay, AI handoff). A
  shipped product must NOT depend on Python (we hit the 0-byte MS-Store stub / stale-PATH
  hell — that can never reach a customer). Keep the `.py` as the dev sandbox.
- **Share the addin's models.** `ClashRule`, `RuleCondition`, `ApprovePolicy`,
  `TestRuleSet`/`ProjectConfig` should move to a shared class library referenced by BOTH
  the Navisworks addin and this EXE, so "the engine" is one implementation, not two.
- **Apply = write the global config.** `%AppData%\ClashRuleEngine\config.clashre`
  (already the single source of truth → instantly live in every Navisworks). REPLACE
  semantics, never append. Versioned backups so a bad apply can roll back.

### 2.2 Data flow
```
 NWDs ──► extract (deployed BatchClashExtractPlugin) ──► clash_kinds.jsonl
        └► analyze (C# port) ──► rules + approve + confidence + AI-handoff
                              └► REVIEW / TUNE (the tabs below)
                                 └► Apply ──► %AppData%\config.clashre ──► Navisworks
                                 └► Publish ──► HTML dashboard / report
```

---

## 3. Feature modules (tabs)

### 3.1 Ingest & Learn
- Drag-drop NWDs (or a folder). Runs the headless extractor, shows progress.
- Mines rules in-process (C# analyzer). Shows the **confidence** headline
  ("reproduces 82% of your specific historical calls") + per-test breakdown.
- Re-run / incremental: add more NWDs later, re-mine.

### 3.2 Rules
- View/edit the per-test element-pair rules (add / disable / reorder / see support &
  confidence). Two tiers visible: fine (family/leaf) vs category fallback.
- **AI handoff (hand off where we can):** the analyzer flags low-confidence + soft-default
  tests (`_aiHandoff`). One click sends those to the AI rule generator (existing
  `AiRuleGenerator`/`ClaudeApiService`) to propose better rules / names; user accepts
  per-rule. Deterministic miner = grounded proposer, AI = judgment/naming on top.

### 3.3 Approvals — the strict ↔ lenient control  ⭐ (brainstormed)
The approve engine is already data-driven (`ApprovePolicy`: per-trade-pair clearance
floors, always-approve kinds/assignees, never-penetration, structure guard). Expose it:
- **One master slider: Strict ↔ Lenient.** Scales every clearance floor by a factor
  (e.g. Strict = floors ×1.5 & no penetration approvals; Lenient = floors ×0.5). Maps to
  `ApprovePolicy.MinGapMm` + each `PairFloor.MinGapMm`.
- **Per-pair fine-tune** sliders (ELEC·MECH needs more clearance than FIRE·MECH — the
  data already shows this).
- Toggles: never-approve penetration, structure guard, require-assignee.
- **Live preview:** "at this setting, X% of your historical clashes auto-approve" (replay
  the policy over `clash_kinds.jsonl` as the slider moves). Makes the trade-off tangible.

### 3.4 Trade hierarchy view  ⭐ (brainstormed: "how we generally assign")
Derive an **overall trade hierarchy from the individual rules** (not imposed — emergent):
- For every trade pair, aggregate who-gets-assigned-over-whom across all tests/rules
  (win rate). Build a directed dominance graph → rank into a general ordering
  (e.g. "in general: STR > FIRE > HYD > MECH > ELEC > ICT > SEC").
- Display as a **dominance matrix** (rows beat columns, with %) AND a ranked list.
- Flag **inconsistencies** (A usually beats B, but B beats A in test X) — these are worth
  a human look (and feed the AI handoff). This turns hundreds of fine rules into one
  legible "this is how your team coordinates" picture for the BIM manager.

### 3.5 Apply
- Write rules + approve policy to the global config (REPLACE), gated by a confidence
  confirmation ("this reproduces 82% of your calls — apply?").
- **"Always assign to one trade" override** ⭐ (brainstormed): a global switch to force a
  whole test (or all tests) to a single trade, overriding mined rules — for the cases a
  manager just wants everything in a test to go to one party. Per-test and global scope.
- Versioned config history + rollback.

### 3.6 Dashboard & Reports  ⭐ (brainstormed: combine our HTML dashboard file)
- **Integrate the existing HTML dashboard generator** (currently a separate file, NOT in
  this repo — locate & bring it in as `tools\dashboard\` or a C# HTML emitter).
- Produce a shareable, self-contained **HTML clash report** for the wider team:
  - Per-test status (active / approved / assigned), approval rates.
  - **Grid / level heatmap** of clash density (we already capture grid + level per clash).
  - **Trade workload** — how many clashes each trade owns (balance view).
  - **Trend / delta between runs** — clash count over time, who's burning down.
  - The derived trade hierarchy (3.4) and confidence (3.1) summarised.
- Builds on `SessionExportService` (the `clashre-session/1` export already exists).

### 3.7 Per-trade reports & distribution  ⭐ (brainstormed)
Push each trade *their* outstanding work, and close the loop with the close-out tool.
- **Per-trade report:** for each trade, generate "what's left for you" — the active
  (unresolved) clashes assigned to that trade, with **hard clashes** (penetrations, gap < 0)
  and **priority** ones (deep penetration, vs-structure, life-safety, dense grid cells)
  called out at the top. Output HTML / PDF / BCF.
- **Drafter directory:** next to each trade in the UI, a small menu to set the **drafter's
  name + email** for this job (persisted per project/trade). The manager records once who
  drafts FIRE / HYD / ELEC … on this project.
- **Send report:** one click emails each trade's drafter their report (or "Send all").
  Transport options: SMTP, a draft via the default mail client (mailto + attachment), or
  hand off to the dashboard export.
- **Closes the loop both ways:** this is the OUTBOUND counterpart to the close-out tool
  (§ inbound, see [closeout-tool-plan.md](closeout-tool-plan.md)). Push open clashes out →
  the trade replies by email → AI turns the reply into a `clashre-closeout/1` file → import
  & apply. Same per-trade contacts, same scoping.

---

## 4. Other useful features to fold in (backlog)
- **Propose → validate → accept** loop everywhere (already the project's trust model):
  never apply blind; always show the replay/confidence first.
- **Holdout cross-validation** for an honest (out-of-sample) confidence number, not just
  in-sample reproduction.
- **Diameter/size rules** once the enriched extract (`diaMm`) is available — the analyzer's
  diameter-split section is already wired.
- **BCF / viewpoint export** of unresolved or high-priority clashes for issue tracking.
- **Priority scoring** (penetration depth, hard/soft, system criticality, cluster size) —
  mostly deterministic, AI for the fuzzy parts.
- **Grouping defaults** surfaced (Grid recommended) incl. the singleton-grouping behaviour
  so reports never have loose clashes.

---

## 5. Build sequence (suggested)
1. **Shared engine library** — lift `Models` + the processing/approve/analyzer logic into
   a `ClashRuleEngine.Core` class library; addin + EXE both reference it.
2. **C# analyzer port** — translate `analyze_clashes.py` into Core (clean, logic is final).
3. **EXE shell** — extend NwdClashLearner: Ingest&Learn + Rules + Apply (MVP: learn →
   review confidence → apply to global config).
4. **Approvals tab** (strict/lenient sliders + live preview).
5. **Trade hierarchy view.**
6. **Dashboard integration** (bring in the HTML generator; wire SessionExport → report).
7. **AI handoff wiring**, then the backlog (4).

MVP = steps 1–3 (you can already learn-and-apply without touching Navisworks manually).
Everything after is the "control center" layer that makes it the be-all-end-all.

---

## 6. Open questions
- WinForms (keep NwdClashLearner) vs re-shell in WPF for a richer dashboard UI?
- Where does the HTML dashboard generator live, and is it JS or C#-emitted?
- Merge vs replace on Apply — do managers ever want to keep hand edits across a re-learn?
  (Default: replace + versioned backups; revisit if they ask for merge.)
- Multi-project: one global config today. Do BIM managers need per-project rule sets
  (a project picker) once this is a product?
- Per-trade reports (§3.7): email transport — SMTP (needs server creds) vs a draft handed to
  the default mail client (no creds, manager hits send)? Where does the drafter directory
  (name + email per trade per project) live — the global config, or a per-project contacts file?

See also: [[product-roadmap-decisions]], [[analyzer-and-global-persistence]] in the
session memory, and `CLAUDE.md` → "Next to build".
