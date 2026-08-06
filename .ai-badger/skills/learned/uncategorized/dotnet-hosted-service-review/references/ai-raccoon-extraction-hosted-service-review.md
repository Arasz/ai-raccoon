# Worked example — ai-raccoon PR #55 `feat(extract): background shared-extraction hosted service`

Review-lane case study (Aug 2026). New `ExtractionHostedService` (137 lines, Infrastructure/Extraction) + `extract enable|mode|list` CLI verbs + `IMemoryStore.GetProjectIdsAsync` port addition (28 files, +688/−3). Verdict reached: APPROVE WITH CHANGES — all 6 safety claims held; 1 SHOULD-FIX (process-killer), 2 low SHOULD-FIXes, 5 NITs.

## Finding shapes worth reusing

**F1 (SHOULD-FIX): interval re-read outside try/catch.** `ExtractionHostedService.cs:51` — `timer.Period = await ReadIntervalAsync(...)` sits outside the run's try/catch (also the startup read at :33). Non-OCE store failure → ExecuteAsync faults → `BackgroundServiceExceptionBehavior.StopHost` (default) kills the whole MCP server. Contradicted the class doc's own "best-effort" claim (:12) and diverged from sibling `WatchHostedService` (try/catches reconcile AND delay).

**F3 (SHOULD-FIX, low): multi-process TOCTOU on dedup.** `AddContentAsync` (SqliteMemoryStore.cs:494-521) is SELECT-then-INSERT keyed on path; `entries` table (MemorySchema.cs:26-50) has NO unique constraint. Each stdio MCP client = one server process; extraction enabled → N loops per bank. Two processes can both read "no shared row" and both INSERT. `PRAGMA busy_timeout=5000` (SqliteConnectionFactory.cs:140) serializes commits, not reads. Rated low because identical exposure pre-existed on the `memory_share_extract` tool path. Fix offered: partial UNIQUE index on committed rows.

**F4 (NIT): per-item catch swallows OCE.** `catch (Exception ex)` per project (ExtractionHostedService.cs:96-99) catches `OperationCanceledException` → spurious `ProjectFailed` Warnings during shutdown; top-level `catch (OCE) when (IsCancellationRequested) break` (:41-44) never fires from the run body.

**F5 (NIT): fake-store idempotency gap.** Test fake's `ShareAsync` appended unconditionally and never updated its shared index, so the poll-loop test asserting `Shared.Count == 2` would pass even if the loop re-shared everything every tick. Real cross-run idempotency (index re-read + store path-dedup) had no direct test.

## Safety-claim table (template for future PRs)

| Claim | Verdict | Evidence pattern |
|---|---|---|
| Off by default | HOLDS | strict parser `value == "true"`; `GetSettingAsync` null for unset key; only writer = CLI verb |
| Propose never shares | HOLDS | promoted list only populated in Promote mode; `ShareAsync` called only for PromotedHashes |
| Promote warns on explicit change | HOLDS (soft, no confirm gate) | CLI prints "shares candidates with ALL projects"; re-warns on enable |
| Dedup vs existing shared tier | HOLDS (per-run snapshot) | value+path dedup vs GetSharedIndexAsync; store path-idempotency |
| Idempotent | HOLDS single-process / caveat multi-process | re-read index each run; TOCTOU window (F3) |
| No delete path | HOLDS | grep for `Delete*` calls in service = none |

## Other verified facts (repo knowledge)

- One host per process: `McpServerSetup.CreateServerHost` picks app-host (stdio-only) OR web-host (http / both) — `AddHostedService` runs once per process.
- EventId ranges by class: 1-5, 100, 200-205, 300-330, 400 (sweep), 500-505 (extraction). Same-category collisions are the only real ones.
- New `IMemoryStore` members need: SqliteMemoryStore impl + MemoryExtensionHost passthrough + fakes in ~11 test files (throw vs return per fake convention) + MemoryStorePortTests port test. Build/tests catch any missed implementer.
- CLI verb family = 4 coordinated touch points: `CliCommandTree.Verbs` array, root command registration, `ConfigCommands` dispatcher row (sanctioned static-dispatch pattern), verb-pin test.
