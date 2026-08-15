# 0072. A term budget for long queries is not adjudicable, and does not ship

Date: 2026-08-15

Status: Accepted

Records a change **specified, measured and not shipped**. No production code changes.
Relates to ADR-0071 (the query-trim record), which bounds the *vector* leg; this record
is about the *keyword* leg and deliberately does not copy that bound.

## The framing this record exists to preserve

**The best search quality is for queries that fit within the limit.** Anything discussed
here is a floor under a degraded case, not a feature that makes long queries work. A
short, specific query beats a long one. Nothing in this record should be read as "long
queries are now supported" — the measurements below say the opposite even in the best
case tried.

## Context

`FtsQueryNormalizer.BuildPlan` has no cap on token count. Queries over 4 content tokens
take the plain-OR path, which joins `rawTokens` — **duplicates included**:

    return new FtsQueryPlan(string.Join(" OR ", rawTokens), null, tokens.Count);

`plan.Fallback` is null on that path, so `TokenCount` and the under-match retry play no
part; the only thing this path decides is which terms reach `MATCH`.

### The defect is real, and its mechanism is measured

On a scratch FTS5 database, repeating a term in an OR expression multiplies its `bm25()`
contribution **linearly** and **reorders results**. Three copies of one term:

| row | matches | `bm25` once | `bm25` ×3 |
|---|---|---|---|
| 1 | zebra only | −1.2238 | **−3.6713** (exactly 3×) |
| 2 | raccoon only | −1.2238 | −1.2238 (unchanged) |
| 3 | both | −2.4476 | −4.8951 |

Rows 1 and 2 **tie** when each term appears once; row 1 **wins decisively** when one term
is tripled. So the query's term-frequency profile becomes a ranking weight.

For pasted machine output that profile is boilerplate frequency, not relevance. Measured
on the live bank (478 recorded queries):

- Median query: **61 characters, 8 raw tokens, 8 distinct**.
- Largest: **448,900 characters → 49,267 raw tokens, 3,847 distinct** — 92.2% of the OR
  terms are duplicates.
- Its top ten terms hold **25.9% of all term slots**: `8`×1740, `utf`×1589,
  `encoding`×1587, `tmp_path`×1345, `n`×1317.
- **75.7% of its 49,464 term slots are terms that exist in the gate corpus vocabulary**,
  so roughly **37,429 noise term-slots** carry weight against the question's ~7.

FTS5 accepts a 50,000-term OR without error (probed at 100/1k/5k/10k/20k/50k terms), so
there is **no crash to prevent**. This is a quality-and-cost problem only.

## What was measured

The FTS leg was scored in isolation — it is the only leg any of these options touch, and
the vector leg is bounded separately at 254 WordPiece tokens (ADR-0071). Real pasted
content taken verbatim from the live bank's own long queries was appended to each
gradeable catalog query, keeping the expected document that was pinned before this work
existed. nDCG@5, `k=5`, against `tests/AiRaccoon.Tests/Resources/jsaa-memory.db`.

FTS-leg-only ceilings with no noise at all: tuning **0.1139**, held-out **0.0924**. These
are not comparable to the gate's end-to-end figures — this measures one leg.

### Result 1 — any substantial paste annihilates the keyword leg

`current` scored **0.0000 on every query, at every noise size, in both orderings**. Not
degraded: zero. That is the finding worth keeping from this work.

### Result 2 — deduplication alone does essentially nothing

Held-out mean nDCG@5, 2,255-character noise: **0.0000 → 0.1200**. At 4,269 characters:
**0.0000 → 0.0000**. Across 38 query/noise pairs at those two sizes, dedup was better in
**1**, worse in **0**, unchanged in **37**. Deduplication is recall-neutral by
construction (verified: the matched row set is identical, since OR is idempotent for
matching), and it removes a weighting nobody intended — but on the only corpus carrying
relevance judgements its measured effect is nil.

### Result 3 — a cap helps only when the question comes first, and real queries do not

Sweeping the cap with dedup, tuning / held-out means:

| cap | small noise, question **first** | small noise, question **last** |
|---|---|---|
| 8 | 0.2258 / **0.3919** | 0.0000 / 0.0000 |
| 12 | 0.2150 / 0.0565 | 0.0000 / 0.0000 |
| 16 | 0.1528 / 0.0437 | 0.0000 / 0.0000 |
| 32 | 0.0195 / 0.1617 | 0.0000 / 0.0000 |
| 48 | 0.0468 / 0.2409 | 0.0000 / 0.0000 |
| 64 | 0.0327 / 0.1765 | 0.0000 / 0.0000 |
| 512 | 0.0000 / 0.1200 | 0.0000 / 0.1200 |
| none | 0.0000 / 0.1200 | 0.0000 / 0.1200 |

