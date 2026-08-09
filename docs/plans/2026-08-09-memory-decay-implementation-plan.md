# Memory decay — implementation plan

**Status:** planned, not started. Supersedes the design brief's staging section
(`docs/reviews/2026-08-09-h9-decay-design-brief.md`); that document remains the record of the
findings and the owner's rulings.

## The shape of the feature, decided

Four owner rulings, in the order they were made:

1. The reaper exists to prune memories that are **not used**.
2. Memories must **decay** via a real algorithm, not disappear after a fixed time.
3. Watched/file-derived entries are **excluded from deletion**.
4. Deletion affects **only manual memories** — `source_file IS NULL`, strictly.
5. **Ranking damping is the centre of the feature, and it applies to every entry including
   file-derived ones.** Deletion is the tail.

Ruling 5 is what this plan is built around, and it follows from a measurement rather than a
preference: 99.8% of the bank carries a `source_file`, so after ruling 4 the deletable population is
**30 rows out of 13,289**. Deletion cannot be the product. Damping reaches the other 98.6%, loses
nothing, and is reversible.

**One-line statement of the feature:** *a memory that stops being used fades out of search results
first, and only manual memories are ever deleted.*

## Why the existing ranking pipeline fits this well

Read `ReciprocalRankFusion.Fuse` before building anything. It fuses the FTS and vector lists into a
raw score per hash, then does:

```csharp
var max = scores.Values.Max();
... payloads[pair.Key] with { Ranking = pair.Value / max }
   .Where(result => result.Ranking >= minScore)
```

**The normalization to `max = 1.0` is the safety property this feature needs, and it is already
there.** Because every score is divided by the best score in the same result set, a damping factor
applied before normalization changes **relative** order, not absolute availability. Three
consequences, all desirable, and all worth pinning with tests:

- A query whose only answers are old still returns them at full normalized ranking. **Decay changes
  which memory wins, not whether you get an answer.** This is the answer to "decay must not become
  censorship" and it needs no artificial floor.
- Where a fresh and a stale memory compete, the fresh one wins — which is the entire point.
- A stale result that falls far enough behind the leader drops under `minScore` and disappears from
  that result set. That is the "fade", and it is graded rather than a cliff.

So the damp belongs **inside the fusion, applied to the raw score before `max` is taken**. Applying
it after normalization would break the first property and make decay absolute.

## Work packages

WP1 → WP2 is the critical path. WP3 is independent. WP4 must come last.

### WP1 — decay becomes a live, idle-based computation

Today `RatingPolicy.Rating(...)` has exactly one call site (`SqliteMemoryStore.BumpAccessAsync:994`,
on a search hit), so an entry nobody touches keeps the schema default `0.5` forever, and
`SweepService:41` measures age from `created_at` rather than last use.

- Add a pure function in `AiRaccoon.Core` — `MemoryDecay.Factor(accessCount, idleDays, halfLifeDays)`
  or similar — expressing `idleDays = (now - COALESCE(last_accessed_at, created_at)) / 86400`. Keep
  `RatingPolicy` as the curve; this is the *evaluation* the product currently lacks.
- Widen `MemorySql.SelectEntryMetadata` — it returns only `rating` and `ttl_days`; it needs
  `access_count` and `last_accessed_at`. Both columns already exist on `entries` and `BumpAccess`
  already maintains them, so **no schema migration is required.**
- Treat the stored `rating` column as a cache, never as a source of truth. Nothing may branch on it
  without recomputing.

**Known dependency — `BumpAccess` is scope-blind, and WP1 reads exactly what it writes.** Fix lane
A1 (`fix/1-6-0-h2-h3-storage-scope`) confirmed while fixing H2 that `SelectRatingForBump` and
`BumpAccess` filter on `(hash, project_id)` with no scope predicate, so **a single search hit writes
`rating`, `access_count` and `last_accessed_at` to every same-hash sibling** — including a
workspace-scratch row and a custom-context row. A1 deliberately left them alone (out of its lane,
and nothing in its suite demonstrated them broken), which was the right call for that lane and
leaves the work here.

