# 0053. `rating` is computed where it is stored

Date: 2026-08-15

Status: Accepted

## Context

`BumpAccessAsync` read a row's `created_at` and `access_count`, computed `RatingPolicy.Rating` in C#
from `row.AccessCount + 1`, then wrote it back in a second statement:

```sql
UPDATE entries SET access_count = access_count + 1, last_accessed_at = @now, rating = @rating WHERE …
```

`access_count` is a **relative** expression and survives interleaving; `rating` is a **literal**
computed from a value read in an earlier round trip, and does not. Two concurrent hits on one hash
both read 5, both write the rating of 6, and the row ends at `access_count` 7 with the rating of 6.

`rating` is the sweep's deletion input (`SweepService`), so the drift is not cosmetic: an entry can be
swept on a rating lower than its own access history justifies.

## The measurement, and a first attempt that was wrong

The data-access lane established the race by hand-interleaving two raw connections. That proves the
SQL shape is racy; it does not prove the store exposes it. The first reproduction here **failed to go
red** — 24, then 200 concurrent `SearchAsync` calls left `rating` exactly consistent
(`access_count=200`, `rating=10.5 = 0.5 × (1 + 200 × 0.1)`).

The reason is worth recording, because it will mislead the next person too, and it is documented
rather than inferred — [Async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async),
Microsoft.Data.Sqlite: *"SQLite doesn't support asynchronous I/O. Async ADO.NET methods will execute
synchronously in Microsoft.Data.Sqlite. **Avoid calling them.**"* Dapper's `ExecuteAsync` therefore
completes synchronously, so
`Task.WhenAll(range.Select(_ => store.SearchAsync(...)))` runs the searches one after another and
never interleaves. Wrapping each in `Task.Run` to force thread-pool parallelism reproduced it
immediately:

```
accessCount=64  rating=3.6      # 3.6 is the rating of 62 — two bumps' contributions lost
                                # expected 0.5 × (1 + 64 × 0.1) = 3.7
```

**A concurrency test built on `WhenAll` over an async-in-name-only provider is not a concurrency
test.** It passes against the defect it exists to catch.

The same page's second half is a finding this codebase has not acted on: the data layer calls those
async ADO.NET methods throughout, which Microsoft says plainly to avoid. They buy no concurrency and
cost a state machine per call; the concurrency they appear to promise is the thing that made the first
reproduction here pass against a live defect. Recorded in the improvement plan rather than changed
under this ADR — the signatures are async all the way up to the MCP tool surface, so it is a package,
not a footnote. WAL, which the page recommends instead, is already enabled.

## Decision

**Compute `rating` in the same statement that increments the count.**

```sql
UPDATE entries
SET access_count = access_count + 1,
    last_accessed_at = @now,
    rating = @baseScore
             * pow(0.5, max(0.0, (@now - created_at) / 86400.0) / @halfLifeDays)
             * (1 + (access_count + 1) * @accessMultiplier)
WHERE hash = @hash AND (project_id = @projectId OR scope = 'shared')
```

SQLite evaluates every `SET` right-hand side against the **pre-UPDATE** row, so `access_count + 1` is
the new count — matching what the C# computed. The policy constants are still passed from
`RatingPolicy`, so the formula has one source even though it is evaluated in two languages.

Chosen over wrapping the read-then-write in `BEGIN IMMEDIATE`: a transaction per result on the search
hot path costs a round trip per hit and serialises bumps that need not be serialised, where one
statement is atomic by construction and **removes** a round trip.

## Consequences

- `rating` is always the rating of the `access_count` beside it, under any interleaving.
- One statement per bumped hash instead of two — the search hot path does strictly less work.
- `MemorySql.SelectRatingForBump` and the `RatingRow` type are deleted; nothing else read them.
- `SqliteMemoryStore.cs` drops 1251 → **1243** lines and the size ratchet is lowered to match.
- **`pow` must exist in the SQLite build.** It does here (SQLite 3.53.4 / SQLite3 Multiple Ciphers 2.4.0) and the gate below would fail loudly with `no such function: pow` if a future build dropped math functions.

## Evidence

`tests/AiRaccoon.Tests/Integration/Storage/RatingBumpConsistencyTests.cs`. Watched red first, with the
message it was written to produce:

```
rating 3.6 does not match the rating of the stored access_count 64 (3.7);
a bump's rating contribution was lost while its count survived
```

The sequential case is asserted alongside it, so the fix cannot trade a race for a formula that is
merely wrong more quietly.