A small cap is the only thing that recovers anything — and **every one of those gains
disappears when the question is not first**. The briefing for this work stated that in
long real queries "the real question is in the first line". **That is not what the live
bank holds.** Of the twelve longest real queries, the first line is:

- `'continue based on the last commit @git:1 - find what session was working on...'` — a question
- `'"value": "1.1 varnish, 1.1 varnish"'` — a JSON fragment
- `'},'` — punctuation
- `'[ASYNC DELEGATION BATCH COMPLETE — deleg_32b6db8f]'` (and two more like it) — a machine banner
- `'adjust-tests'`, `'log-leak'`, `'v1.8.2 log errors:'` — labels, with the body below

Roughly half carry no question in the first line. A first-N cap therefore keeps JSON
punctuation and drops the intent, in exactly the cases it exists to serve.

### Result 4 — no cap number survives the held-out set

The held-out column is **three queries** (A8, A9, A10). Across the sweep it moves
0.3919 → 0.0565 → 0.0437 → 0.1151 → 0.1617 → 0.2409 → 0.1765 → 0.1200 with no monotone
trend and no stable optimum. Choosing a cap from that is fitting noise on n=3 — the same
condition that stopped ADR-0058, and the reason its number was not taken either.

## Decision

**Nothing ships.** `FtsQueryNormalizer` is unchanged.

Each option was rejected for its own reason, recorded here so that a reader who cannot
see what lost does not propose it again:

- **First-N raw tokens** — rejected. Its premise, that the question leads the query, is
  false for about half of the real long queries measured above.
- **N most selective terms** — rejected without measurement, and the reason matters: in
  pasted machine output the rarest terms are hex ids, timestamps and paths. Selectivity
  ranking would preferentially keep the noise and discard the question's common words.
- **Deduplicate, first-occurrence order** — not shipped. Recall-neutral and principled,
  but its measured retrieval effect is nil (Result 2). Its remaining case is **cost**:
  49,267 → 3,847 terms, a 92.2% reduction. That is a latency claim, and latency was not
  measurable (see below), so it is not made.
- **Deduplicate + cap** — not shipped, per Results 3 and 4.
- **Trimming the FTS leg to 254 tokens to match the vector leg** — rejected. That is the
  embedding model's hard limit (ADR-0071), not a retrieval argument, and the tail's rare
  terms are what keyword search is best at.

## Consequences

**Latency is unmeasured, and no figure is recorded.** Load average ran between 26 and 60
throughout this work with two other agents building and testing on the same machine.
`ParityGateTests` asserts a p95 budget and had already failed locally today purely under
background load. A caveated number gets quoted without its caveat — which is how this
project published in-sample retrieval figures that were 42% of what readers believed — so
the cost half of the dedup case stays open rather than half-answered.

**The existing gates cannot adjudicate this change, and that is the blocker.** All 44
queries in `scripts/baseline-queries.json` are ≤10 raw tokens and **not one contains a
duplicate token**. Dedup and any cap ≥11 are therefore no-ops across the entire gate
corpus: the gates can prove non-regression on short queries and nothing more. The
measurements above had to build their own long-query condition, and a corpus built
alongside the change it validates is precisely the trap this project has been caught by
before — so those numbers are evidence for refusing, not licence for shipping.

**What would unblock it:** a held-out family of long, pasted-output queries with expected
documents pinned by someone other than the author of the change, large enough that a cap
sweep is not fitting three points. Until that exists, no term budget is adjudicable.

**The one thing the measurements settle cheaply:** the keyword leg contributes **nothing**
once a paste is in the query. The user-facing consequence — a long query gets a weaker
search — is real, already true today, and better addressed by telling the caller (as
ADR-0071 does for the vector leg, event 416) than by silently reweighting their terms.
That warning is not in this record because it was not measured here.

**Reproduction.** The experiment scripts are committed under `docs/work/2026-08-15-fts-term-budget/`.
`BuildPlan` is reimplemented there in Python for the sweep; it is not the shipped code,
and any follow-up should re-derive rather than trust it.
