# 0096. Default search shape: limit 8, relative floor 0.6

## Status

Accepted 2026-09-03.

## Context

The `memory_search` default path (`limit=20`, `minRelativeScore=0`, `kind=both`)
returns up to 40 unfiltered snippets per call. Measured on the live bank
(project `cfe47dab`, query `source affinity ranking RRF fused results`,
`docs/work/2026-09-03-search-output-shape.md` follow-up): 20 memory hits scoring
1.0 → 0.76, the two on-topic ADRs at ranks 3–4, ranks 1–2 cross-project keyword
matches, ranks 5–20 tail noise — ~6.5 KB wire, ~1,600 tokens, for two good hits.
The same query at `limit=6, minRelativeScore=0.9` kept both ADRs at ~2 KB.

ADR-0047 deliberately shipped the floor default-off: a relative floor "must not
silently truncate a caller's requested limit", because rank 1 is always 1.0 even
when nothing matches. That reasoning stands for explicit calls; it misfires for
the default path, where no caller requested anything and the 20-hit dump is read
as relevance. The noise cost is now measured, not hypothetical.

## Decision

1. `SearchQuery` defaults become `Limit = 8`, `MinRelativeScore = 0.6`, held in a
   dedicated `SearchDefaults` static class that both the record signature and
   `MemoryTools.Search` bind to — a sibling const on the record itself is not
   referenceable from a primary-constructor default (CS0103), so the external
   class is what keeps the single place of truth literal-free.
2. The code leg inherits both through the existing `codeLimit ?? Limit` /
   `codeMinRelativeScore ?? MinRelativeScore` fallthrough (ADR-0088 §3.6):
   worst case is now 8+8 hits, ~4 KB.
3. Full recall stays one explicit call away: `limit` + `minRelativeScore=0`
   per call, unchanged semantics. Bank `retrieval.*` tuning is untouched.
4. Amends ADR-0047's default-off rule (explicit-call reasoning preserved) and
   the `memory_search` wire table (`limit=8`, `minRelativeScore=0.6`).

## Consequences

- **Positive**: default responses shrink ~70% in volume and tokens; the floor
  removes the weak tail that relative scoring cannot rank away (the top hit is
  always 1.0, so only a floor or a cap can).
- **Negative**: the floor cannot demote a bad top hit — ranks 1–2 of the probe
  were weak matches scoring 1.0/0.98 and survive any relative floor. A floor
  above ~0.8 also risks amputating good second-cluster answers on hard queries;
  0.6 keeps everything within shouting distance of the best hit.
- **Not addressed**: the `search_quality` row still stores no `limit` (cap vs
  exact stays ambiguous) and no `kind` (ADR-0094's noted repair) — unchanged.
- Validation owed: one probe query is thin evidence for a default. The
  `search_quality` log (every kind records since ADR-0094) is the corpus to
  re-validate 8/0.6 against: result-count distribution and follow-through rate
  before vs after rollout.
