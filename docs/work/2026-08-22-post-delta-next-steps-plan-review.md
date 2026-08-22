# Review record — post-delta next-steps plan (MoE, 2026-08-22)

Two independent opus lanes reviewed rev 1 of `2026-08-22-post-delta-next-steps-plan.md`;
rev 2 folds every finding. Peer input from #405 and ai-raccoon-cc (cross-session, same day) is
folded where cited in the plan.

## Lane 1 — architect (structure): REQUEST-CHANGES → all findings folded

| # | Finding (severity) | Disposition in rev 2 |
|---|---|---|
| 1 | **BLOCKER** — S6 rewrite scheduled across S8's open branch; merging a pre-rewrite branch resurrects the PII blob or destroys work | S6b hard precondition: no pre-rewrite branch open (#436/#437/#440 merged or re-planted); sequencing fixed; re-plant + identical-diff check in acceptance |
| 2 | HIGH — S1's docs half gated on G1 but the release doesn't wait for the gate | Compat note ungated (documents shipped behavior); only the named-refusal escalation stays on G1-CHANGE; README ownership named (#405) |
| 3 | HIGH — S7 had no acceptance; negative result indistinguishable from a blind harness | Full acceptance added: 1.29.0 binary by SHA, delay grid ×≥20, positive control required before any negative claim, named artifact |
| 4 | HIGH — S4's rule predicate could not produce its own RED; moves not mechanical | Predicate re-stated (Infrastructure-declared, host-referenced, no Infrastructure-only types in signature); RED set pre-declared and must match exactly; `PromotionQueueOrphanReport` extraction in scope; drop-the-rule fallback |
| 5 | HIGH — #422 (code-mem D1, APPROVED) missing; #435 asserted closed while open | S9 added for #422; #435 corrected to open-with-negative-condition, S5 posts evidence and recommends closure |
| 6 | MEDIUM — arch F2 and QA F3 silently dropped from the carried table | Both restored as S5 rows |
| 7 | MEDIUM — S5 gated yet "anytime"; undecidable parking conditions | S5 ungated; quiet-window defined; H16 given owner + review date |
| 8 | MEDIUM — diagram encoded priority as dependency; hid the single-session bottleneck | Split into hard constraints + d6's ordered queue; delegated lanes marked |
| 9 | MEDIUM — S6's `git log --follow` acceptance is success-shaped | Object-level check on a fresh clone, RED recorded first |
| 10 | MEDIUM — "status notes" unnamed; S2 "three constraints" vs four bullets | Artifact named (`.ai-badger/status-notes.json` § postDelta); S2 says all four |
| 11 | LOW — M10 already resolved; DEFER paths undefined | M10 closed citing README:37; every gated step has a DEFER line |

## Lane 2 — code-reviewer (facts): FACTS-CORRECTIONS-REQUIRED → all folded

Confirmed against source: sync framing + TOFU watermark, old-client loud failure, mcp-token
(bank-not-project, 0600 POSIX-only), `vec_code` project partitioning, H21 duplicate members
(named), all four #436 design premises, `allProjects` built with no access check on the null
branch, eleven delta PRs merged, gate 8/8, four OQ5 repos.

| # | Correction (severity) | Disposition in rev 2 |
|---|---|---|
| F-1 | HIGH — #435 is OPEN under P3's conditional, not "closed as not-a-defect" | Corrected; S5 posts cc's negative-condition evidence (cosine 1.0) and recommends closure |
| F-2 | HIGH — S6 omitted #414's immediate half; fixture still tracked + used by 5+ tests | S6a added with the file list; S6b cannot run before it |
| F-3 | MEDIUM — #440's What's-new is three bullets; #423 unlinked; nine delta PRs uncovered | In-flight table corrected; S1 carries the coverage note to #405 |
| F-4 | MEDIUM — H1 not blocked; question 7 answered in ADR-0058 (2026-08-15) | H1 re-parked as plannable with M5; flag F3 withdrawn; gate card corrected |
| F-5 | MEDIUM — S1 targeted a nonexistent how-to page | Retargeted to `agent-memory-server.md` + `architecture.md`; no page created |
| F-6 | MEDIUM — compat sentence over-stated: framing is encrypted-banks-only | Sentence corrected in plan and gate card |
| F-7 | MEDIUM — F2 drift guard missed `architecture.md:535-590` | Fourth artifact added |
| F-8 | MEDIUM — H20 "WorkspaceService ports" is one port; no Core references at all | Inventory corrected (three ports, named, with lines); predicate re-stated (see lane-1 #4) |
| F-9 | LOW — #440's checklist JSON is an unfilled template | Noted in the in-flight table: no in-repo evidence of the 1.31.0 pass yet |
| F-10 | LOW — mechanism wording (keyed open, not magic string), 0600 POSIX qualifier, base omitted #439 | All corrected at source |

## Unverifiable (recorded as such, not as fact)

cc's "~8 clean lifecycles all waited 6–10 s" and pid list; #405's broadcast promise; the gate
watch state. These stay attributed to their sessions in the plan, not asserted from the repo.

## Verdict after fold

Both lanes' required changes are in rev 2. Remaining owner inputs: the G1–G6 gate verdicts
(`2026-08-22-delta-open-items-review.html`).
