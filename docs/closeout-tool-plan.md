# Clash Close-out Tool (plan)

> Status: PLAN captured 2026-06-18. Not built yet.
> Goal: trades email us "approve this clash" / "reassign this to X" — let a BIM manager
> drop in a **close-out file** (AI-generated from those emails) and have the addin apply
> every action to Clash Detective, with a preview and an audit trail.

## 1. Why this is mostly already supported
The risky part — writing back into Clash Detective without corrupting it — is **done**.
- `ClashProcessingService.WriteBack` already sets **Status / AssignedTo / Description**
  (and ApprovedBy/ApprovedTime) on detached `ClashResult` copies inside the SDK-supported
  transaction (see CLAUDE.md quirk #0). Approve / reassign / comment are the SAME ops the
  engine already performs.
- Clashes are already **resolved by GUID** (`ClashNavigationService` / `TestsData.ResolveGuid`)
  — stable identity for "this exact clash".
- `SessionExportService` already exports the per-clash list (name, GUID, grid, level, both
  elements' properties, status, assignee) — that export is what the AI matches emails against.

So the close-out tool = a **new input** (explicit per-clash actions) into the **existing**
write-back, plus an importer/preview UI and the AI email→file step.

## 2. Workflow
```
 trade emails ──► AI (Claude) + current clash export ──► close-out file (JSON)
                                                       └► addin: Import ──► PREVIEW ──► Apply
                                                                                     └► write-back + audit stamp
```
1. Export the current clashes (existing `SessionExportService`) — gives the AI the ground
   truth to match against (so "the chilled-water vs duct clash at H-22" → a real GUID).
2. AI reads the emails + that export → emits a `clashre-closeout/1` file of actions.
3. Manager imports it in the panel → **preview** (every action + the clash it targets) →
   **Apply** → existing write-back applies Status/Assignee/Description in one transaction.

## 3. Close-out file schema (`clashre-closeout/1`)
```json
{
  "schema": "clashre-closeout/1",
  "source": "trade emails 2026-06-18",
  "actions": [
    {"test": "_HYD vs _MECH", "guid": "…", "clash": "Clash123",
     "action": "approve", "comment": "approved per ABC Mech email 18/6"},
    {"test": "_ELEC vs _FIRE", "clash": "Clash124",
     "action": "assign", "assignee": "FIRE", "comment": "Sparkies say fire to move"},
    {"test": "_ICT vs _MECH", "guid": "…",
     "action": "status", "status": "Resolved"},
    {"clash": "Clash125", "action": "comment", "comment": "awaiting RFI 042"}
  ]
}
```
- **Actions:** `approve`, `assign` (→ trade/person), `status` (Active/Reviewed/Approved/
  Resolved), `comment` (append to Description). Extensible (e.g. `group`, `unapprove`).
- Every action SHOULD carry a `comment` (the email reference) for the audit trail.

## 4. Clash matching (email prose → a specific ClashResult)
Match in priority order, so exactness wins and ambiguity is surfaced, not guessed:
1. **GUID** — exact (preferred; the AI copies it from the export).
2. **test + clash name** — exact (Clash Detective names like "Clash123").
3. **Fuzzy** — grid + level + element descriptors vs the export (AI's job, with a
   confidence score). Low-confidence matches are flagged, never auto-applied.
Unmatched / ambiguous actions go to a **review list**, not the document.

## 5. Safety & audit (non-negotiable for an email-driven tool)
- **Preview before apply:** "14 actions — 9 approve, 4 reassign, 1 resolve. Review each →
  Apply." Show the target clash + before/after for every action.
- **Provenance stamp:** write the email reference into the clash Description/Comment
  ("Approved per ABC Mech email 2026-06-18") so the report shows WHY it changed.
- **Idempotent / re-runnable;** applying twice is a no-op.
- **Unmatched report** the manager can fix or send back to the AI.
- Honour existing guards (e.g. don't silently approve a structure clash unless explicit).

## 6. The AI step (email → file)
- MVP: **external** — manager pastes the emails + the clash export into Claude with a fixed
  prompt; gets the `clashre-closeout/1` file; imports it. Zero new addin code beyond the
  importer.
- Wired in later: a **"Close-out from emails"** panel action using the existing
  `ClaudeApiService` / `AiRuleGenerator` raw-HTTP path — paste/drag `.eml`/`.msg` or text,
  it calls Claude with the export as context, returns the file ready to preview.

## 7. Build sequence
1. **Importer + apply** for a hand-written `clashre-closeout/1` file, reusing `WriteBack`
   (approve/assign/status/comment). Match by GUID + name first.
2. **Preview/confirm UI** + unmatched report + provenance stamping.
3. **Fuzzy matching** against the session export (grid/level/elements).
4. **AI email→file** wired into the panel (existing Claude path).

MVP = step 1 (a file → applied changes), which is small because WriteBack already exists.

## 8. Open questions
- Email input: paste text, or ingest `.eml`/`.msg`/Outlook export directly?
- Do we need per-trade auth ("only Mech can approve Mech clashes")? Probably out of scope.
- Should an approve from a close-out file also move the clash into the approved grid
  sibling group (consistency with the Grid grouping), or leave grouping untouched?
- Two-way: also EXPORT a per-trade "your open clashes" list to send out (closes the loop)?

See also: [docs/combined-exe-plan.md](combined-exe-plan.md) (this could be a module of the
Control Center), and CLAUDE.md → "Next to build".
