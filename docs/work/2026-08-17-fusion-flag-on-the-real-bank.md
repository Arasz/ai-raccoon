# The fusion flag, exercised on a copy of the real bank

Date: 2026-08-17

Evidence that the default-off no-fusion-regression reorder does what ADR-0078 claims, measured
against real content rather than a fixture. Companion to
`docs/work/2026-08-17-367-baseline-before-any-change.md`, which recorded the same query before any
change existed.

## Setup

A **copy** of the live bank (25,995 entries), taken with `VACUUM INTO` from a read-only connection —
not `cp`, because the bank runs in WAL mode and a file copy without its `-wal` sidecar silently drops
recent commits. The copy read 25,995 rows against 25,993 in the main file, so the WAL was in fact
carrying writes.

A second server was started on its own port against that copy, so nothing touched the live bank or
the server holding it:

```bash
ai-raccoon --data-root <copy-dir> serve --port 7799
```

**Both flags are required for an isolated bank.** `--data-root` alone leaves the CLI talking to the
default port, which fails with *"the settings server at http://127.0.0.1:7721/mcp refused this
credential — it may serve another data root"*. That message is accurate and worth keeping; the fix is
to pass `--port` too.

## The toggle

```
$ ai-raccoon --data-root <copy> --port 7799 settings retrieval fusion show
enabled: False  (default: False — off serves the baseline fusion)
$ … fusion enable   → retrieval fusion no-regression reorder enabled
$ … fusion show     → enabled: True  (default: False — …)
$ … fusion disable  → retrieval fusion no-regression reorder disabled
$ … fusion show     → enabled: False (default: False — …)
```

## The measurement

Query — a paraphrase that deliberately avoids the literal term `clipped`:

> the stored query text was truncated at the field cap so only a prefix was saved, not the whole query

Targets are the two chunks of `ai-badger/docs/retrieval.md` carrying the `| clipped | … |` row,
identified **by hash**, not by rank — ranks are what moves, so a target named by rank cannot be
followed across the change.

| leg | flag OFF | flag ON |
|---|---|---|
| hybrid — chunk 9/64 | 6 | **3** |
| hybrid — chunk 7/64 | 12 | **6** |
| FTS-only (`vectorWeight=0`) | 4 / 6 | 4 / 6 |
| vector-only (`ftsWeight=0`) | 14 | 14 |

Two things to read here.

**The rule now holds.** With the flag on, hybrid ranks the target **3** against FTS-only's **4** — the
hybrid is no longer worse than its best single leg, which is exactly what ADR-0006:49-50 declares and
what #367 found violated.

**The single-leg controls did not move.** That is what makes the change attributable to the fusion
rather than to something else in the pipeline. If FTS-only had shifted too, this table would prove
nothing about fusion.

Flag OFF reproduces the pre-change baseline exactly (hybrid 6, FTS 4, vector 14), so the copy behaves
identically to the live bank and the reorder is genuinely inert when disabled.

## The telemetry

Three rows written to the existing `metrics` table on the flag-enabled search, none on the default
path:

| name | value | unit |
|---|---|---|
| `search.fusion.top1_changed` | 1 | flag |
| `search.fusion.top1_rank_delta` | 1 | ranks |
| `search.fusion.top5_moved` | 4 | results |

All three carry the same `correlation_id`, which joins to `search_quality` — so a period of enabled
running can be scored against the usefulness grades it actually produced. That join is the whole
argument for shipping this on rather than deciding it offline: ADR-0072 refused a change because a
three-query held-out set cannot adjudicate one, and this is the surface that can.

## What this does NOT establish

**One query is not an evaluation.** This shows the mechanism works, the flag is inert when off, the
controls are unmoved, and the telemetry lands. It does not show the rule improves retrieval in
general, and no nDCG figure is quoted for it — publishing one off the 44-query catalogue is the exact
error ADR-0056 measured, where out-of-sample scored 42% of the published figure.

The verdict comes from accumulated telemetry, not from this table.

## Still open

- No case yet where the flag changes the served **top-1** on real content; here it promoted the
  target from 6 to 3, behind a consensus row. With two legs, up to two results can claim rank 1, so
  the changed-top-1 path is covered at unit level instead.
- `SourceAffinityRanker` at the shipped λ=0.1 can override the reorder — one adjacent sibling is
  worth roughly 7 rank positions. How often that happens on real traffic is unmeasured, and is
  precisely what `top1_changed` over a longer window would answer.
