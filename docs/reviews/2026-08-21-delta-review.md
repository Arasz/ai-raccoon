# Delta project-scope review — integrated record (Phase 2)

Date: 2026-08-21 · Base: `155f281e` · Delta: `1d1889d5..155f281e` (168 commits) ·
Ground truth: [`2026-08-21-delta-review-ground-truth.md`](2026-08-21-delta-review-ground-truth.md) ·
Lane reports, verbatim: [`lanes/2026-08-21-delta-*.md`](lanes/) ·
Owner rulings: `docs/work/2026-08-21-delta-review-owner-rulings.md` (canonical tree)

Six lanes (architecture, retrieval/embedding, security, data-access, test-QA, consumer-surface):
**55 findings** — 3 HIGH, 12 MEDIUM, 12 LOW, rest NIT/positive-verification. Every finding that
drives expensive work was re-verified by the orchestrator at `path:line` before entering here.

## Verdict

**The delta is in markedly better shape than the codebase the last campaign reviewed: all three
blockers from 0814 are fixed and verified fixed, and the new code's defects concentrate in one
theme — the arbitrary-model supply chain, which is loaded but has never fired.**

What the delta fixed (orchestrator-verified, prior-campaign IDs):
- **B1** cross-project delete → `ContextScope.RequireWithinProject` gates `DeleteContextAsync`
  (`SqliteMemoryStore.cs:288`), ADR-0051, README SECURITY line.
- **B3/H24** write/ingest chunking → `ChunkToBudgetAsync` on the write path (ADR-0064),
  engine-aware budget (ADR-0036 invariant holds on all paths — retrieval F9), `ChunkBackfill`
  re-chunks oversized rows (ADR-0069). Live bank: 0 rows over 20k chars, avg 830.
- **H6** shared-write bypass → naming `shared` requests promotion (ADR-0067).
- **H2/H3** vacuous/in-sample gates → `[0,1]` assertions deleted; held-out floors pinned with an
  executable reversal-discrimination proof (QA F6 verified it passes live).
- **H12, H13/H26, H14, H17, H19, H22, H23, H8-partial** — all fixed and verified (see ground
  truth). Nightly silence fixed via triage script + issue filing.

## Blockers (new)

| # | Finding | Grade | Lane |
|---|---|---|---|
| B1' | **Manifest sha256 pins are never re-verified; a swapped weights file is undetectable** — fingerprint hashes only manifest bytes; loader checks `File.Exists` only; the loader's own comment claims tampering is detected, which is false for content swaps. Two lanes converged. | READ ×2, orchestrator-verified | retrieval F1 + security F3/F4 |

## High

| # | Finding | Grade | Lane |
|---|---|---|---|
| H-A | **Auto-start dead on unpackaged invocation** — `Environment.ProcessPath` returns the dotnet driver; child is `dotnet --flags serve`; stderr drained so the operator sees only a 30s timeout, EXIT=18. All server-mediated CLI verbs dead under `dotnet run`. Live-probed. | MEASURED | surface F2 |
| H-B | Sync whole-bank upload + remote blob trusted as SQLite (H9/H10 carried, unchanged). | READ | security F5 |
| H-C | Access mode resolves from the caller-named project (H7 carried, unchanged). | MEASURED | security F7 |

## Medium (condensed; full detail in lane reports)

- **M1** Provenance config files (dims/pooling/normalization) download with TLS as the only integrity control (security F2). Owner ruled: pin them (option B).
- **M2** Reconcile runs only in the drain; commit→reconcile window fails writes loud (~15s, unbounded if `model set` ran serverless). Owner ruled: reconcile at open, cheap check, server-only, HARD INVARIANT — perf question to answer in the plan.
- **M3** Digest stamped before the version ladder; crash window leaves the cheap path trusting a stale-schema bank (data F1). Owner ruled: stamp last.
- **M4** `HasWorkAsync`/ledger read outside the per-job guard — one throw aborts the whole pass (data F2). Owner ruled: guard them.
- **M5** Out-of-sample control is thin: 3 pinned queries on a different corpus; eval-set-100 is one family, 3 queries/doc, no internal split (retrieval F3/F4).
- **M6** Nightly-only quality gates; nightly.yml documents its own silent-drop risk (QA F1). Owner ruled: add workflow_dispatch PR leg.
- **M7** `StepUntilAsync` bounds iterations, not awaits — one blocked call hangs the testhost indefinitely; observed twice this session (QA F2). Owner ruled: wall-clock-linked token.
- **M8** `promotion_list` without `projectId` skips the gate (H8 residue). Owner ruled: require read-all mode.
- **M9** Server-side 5xx exits 15 ("you mistyped") (surface F3). Owner ruled: own exit code.
- **M10** README What's new missing the 1.29.0 feature (surface F1).
- **M11** jsaa-memory.db PII in history. Owner ruled: remove, file issue now, rewrite on a calm machine.

