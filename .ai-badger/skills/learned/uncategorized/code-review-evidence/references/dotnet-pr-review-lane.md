# .NET PR review lane — Dapper affinity, hosted-service loops, dead knobs, DI duplication

Worked dotnet-engineer lane of the ai-raccoon PR #55 integration review
(background shared-extraction hosted service + CLI verbs). Reusable for any .NET
PR touching `BackgroundService` loops, Dapper/SQLite queries, DI registration,
LoggerMessage EventIds, or settings-table config keys.

## 1. Dapper single-column mapping: check column AFFINITY, not the alias

Trap class: "does `QueryAsync<string>` work for this `SELECT ... AS Alias` query?"

- The alias (`AS ProjectId`) is **irrelevant for scalar mapping** — Dapper reads
  column 0 and casts via the type handler (`StringHandler.Parse` = `(string)value`).
  Record-ctor matching (alias → ctor parameter) is a *different* path and only
  applies to `QueryAsync<SomeRecord>`.
- What matters is the column's **declared type → affinity** in CREATE TABLE:
  - TEXT affinity → Microsoft.Data.Sqlite returns `string` → `QueryAsync<string>` works.
  - INTEGER affinity → returns `long` → `(string)long` cast → InvalidCastException at runtime.
  - BLOB affinity → returns `byte[]` → cast fails. (This repo's history: "blob-affinity
    columns defeated record-ctor matching", fixed by switching a row record int→long.)
- Procedure: find the CREATE TABLE (e.g. `MemorySchema.cs`), read the column
  declaration, *then* judge. Confirm the query is index-covered
  (`CREATE INDEX ... ON entries(scope, project_id)` pattern) and that a port/contract
  test exercises it against the real database, not just fakes.
- NULL hardening NIT: if the column is declared NULLable, consider
  `AND col IS NOT NULL` — a NULL row surfaces as `null` in the list and flows into
  callers that may throw per-item (which the per-project catch then logs as spam).

## 2. Hosted-service loop review (BackgroundService.ExecuteAsync)

- **Outer loop idiom**: `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }`
  before `catch (Exception)`. An OCE propagating out of `ExecuteAsync` is fine
  (task completes Canceled; StopAsync doesn't rethrow for Canceled status).
- **Per-item catch trap**: a bare `catch (Exception)` around per-item work inside
  the loop **swallows OCE** → during shutdown every remaining item throws OCE,
  gets logged (warning + exception spam), and costs a DB round-trip each. It does
  terminate (the next post-loop await on the cancelled token propagates), but the
  inner catch needs the same filter + `throw;` for responsiveness and clean shutdown.
- **"Best-effort" contract check**: read the class doc claim, then verify EVERY
  awaited read is inside the try. A settings/interval re-read placed outside the
  try (e.g. `timer.Period = await ReadIntervalAsync(...)` after the catch block)
  faults `ExecuteAsync` on a transient DB error; with the .NET 8+ default
  `HostOptions.BackgroundServiceExceptionBehavior.StopHost` that takes down the
  WHOLE host, not just the loop — contradicting the documented best-effort promise.
- **Timer choices**: `PeriodicTimer(period, TimeProvider)` (net8+) avoids
  Task.Delay-loop drift and supports `timer.Period = ...` for live re-config;
  `FakeTimeProvider` + `time.Advance(...)` drives it in tests. First tick fires
  one interval after construction — an "enabled but no effect for a full interval"
  NIT unless the loop self-gates on an enabled flag and an immediate tick is intended.

## 3. Config knobs: prove a writer exists before accepting a knob

For any settings-table key that a service reads or a `list` verb displays:

```bash
git grep "<key>" <branch> -- src/ tests/ docs/
```

If the matches are only (const definition + reads + test fixtures) — **no write
path** — the knob is dead: displayed-but-unsettable, default forever. Flag as
SHOULD-FIX (add the missing CLI verb + docs + test, or drop the key from the
surface). Never assume a knob is settable because it is read.

## 4. DI duplication across transport paths

When a registration helper (`RegisterMemoryServices`) is called from multiple
transport builders (stdio host, web host), verify the call sites are **mutually
exclusive branches of one `CreateServerHost`** (e.g. `if (transports.Count == 1 && stdio) return StdioHost(); return WebHost();`),
not additive paths. Both-transports mode must fall through to a single host →
exactly one `AddHostedService<T>` / one singleton. If additive, you get two
background loops doing duplicate work (idempotent or not).

## 5. LoggerMessage EventId collision check

```bash
rg -n "EventId\s*=" src/
```

Map the existing ranges (this repo: 1-5, 100, 200-205, 300-310, 320, 330, 400;
per-class re-use of 1-5 is tolerated) before accepting a new block (500-505 was
clean). Note which classes use NO LoggerMessage at all — no collision there.

## 6. Mid-review merge: branch ref pruned / PR merged while reviewing

Symptom: commands that worked minutes ago start failing
(`fatal: bad revision 'origin/<branch>'`), or `git grep origin/main` unexpectedly
finds files that should only exist on the branch.

- Detect: `git log --oneline origin/main -- <branch-only-file>` → the merge/squash commit.
- Re-baseline: `git diff <merge>^ <merge> --stat` must equal the original
  `git diff origin/main...<branch> --stat` (same file count / insertions) — then
  cite line numbers from the merged tree (`git show <merge>:<path> | nl -ba`).
- Report the state change up front so the review's target commit is unambiguous.

## Severity calibration from this review

- SHOULD-FIX (not MUST): OCE swallowed by per-item catch; settings/interval read
  outside the best-effort try; dead config knob (read+displayed, never written).
- NIT: first-tick warm-up delay; missing `IS NOT NULL` on a NULLable filter column.
- Verified-OK (worth stating explicitly): scalar Dapper mapping with TEXT affinity;
  cancellation propagated through `CommandDefinition`; EventId block collision-free;
  `CancellationToken` last in signatures; new optional dispatcher param before the
  token with named-arg call sites; single hosted-service instance across transports.
