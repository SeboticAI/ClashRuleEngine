# Competitive note — ClashWise AI

> Captured 2026-06-18. Source: the **Autodesk App Store listing** for ClashWiseAI
> ([apps.autodesk.com](https://apps.autodesk.com/NAVIS/en/Detail/Index?id=2998997804481884389))
> and a web search. Their own site (clashwise.ai) is a client-rendered JS app that was
> throwing a runtime error and could not be read — so this is THEIR MARKETING CLAIMS, not a
> verified look at the product. Do not over-index on it.

## What ClashWise appears to be
Navisworks-native (Manage 2023–2027, Win x64) AI clash-management tool, on the App Store, trial available.
- **AI clash titling** — descriptive names from real elements/levels/disciplines; 11 languages; 6 naming standards; group-clash aware; engine "runs locally or cloud-based".
- **Clash matrix + rules engine** — define discipline pairs, tolerances, ownership; **Auto Rule-Matching** routes new clashes to owner/priority once a rule is defined.
- **Cloud web portal** — live dashboards (open clashes by discipline/level/owner/age, weekly trend lines), shareable views.
- **Bi-directional sync** (status/owner/priority/comments) between Navisworks and their cloud.
- **Full audit trail** (timestamped, attributed, reversible). An **AI chat assistant** is advertised on the site (not in the App Store listing).
- Read-only to the federated geometry; GPU accel "on the roadmap".

## Overlap with our tool
Native Navisworks clash management, a rules engine (discipline-pair → owner/priority), a clash
matrix, assignment + prioritisation, dashboards/reporting, an audit trail, and local-or-cloud AI.

## Our differentiation (where we are genuinely different / stronger)
1. **Learned vs defined rules.** They are *define a discipline-pair rule → auto-route*. We **learn
   how the team actually assigns** from dozens of historical jobs, at **element/family level**
   (a switchboard vs a pipe inside ELEC-vs-HYD), and report **confidence** ("reproduces 82% of your
   calls"). "It learns your coordination" beats "configure rules and we route".
2. **Email coordination loop** — inbound **close-out** (trade emails → AI → preview → write
   approve/reassign back) and outbound **per-trade reports + reply loop**. Not evident in ClashWise.
3. **Calibrated approve engine** — learned per-pair clearance floors + strict↔lenient dial.

## Where they are ahead (be honest)
1. **Shipping** — App Store, trial, marketing, multi-version. We are pre-product.
2. **Cloud SaaS portal + bi-directional sync** already built (our dashboard is still planned).
3. **Polished titling** (11 languages / 6 standards).
4. **Local AND cloud AI already** → confirms local AI is viable AND table-stakes (parity, not a moat).

## Takeaways
- This is **validation, not a wall** — a funded competitor proves the market.
- Don't try to out-feature them on titling/dashboards. **Win on learned assignment intelligence +
  the confidence/trust layer + the email coordination loop** — the hard-to-copy combination.
- Headline = "it learns how *you* coordinate", not "AI clash tool" (now crowded).
- Study their App Store distribution / trial / pricing as the channel and bar to clear.

## IP / clean-room — are we at risk? (NOT legal advice)
Short version: **building a clash-coordination tool is not infringement; nobody owns the category.**
- **Copyright protects expression, not ideas/functionality.** You can't copyright clash detection,
  rules engines, matrices, grouping, approvals, or "assign clashes to trades" — these are decades of
  prior art (Navisworks itself, Solibri, BIMcollab, Newforma…). Copyright only stops you copying their
  **source code, exact text/marketing copy, icons/graphics, logo**. We wrote our own code from our own
  data → **independent creation is a complete defence to copyright.**
- **Trademark** protects the name/brand "ClashWise". Just don't use a confusingly similar name/logo.
  Ours (Clash Rule Engine / OConnors Clash / Clash Control Center) are distinct — fine.
- **Patents** are the only non-trivial risk: IF they (or anyone) hold a patent on a specific novel
  method and we implement that exact method. Our learned element-pair approach is our own, which
  lowers risk. Before commercial launch, do a quick **patent + trademark clearance search** (or have an
  IP attorney do one).
- **Trade secrets** — don't reverse-engineer their binary in breach of its EULA, don't scrape their
  proprietary data, don't poach their staff for internal know-how. We're independently developing, so OK.
- **Clean-room hygiene:** develop from our own knowledge and data; don't copy their text/UI; our git
  history already evidences independent development. **For real commercialisation, get an IP attorney
  to run a clearance/freedom-to-operate check** — cheap insurance, and this doc is not a substitute.