## Low/NIT highlights (see lanes)

Legacy `ConfigureAsync` on the port fails broken (owner: remove — D4). `FinishModelMigration` not lease-guarded (mild). Metrics retention DELETE unbounded. SchemaDoctor not snapshot-consistent. doctor exits 0 on missing bank (owner: fix — C3). Consolidation can serve < limit. FusionDiff all-dropped edge. Two always-pass report Facts. Golden-vector tripwire arch-scoped. Timeout margins thinned. Architecture: two ports in Infrastructure, RRF/affinity outside Core, IMemoryStore 27 members + 4 duplicate settings members, silent narrow-ctor path.

## Disconfirmed this campaign (leads tested and found wrong)

- `--endpoint` CLI override exists → it doesn't; DI seam only (security F1, surface F5).
- Settings endpoints hold business logic → none; validation + transport only (arch F4).
- Embedding orchestration leaks into Core → it doesn't (arch F5).
- Sextant corpus is the chunking adjudicator → it's a probe; adjudication uses vendored real docs (retrieval F5).
- MeasurementBuffer lost-update → none (data F9).
- Maintenance ordering untested → it is tested, same-pass pin (QA F9).
- "Spawned server holds the port" hang mechanism → this class is in-process (QA F2).
- Blanket skip-honesty → one documented permanent `[Fact(Skip)]` counterexample (QA F5).
- Surface lane's own first noise-500 observation → self-withdrawn, lane's artifact (surface Still-open).

## What is healthy — verified

Layering held through 10.9k new lines: Core still zero project references, engine orchestration entirely Infrastructure-side (arch F5), settings endpoints thin (arch F4). Ranking math verified correct end to end (retrieval F10). Held-out partition genuinely document-disjoint (retrieval F2, measured). ADR-0036 budget invariant holds on every path (retrieval F9). Crash-mid-drain tested with a real process kill inside the observed window (QA F4). Fake fidelity meets the SHA256-derived standard (QA F8). ~28 new SQL statements: zero dropped-parameter mismatches (data F8). Migration ladder append-only (data F10). Exit-code contract live-verified (surface F10). server.json now single-sourced from VERSION. Live bank: schema current at v10, all rows embedded, maintenance ledger cycling, metrics flowing, 0 oversized rows.

## Owner rulings (14/14 APPROVE — full notes in the result file)

D1 activation re-verifies pins · D2 pin provenance files (option B) · D3 reconcile at open (cheap check, server-only, HARD INVARIANT — perf answer owed in plan) · D4 remove legacy configure from port · D5 stamp digest last · D6 guard HasWorkAsync · S1 gate unscoped promotion_list · S2 sync authenticity (approved) · S3 remove jsaa db from history — **file the issue now, rewrite on a calm machine** · Q1 wall-clock StepUntilAsync · Q2 workflow_dispatch PR leg for Nightly gates · C1 auto-start actionable failure · C2 own exit code for 5xx · C3 doctor distinguishes no-bank.

## Still open

- D3's performance question (dim check at open) — to be answered in the plan with a measured cost.
- Whether the global-tool path ever hits H-A (if not, it downgrades to dev-only severity).
- Whether any Nightly workflow run has actually executed recently (`gh run list --workflow=nightly.yml`).
- Mechanism of the full-suite seed-embed slowdown (QA F3).
- H1 ranking-field semantics unchanged (carried, owner-question 7 from 0814 still unanswered).
- Leave-one-family-out on RRF parameters (carried from 0814 — still not run; eval-100's single-family shape makes it moot for that corpus, per retrieval F4).
