# 0040. Read-Path Query Guard

Date: 2026-08-14

## Status
Accepted

## Context
Agents sometimes call `memory_search` with a query that is itself machine output rather than a
question — a background-process completion notice, a delegation-batch summary, a pasted stack
trace. The bank does what it is asked and returns whatever is nearest by embedding distance,
which is useless, and the agent spends a turn on it.

Measured read-only against the live bank's `search_quality` table (399 rows, 181 graded, sampled
2026-08-14):

| Query kind | Found | Graded | Grades |
|---|---|---|---|
| Hermes process-notification (`[IMPORTANT: Background process ... completed normally ... Command:`) | 18 | 12 | **12/12 scored 2/5, never higher** |
| Async delegation-batch (`[ASYNC DELEGATION BATCH COMPLETE ...`) | 25 | 10 | **10/10 scored 2/5, never higher** |
| Everything else | — | 159 | 18×1, 118×2, 4×3, 1×4, **18×5** |

Both machine-output shapes are unambiguous — every graded instance of either scored 2/5 and none
ever scored higher — while ~11% of ordinary queries reach 5/5. A query search-quality never once
rewarded is a defensible refusal, not a judgment call.

`ADR-0029`'s own Context named noise "as search queries" as part of the problem it was solving,
then targeted the write path instead (`INoiseFilteringService` on `memory_write`). This ADR closes
the read-path gap that left: `memory_search` had no equivalent guard until now.

A third finding shaped the design more than the two refuse shapes did: the bank's dominant
low-grade traffic is not log content at all. Sampling the graded-2 rows that are *not* one of the
two shapes above shows the agent frequently pastes its own multi-paragraph task brief (a `f:`/`e:`
prompt-marker message, a bulleted plan) as the search query — long, sometimes multi-line, but not
machine output. A naive "long or multi-line query" heuristic would misclassify that dominant case
as "looks like tool output," which is simply false, and would be exactly the kind of
tuned-on-invented-signal mistake this project's write-path filter (ADR-0029, ADR-0033) already
made. The annotate tier below was narrowed until it stopped doing that.

## Decision
`memory_search` gets a two-tier read-path guard, evaluated on the raw query string before any
other work happens.

1. **Refuse, high confidence** (`QueryGuardPolicy.Evaluate` → `Refuse`). A query matching one of
   the two structurally-unambiguous machine-output shapes above is refused: `MemoryTools.Search`
   throws `McpException("invalid-params: ...")` before calling `IMemoryStore.SearchAsync` — no
   embedding, no `search_quality` row, no fabricated empty success (ADR-0032). The message is
   reused through the existing `invalid-params:` convention `ToolRefusals`/callers already branch
   on, not a new refusal category.
2. **Annotate, lower confidence** (`Warn`). A query that merely contains a structural marker of
   genuine log/tool output — a `.NET`-style stack-frame line (`at Namespace.Type.Method(...)`) or a
   console log-level prefix (`info:`, `warn:`, `fail:`, ...) — still runs; the response
   (`MemoryTools.SearchResultList.Warning`) carries guidance. Refusing here would risk blocking a
   legitimate search for a stack frame someone actually saw.

Both tiers hand back the same kind of guidance, concretely actionable rather than "invalid query":
*"This looks like tool output rather than a question. Memory search matches meaning, so search for
what you want to find — e.g. 'why did the auth build start failing' rather than pasting the build
log."* This is the server's agent-assistance purpose doing real work, not decoration — the owner's
explicit requirement.

**Where the logic lives.** `QueryGuardPolicy` and `QueryGuardVerdict` are a static, dependency-free
policy in `AiRaccoon.Core.Memory.QueryGuard` — pure string/regex checks, no I/O, no DI container
(the "static classes: pure functions only" invariant). `MemoryTools` (the MCP tool layer) only
consumes the verdict: reads two settings, calls `Evaluate`, and maps the tier to
throw/annotate/pass-through. No business logic lives in the tool (the MCP-stays-thin invariant).

**Settings and shadow mode.** Two keys, mirroring the `noise.enabled.global` / `sweep.enabled.global`
convention:

| Key | Default | Meaning |
|---|---|---|
| `queryGuard.enabled.global` | `true` | Kill switch. `false` skips guard evaluation entirely — behavior is byte-identical to no guard. |
| `queryGuard.shadow.global` | `false` | Shadow mode: a non-Clean verdict is logged (`MemoryTools` EventId 920) and then treated as Clean — nothing is refused or annotated. |

**Default: armed (`true`), not shadow.** Unlike the write-path noise filter, a false positive here
costs the caller one refused search, not a destroyed memory — the asymmetry ADR-0029 traded off is
absent. And the refuse tier is exactly as evidence-backed as the write-path filter (22/22 graded
matches across both shapes scored 2/5, never higher), so there is no real-traffic reason to ship it
off by default. Shadow mode exists for an operator who wants to verify the guard's behaviour
against their own traffic before trusting it, not because the default itself is in doubt.

**Provenance of every threshold.** There are no tuned numeric thresholds in this guard — every
signal is a structural match (a literal prefix, a required substring, a regex anchored on an actual
log/stack-trace shape), each commented with the `search_quality` measurement or the real graded
row that motivated it. This is deliberate: ADR-0033's post-mortem on the write-path filter's
`0.20`/`0.12`/`0.75` cutoffs is half of why this design avoids inventing any.

## Consequences
- **Positive:** A query search-quality has never once rewarded (12/12 and 10/10, always 2/5) is now
  refused before the embedding cost is paid, with guidance the agent can act on immediately.
- **Positive:** Zero embedding cost by construction — `QueryGuardPolicy` cannot call an embedder;
  measured at averaging well under 1 ms per call over 10,000 iterations
  (`QueryGuardPolicyTests.Evaluate_RunsInSubMillisecondTime`), against a real embedding cost of
  14.6-26.5 ms (ADR-0029).
- **Positive:** Measured false-positive rate on real graded-4-and-5 queries: **0/19** — every real
  high-value query sampled from `search_quality` passes through Clean, untouched.
- **Negative:** The annotate tier's two structural markers (stack-frame line, console log-level
  prefix) are backed by a single real example each (`search_quality` id 251, grade 1/5: a pasted
  `HttpRequestException` stack trace) rather than the double-digit sample the refuse tier has. It
  is deliberately narrow rather than broad — see the "How this table is produced"-style caution
  above about tuning on imagined shapes.
- **Negative:** The dominant source of low grades in this bank — agents pasting their own
  multi-paragraph task briefs as the query — is not addressed by either tier. It is real noise, but
  it is not machine output, and mislabeling it as "looks like tool output" would be actively wrong
  guidance. Left as a finding for a future ADR, not folded into this one's evidence.

## Measurement (search_quality, 399 rows, 181 graded, read-only, 2026-08-14)

| Metric | Value |
|---|---|
| Hermes process-notification shape, graded | 12/12 → 2/5, never higher |
| Async delegation-batch shape, graded | 10/10 → 2/5, never higher |
| Everything else, graded | 18×1, 118×2, 4×3, 1×4, 18×5 |
| False-positive rate on real graded-4-and-5 queries | 0/19 |
| Guard evaluation cost | sub-millisecond (measured); real embedding is 14.6-26.5 ms (ADR-0029) |
