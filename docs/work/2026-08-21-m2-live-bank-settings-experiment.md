# M2 retrieval settings applied to the live bank (observation experiment)

Date: 2026-08-21 (~17:00 CEST)
Task: review-results (follow-up to `2026-08-22-retrieval-tune-no-regression.md`)

## What changed

The M2 config from the regression-elimination report was applied to the live bank's
settings table (bank-wide settings, **not** shipped defaults — the code's canonical
constants are untouched; ADR-0056's gate on default changes does not apply):

```
ai-raccoon settings retrieval rrfk set 30          # was default 60
ai-raccoon settings retrieval fts-weight set 3     # was default 1
ai-raccoon settings retrieval vector-weight set 2  # was default 1
```

The other six M2 knobs (sourceLambda 0.1, consolidationThreshold 0.1,
docScoreFormula max, candidateWindow max3x100, structureAlpha 0.5) already matched
the live values and stay untouched.

## Deliberate deviation from the measured M2

`fusion.noRegression.enabled.global` stays **true** on the live bank, although M2 was
measured with it false. The fusion flag is its own running live-telemetry experiment
(ADR-0078; `2026-08-17-fusion-flag-on-the-real-bank.md`) and disabling it would end
that data collection. Its per-search `search.fusion.*` metrics rows (joined to
`search_quality` by correlation id) keep the two experiments attributable: a search
the fusion reorder touched is marked as such.

## How to judge

Compare `search_quality` grades and the retrieval metrics for windows before/after
2026-08-21 17:00 CEST. The eval-set expectation from the report: mean nDCG@5
+0.05, hit@3 +0.07, with a known knob-tuning floor of ~5 re-ranked queries
(FTS-leg winners give way to vector-leg winners). The live bank differs from the
22,511-entry study copy, so treat the report's numbers as direction, not prediction.

## Revert

There is no `unset` verb (verified against 1.28.1 — the setting subcommands are
`set`/`show` only), so revert by setting the defaults back:

```
ai-raccoon settings retrieval rrfk set 60
ai-raccoon settings retrieval fts-weight set 1
ai-raccoon settings retrieval vector-weight set 1
```

`settings retrieval show-all` will then read the default values with source
`(setting)` — same as `structureAlpha` today, harmless.