This is not cosmetic for WP1: the idle computation keys on `last_accessed_at`, so under the current
statement a search that touched the committed row marks an untouched workspace sibling as recently
used, and that sibling silently stops decaying. **Fix the bump statements as part of WP1** — same
one-line predicate shape A1 already applied to `UpdateEntryTtl` and `SelectEntryMetadata`, and worth
a test asserting a sibling's `last_accessed_at` stays null when the project row is searched.

A1 also found, and fixed, a second-order version of this that the review had only hypothesized:
`SelectEntryMetadata` was scope-blind too, so with `UpdateEntryTtl` alone fixed, the sweep's
metadata read (`LIMIT 1`, no `ORDER BY`) could return the *sibling's* `ttl_days = NULL` and go inert
on the very row it was meant to delete. Expect more of this class wherever a statement filters on
`(hash, project_id)` — the hash does not encode scope, so that pair is not a row identity anywhere
in this schema.

**Acceptance.** Red-first, paste each failure:
- An entry never accessed and idle past the crossover computes a factor below the threshold. *Fails
  today — the stored `0.5` comes back.* This single assertion is ruling 1.
- Two entries created at the same instant, one read yesterday and one never: the read one scores
  materially higher. *Fails today; both decay from creation.*
- An entry accessed 100 times but idle a year scores below one accessed once yesterday — disuse must
  beat access count, or "not used" means nothing.
- Monotonic decrease across increasing idle days, with no discontinuity at the threshold.
- `idleDays` is clamped at 0 so a future `last_accessed_at` (clock skew, sync from a fast machine)
  cannot produce a factor above the base score.

### WP2 — decay damps the ranking (the centre of the feature)

- Thread a per-hash decay factor into `ReciprocalRankFusion.Fuse` and multiply the accumulated raw
  score by it **before** `max` is computed. Do not touch the normalization, the `minScore` filter or
  the ordering — they already do the right thing.
- Fetch the decay inputs in the same batch that resolves payloads; do **not** issue a query per hash.
  `SearchAsync` already batches per context, and the review measured the 1 Hz watch loop opening
  ~15 unpooled connections per second — do not add a second per-row-connection pattern.
- The damp applies to **every** entry, file-derived included (ruling 5).
- Bound the factor. A multiplier that can reach 0 lets one very stale row vanish from a set where it
  is the only answer, undoing the normalization property above. Floor it well above zero and pin the
  floor with a test.

**Acceptance.** Red-first:
- Two entries of equal textual relevance, one fresh and one stale: the fresh one ranks first. *This
  is the feature.*
- **The censorship guard:** a query whose only match is a heavily decayed entry still returns it,
  with `Ranking == 1.0` after normalization. Assert this explicitly — it is the property that makes
  damping safe, and it must be seen to fail if someone later applies the damp post-normalization.
- **No feedback ratchet:** simulate repeated search rounds where ranking damps and hits bump. Assert
  a mid-decay entry does not spiral to the floor purely because damping demoted it. If it does, the
  bump must win over one round of damping.
- Damping is off by default behind a setting until WP3 has been observed (see Rollout), and the
  off path is byte-identical to today's ranking — pin that with a test too.

**Answer ADR-0018 before writing this code, do not bypass it.** That ADR deliberately removed
`Rating`/`AccessCount` from the promotion scorer, and `SharedExtraction.cs:24` still records it:
*"Rating/AccessCount are informational context v3 does not read."* The distinction to argue in the
new ADR is that promotion asks *"is this durable and worth sharing across projects?"* — where
popularity is a bad proxy — while retrieval asks *"is this the answer right now?"*, where
recency-of-use is legitimate evidence. If that argument does not hold up, this WP does not ship.

### WP3 — make decay observable before it is destructive

Independent of WP2 and worth landing first if anything slips.

- Surface the computed decay factor and `idleDays` on `memory_sweep(dryRun:true)` output, so the
  owner can see exactly what *would* be pruned on the real bank, for as long as they want to watch.
