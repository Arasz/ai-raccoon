# 0032. A Write Outcome Is Truthful

Date: 2026-08-14

## Status
Accepted

## Context
`SqliteMemoryStore.WriteAsync` returned a fabricated `MemoryEntry("noise_hash", "noise_path", ...)`
for any write a noise policy rejected. No row reached `entries` — the content was unreachable, not
persisted anywhere a tool could return it — but `MemoryTools.Write` mapped the fabricated entry
straight into a success envelope, under a tool description that reads "Returns the stored entry."
Every rejected write returned the same hash and path, so an agent could not distinguish two
dropped writes from each other or from a real one, and `memory_delete("noise_hash")` reported
`deleted=0` — indistinguishable from an idempotent no-op on an unknown hash.

ADR-0029 §Decision 2 ratified this explicitly ("we return a dummy success entry"). This is the one
path in a memory server that discards an agent's memory and tells it the opposite.

## Decision
A write outcome is truthful. `MemoryEntry` gains two additive optional members —
`bool Stored = true` and `string? Reason = null` — rather than a new return type: `IMemoryStore`
already returns `MemoryEntry` from `WriteAsync`, and 16 test files implement `IMemoryStore`, so a
new type would have turned a small fix into a 16-file edit for no behavioural gain. Every existing
positional construction of `MemoryEntry` keeps compiling unchanged, with `Stored` defaulting true.

1. A write a policy rejects now returns `Stored: false` and `Reason` naming the policy
   (`"rejected by noise policy '<PolicyName>'"`), with `Hash` and `Path` both empty — no fabricated
   identity for content that was never persisted.
2. `INoiseFilteringService.EvaluatePreWriteAsync` returns the existing `NoiseFilterResult` (already
   carrying the matched policy's name) instead of a bare `bool`, so the caller can name what
   rejected a write.
3. The MCP `WriteResult` surfaces `Stored`/`Reason`, and the `memory_write` tool description states
   that a write may be refused and how to tell (`stored=false` + `reason`).
4. A `noise.enabled` settings key (`noise.enabled.global`), mirroring the existing `sweep.enabled`
   convention (on unless explicitly `"false"`), is a kill switch for pre-write rejection —
   `noise enable`/`noise disable`/`noise show` CLI verbs match the `sweep` verb family.

Supersedes ADR-0029 §Decision 2. Landed in the same PR as, and before, ADR-0033's deletion of the
noise store — a rejected write must never be a fabricated success *and* unrecoverable at the same
time, which is what the interval between those two changes would otherwise produce.

## Consequences
- **Positive:** An agent can now tell a refused write from a stored one, and knows which policy
  refused it, without guessing from a sentinel hash.
- **Positive:** No new return type — `IMemoryStore.WriteAsync` and its 16 test fakes are untouched.
- **Negative:** `WriteResult`/`MemoryEntry` grow two members every caller must now consider,
  even ones that never see a rejected write.
- **Negative, not fixed here:** the kill switch (`noise.enabled`) has to be read from somewhere, so
  `WriteAsync` now issues one extra settings read (`ReadSettingAsync(NoiseConfigKeys.EnabledGlobal)`)
  on every `memory_write` — there is no settings cache anywhere in this codebase today. That is one
  more round trip on the operation this server exists to make fast. Deliberately not addressed in
  this wave; WP6 is already targeting per-write round trips and should fold this read into whatever
  batching or caching it lands there rather than this wave reinventing it in isolation.
