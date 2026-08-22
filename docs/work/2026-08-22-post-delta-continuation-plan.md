# Continuation plan — parked post-delta items

**Date:** 2026-08-22 · **Commissioned by:** owner gate G4 (APPROVE, note: "Create a continuation
plan for them now, create issue that will reference this plan") · **Parent plan:**
`docs/work/2026-08-22-post-delta-next-steps-plan.md` (rev 2, MoE-reviewed) · **Verdicts:**
`docs/work/2026-08-22-delta-open-items-feedback.md` (6/6 APPROVE)

Each item below is parked with a named re-raise condition, or scheduled. This document is the
single ledger; the referencing GitHub issue tracks it. Items graduate from here into tracked
tasks — nothing leaves silently.

| Item | Status | Condition / trigger | Notes |
|---|---|---|---|
| **S2 — project-identity ADR (H-C/O2)** | **designed — ADR-0089 drafted in PR #448, awaiting owner ratification** | G2 APPROVE | Design: [`docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md`](../adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md), PR **#448**. Owner design input carried in verbatim: token = sortable **guidv7** (the id *is* the token); new tool `project_id_token_get` (owner named it `get-project-id-token`; renamed for the family-first convention); CLI `project id generate` / `project id convert` (one-way); threat model = accidental cross-project access only, same-machine projects trusted; old raw-text ids warn-but-work (a non-guid id the bank does **not** know is refused — the one place the ADR extends the owner's words, flagged for ratification); storage instructions + the file-stored id kept out of memory via `ai-raccoon.ignore` (**not** `.gitignore` — owner reversed this in the PR #448 review). Review round folded: `project_id_token_get` mints **and registers** into a new `projects` table (id / nullable name / created_at), a write to an unregistered id is refused guid-or-not, and `convert` registers with name = the old raw id by default; ADR-0046's membership definition is deliberately untouched (`SelectProjectIds` narrows to "projects with content" and is the legacy-grandfathering oracle). **Owner's open question answered in the ADR under its own heading:** yes, any holder of the bank's `mcp-token` can still enumerate every project, and encrypting the bank does not change that. Implementation is scoped separately — this row closes on ratification, not on merge. |
| **H20 — port placement** | scheduled | G4 APPROVE | One ArchUnitNET rule, predicate: public interface declared in Infrastructure, referenced from the host assembly, no Infrastructure-only types in signatures. Pre-declared RED set: `IWorkspaceService`, `IPromotionQueuePruneStore`, `IWatchRegisteredStore` — observed set must match exactly. Includes `PromotionQueueOrphanReport` DTO extraction. Fallback: three moves, no rule. |
| **Parity-gate wiring** (new, from #437) | scheduled | none — actionable now | `GraphPooledOutputParityTests` is Nightly-tagged but gated on `AIRACCOON_POOLING_PARITY_MODEL_DIR`, which nothing sets — it skips *everywhere*, including nightly: a gate in name only. Wire the variable into the `build-nightly-gates` leg and/or `nightly.yml` and record one run where the parity test actually executes (`prove-the-check-fails`). |
| **1.29.0 server-lifecycle failure** | parked per G6 note | **priority on a second observed occurrence** | Unexplained, not diagnosed (two hypotheses falsified and retracted). Negative repros on 1.30.x never exercised a **fast rebind** (all waited 6–10 s stop-to-bind) — that is the variable to sweep if it recurs. Tracked by its own issue. |
| **H21 — `IMemoryStore` decomposition** | parked | quiet window: no open PR touching `IMemoryStore`/`SqliteMemoryStore` in any session; re-evaluate at each release boundary | Duplication evidence: `IMemoryStore` re-declares `ISettingsStore`'s four settings members with no inheritance relation. #405 ranks it last. |
| **H16 — CI OS matrix** | parked | review at next release boundary | Owner: release-checklist owner. ubuntu-only today. |
| **H1 — `ranking` rank-derived; λ=0.1** | parked (unblocked) | next tuning round, together with M5 | 0814 question 7 **was answered** (ADR-0058, Accepted 2026-08-15) — no ruling outstanding. |
| **M5 — out-of-sample retrieval control** | parked | next tuning round | Reuse the four OQ5 repos (vscode, aspnetcore, deepseek-harness, semantica). cc's kept `bank-fixed` artifact + model (issue-435 thread) avoids re-paying the 2.5 h embed. |
| **arch F2 — RRF/affinity outside Core** | parked | rides H20's disposition | If the placement rule lands, extend or record why not. |
| **QA F3 — full-suite seed-embed slowdown** | parked | re-raise on next occurrence | Q1's wall-clock budget (PR #428) now makes an occurrence diagnosable instead of a hang. |
| **#435 — bge-m3 weak vector leg** | closure recommended | owner closes (or objects) on the posted evidence | P3's condition resolved negative: pooling hypothesis refuted (cosine 1.00000000), re-embed never spent; bge-m3 verified simply weaker on this corpus (−0.233 nDCG@5, p=0.001). Untested residual: re-chunking at the engine's own budget (an OQ5-eval experiment). |
| **#436 — code-corpus prune gap** | accepted by ai-raccoon-cc, not started | cc's next session, or reassign | Complete design brief in the parent plan §S8. |
| **#414 — jsaa-memory.db (G5 APPROVE)** | **S6a DONE (PR #450)**; S6b still owed | S6b: after no pre-rewrite branch remains open; owner pushes with the force-push hook deliberately lifted | S6a replaced the fixture with a public-docs corpus (ADR-0090): the private bank leaves HEAD, all 14 consumer classes keep running in CI, `Category=Retrieval` 74 passed / 3 skipped. Two follow-ups filed: #454 (AND-under-match anchor), #455 (benchmark corpus still carries jsaa prose). S6b is the only thing that reclaims the bytes and clears history. |

## Not carried here

- G1's escalation (named format-version refusal on pull): the owner approved the docs-only note;
  the escalation parks until the next sync-format change.
- G3's ratifications: recorded in `.ai-badger/status-notes.json` § `post-delta`; nothing to do.
- **Release/publish:** owner note on G1 — the compat note ships, but **no version publish for
  now**; #440's auto-cut release (if merged) waits at the production-approval gate.
