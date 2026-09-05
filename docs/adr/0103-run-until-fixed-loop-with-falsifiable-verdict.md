# ADR-0103: one repair invocation runs until the bank is fixed, then says so falsifiably

**Status:** accepted · **Date:** 2026-09-05 · **Task:** `air-run-once-repair-fully-converges`

## Context

`repair project-ids --apply` was fire-and-forget: one request, one poll, and
the operator re-ran blindly until the report *looked* quiet — with no definition
of quiet, no distinction between "writers still active" and "repair stuck", and
a closing line that never closed with a verdict. Combined with the pre-0100
planner/applier mismatch, `--apply` loops were proven exhausted on the live bank.

## Decision

- `--apply` is now derive → commit-request → poll (~15 s maintenance) → reap →
  re-derive until done, bounded at **10 passes / 10 min backstop** (one poll per
  pass; stuck-abort normally fires first). `--queue-only` preserves
  fire-and-forget for scripts. The loop re-derives first: a pinned-only plan
  reports without committing a blind `repair_requests` row.
- Stop classes, all measured from per-pass moved-counts + census totals, never
  guessed: **converged** | **pinned-only** | **stuck** (identical actionable set
  across 2 passes, zero moved, no growth → abort with diagnosis) |
  **writers-active** (rows move but totals grow → quiesce guidance, loop to
  bound). Zero-moved *with* growth reads as writers-active, never false-stuck.
- **Fully fixed**, formally (D6): live re-derive yields zero
  folds/drops/retires/unresolved; every remaining non-canonical row sits in a
  pinned bucket with a reason; two consecutive derives agree with zero moves;
  P3 armed (durable map persisted + probe write refused-or-folded). Summary
  grammar: `converged|pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved,
  N pinned (reasons…), P3 armed` — superseding the 1.40.2 closing lines.
- CLI stays read-and-request-only (ADR-0075; asserted by construction: the loop
  takes only `IRepairStore`).

## Consequences

**+** One invocation converges a quiesced bank; a live-writer bank reports
  writers-active with quiesce guidance instead of a false converged.
**+** The verdict is falsifiable: every clause has a red-proofed test,
  including the full-stack one-invocation convergence E2E.
**−** Wall clock up to ~3 min nominal (10 polls) on a large bank; 10
  `repair_requests` rows max per invocation.
**Neutral:** receipts per pass; EventId 711 per requested run.

## Alternatives considered

- Unbounded loop (rejected: a stuck repair must abort with a diagnosis, not
  spin; bounds are the backstop, the stuck rule is the trigger).
- Daemon-side auto-repair without CLI (rejected: folds/drops need a human
  `--apply`; the outbox stays the consent boundary).
- Exact-count winner asserts in BDD (rejected: dedup-collapse and re-ingest
  replacement make counts move legitimately — key-set union + per-key survival
  are the honest invariants).