- Report the same on the sweep span once lane B's H7 fix lands (that fix is what makes a sweep pass
  visible in traces at all).
- Add a CLI read — `ai-raccoon sweep preview` or equivalent — that reports the decay distribution
  without deleting. There is currently **no bank-wide dry run and no manual trigger**; a destructive
  default-on job with neither is its own finding.

**Acceptance.** A dry run on a populated bank reports candidates and deletes **zero** — assert the
row count is unchanged, not merely that the response said `dryRun`.

### WP4 — deletion, manual-only, last

Only after WP1 and WP3 have run on real data for at least one half-life (30 days), **and** after
H2 (scope-blind delete) and H6 (reaper ignores per-project access modes) have landed. Pruning must
be correct and consented before it is broadened.

- Eligibility becomes: `source_file IS NULL` **and** decayed factor below threshold **and** idle
  beyond a grace period. Relax `ttlDays.HasValue` in `DegradationPolicy` from a mandatory conjunct
  to an optional override.
- An explicit `memory_set_ttl` keeps meaning "drop this after N **idle** days" — shift its semantics
  from creation-age to idle so both gates agree, and correct its tool description, which currently
  tells agents decay is a matter of time.
- **Provenance must fail safe:** an entry whose provenance cannot be established is treated as
  file-derived and left alone. Over-deleting is unrecoverable; under-deleting costs disk.

**Acceptance.** Red-first: a file-derived entry, idle for years, with a decay factor far below
threshold, is **never** a deletion candidate. A manual entry in the same state **is**. And the
measured regression guard: with no TTL and idle < ~22 days at the default half-life and threshold,
nothing is a candidate.

## Rollout

1. **WP1 + WP3 together, damping off.** Zero behaviour change; the bank becomes observable. Watch
   the dry-run distribution.
2. **WP2 behind a setting, default off.** Turn it on, compare retrieval quality against the golden
   retrieval set the repo already keeps (`scripts/regenerate-retrieval-golden.py`,
   `baseline-retrieval-report.md`) — **decay must not regress the baseline**. This is the gate that
   decides whether damping ships.
3. **WP2 on by default** once the baseline holds.
4. **WP4** last, after a half-life of WP3 observation.

Only WP4 is irreversible. Steps 1–3 are how you learn what WP4 would do.

## Facts to carry into the ADR

Write one ADR covering the decay model; it is the decision record the reaper never got.

- `RatingPolicy.Rating` has **one** call site; the stored rating is read in two places
  (`SweepService:40`, `ForgettingPolicyService:54`) and influences ranking nowhere. A formula
  existed; a system did not.
- Live bank, measured read-only: 13,289 project rows, 92.8% never accessed, **0 carrying a TTL**,
  max idle 3.9 days against a ~22-idle-day threshold crossover
  (`0.5·0.5^(d/30) < 0.3 ⇒ d > 30·log₂(0.5/0.3) ≈ 22.1`). The feature lands with zero immediate
  deletions.
- 99.8% of rows carry a `source_file`; **30 rows** are deletable under ruling 4. Hence ruling 5.
- Pruning a watched-file chunk is **not self-healing** — the watch digest compares file hashes, so
  an unchanged file is never re-ingested and the chunk stays missing until someone edits it. This is
  what forced rulings 3 and 4.
- Everything decays at one rate. A pinned decision and a transient scratch note sharing a half-life
  is a known gap (`docs/work/archive/2026-08-04-memory-model-gap-analysis.md`) and the natural
  follow-up once the system works at all — **out of scope here.**

## Sequencing and collisions

- After in-flight fix lane A1 (`fix/1-6-0-h2-h3-storage-scope`), which owns `MemorySql.cs` and
  `SqliteMemoryStore.cs` — both of which WP1 and WP2 need.
- WP4 after H2 and H6.
- No collision with PR #246 (promotion scoring): that is candidate *selection* for the shared tier;
  this is retrieval ranking and retention. They meet only in ADR-0018, which WP2 must answer.
