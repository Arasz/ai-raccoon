# Integration Review: parallel session — 1.0.9 release + hosted-service PR #55

**Reviewer:** Hermes agent (task `integration-review-1-0-9`, MoE review)
**Date:** 2026-08-06 **Base → HEAD:** `origin/main` @ `1817a80` → `d6cda64` (incl. #55 squash `f9e1eea`, merged
mid-review by owner)
**Scope:** quality check after a parallel work session — how the changed points integrate (package-id migration to
`ai-raccoon` 1.0.9, baseline re-pin #54, observability/parity fixes #51, hosted extraction service #55), regression
check, full suite, manual tests, fresh server setup. **Gates:** `dotnet build` (0 warnings) · full `dotnet test` ·
manual fresh-install protocol (published 1.0.9) · fresh local server + MCP round trip · global-tool migration (owner e:)
**Method:** MoE — 4 expert lanes in parallel (architect, code-reviewer, test-engineer, dotnet-engineer), orchestrator
ran all gates and reconciled lane findings against the merged source (two lanes had state-drift/claim errors; each
finding verified at `f9e1eea` before acceptance).

---

## Gates (measured, orchestrator)

| Gate                                                         | Result                                                                                                                                                                                                                             |
|--------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Build, main HEAD `1817a80`                                   | 0 warnings / 0 errors                                                                                                                                                                                                              |
| Full suite, main HEAD                                        | **1211 passed / 0 failed / 4 skipped** (prev review: 1206/5/4 — the 5 corpus failures cleared by #54 re-pin)                                                                                                                       |
| Build, merged main `d6cda64` (incl. #55)                     | 0 warnings / 0 errors                                                                                                                                                                                                              |
| Full suite, merged main                                      | **1225 passed / 0 failed / 4 skipped** (+14 from #55)                                                                                                                                                                              |
| Fresh local server (fresh bank, merged build)                | extract list/enable/mode round trip ok; invalid mode → exit 1; MCP write→search→stats ok; memory_share_extract propose+promote no crash, valid shapes; empty candidates on a fresh low-score row = correct (score 0.5 < floor 1.0) |
| Manual fresh-install protocol (published `ai-raccoon` 1.0.9) | **ALL GREEN** (install first try, layout+sha256, version, stdio round trip pending=0 proving vec0 path, dual-instance, graceful shutdown, zero silent repair)                                                                      |
| Global-tool migration (owner e:)                             | `arasz.ai-raccoon` 1.0.8 → `ai-raccoon` 1.0.9 installed; `--version` = 1.0.9+d7bd782; bank `~/.ai-raccoon/memory.db` intact (23 MB); stdio round trip on real bank: 20 tools, memory_stats shows all 4 project contexts            |
| NuGet registration                                           | 6/6 matrix RIDs serve 200 (osx-x64/linux-musl-arm64 not in matrix — expected 404s, not lag)                                                                                                                                        |

## Integrated findings (verified at merged `f9e1eea`)

No MUST-FIX from any lane. PR #55 was already merged by the owner mid-review (`f9e1eea`); findings below are ordered
follow-up work unless the owner rules otherwise.

### SHOULD-FIX

**S1 — Interval reads outside the exception shield can kill the whole server process**
(architect A3, code-reviewer F1, dotnet-engineer 2 — three independent lanes).
`ExtractionHostedService.cs:33` (initial `ReadIntervalAsync` before the timer) and `:51`
(`timer.Period = await ReadIntervalAsync(...)`) both sit outside the try/catch. A non-OCE store failure (SQLITE_BUSY
past the 5s timeout, disk I/O, encrypted bank without passphrase)
faults `ExecuteAsync` → default `BackgroundServiceExceptionBehavior.StopHost` terminates the entire MCP server (both
transports). Contradicts the class's own "best-effort" doc claim and diverges from `WatchHostedService`, which
try/catches every read. Fix: wrap both reads (or move them into the loop's try), rethrowing OCE.

**S2 — Dead knob: `extract.interval-minutes.global` is read and displayed but never written**
(architect A2, dotnet-engineer 3, test-engineer F11 — three lanes; grep-verified: zero writers in src/tests).
`extract list` advertises `interval: 60 min` that no supported channel can change, and the settings table is CLI-only by
design ("Ruling 3"). Either add
`extract interval <minutes>` (one verb + handler, matches the family) or drop the key + display.

**S3 — Cross-project duplicate shared rows within one pass** (architect A4; verified at merged source).
`ExtractionHostedService.cs:77` loads `sharedIndex` once; the per-project loop promotes without refreshing it.
`SelectSharedIndex` (MemorySql.cs:38-42) has no dedup, and
`AddContentAsync`'s dedup (`SelectEntryByPathInBucket`, project_id-scoped) cannot see project A's new shared row when
project B checks the stale snapshot — two projects holding identical content both promote → duplicate shared rows.
Breaks the service's own "dedup is exact (value/path) against the existing shared tier" contract. Pre-exists in
`memory_share_extract`
(MemoryTools.cs:289) but the sweep widens blast radius to all projects. Fix: re-read the index per project or track
promoted values/paths in-memory within the pass.

**S4 — Propose mode promises "logs ranked candidates" but logs only counts**
(architect A5). `CliCommandTree.cs:189` + reference doc promise ranked candidates; the hosted service discards
`result.Candidates` and `Log.Pass` (EventId 502) logs counts only. The feature's safety story is "propose first, review,
then promote" — in the hosted service there is nothing to review. Fix: log candidate details (path + score + reasons)
per project, or correct the help text and docs to "logs candidate counts".

**S5 — OperationCanceledException swallowed by the per-project catch**
(code-reviewer F4, dotnet-engineer 1). `ExtractionHostedService.cs:96-99` catches all exceptions per project; on
shutdown each in-flight project logs a spurious Warning +
`ProjectFailed` before the outer OCE break. Fix: filter OCE (`when
(!cancellationToken.IsCancellationRequested)`) in the inner catch, matching the outer loop's idiom.

**S6 — Poll-loop test's `count==2` encodes the fake's stale-index behavior, not production**
(test-engineer F2). `ExtractionHostedServiceTests.cs:229-232`: the fake's `Index` never updates after sharing, so the
second pass re-promotes — but in production the second pass re-reads the shared index (now containing the row) and
dedups, so count stays 1. The assertion "proves" iteration via a proxy that diverges from reality (and would fail
against correct production behavior). Fix: add a RunOnceAsync/extraction call counter to the fake and assert on that;
also makes the first assertion meaningful.

**S7 — Three hosted-service tests assert only negative outcomes** (test-engineer F3).
`RunOnce_Disabled_DoesNothing`, `RunOnce_ProposeMode_ListsCandidates_WithoutSharing`,
`RunOnce_NoProjects_NoOp` assert only `store.Shared.ShouldBeEmpty()` — a fully no-op service passes them. Add a call
counter to the fake.

**S8 — `GetProjectIdsAsync` real-SQL is untested** (test-engineer F4; dotnet-engineer's
"runs against real sqlite" claim is wrong — the port test uses the `RecordingStore` fake). One `SqliteMemoryStoreTests`
case (project + shared + workspace rows; distinct, ordered, shared-only excluded) closes the hole cheaply.

**S9 — DI registration of the hosted service is untested** (test-engineer F5).
`WatchDependenciesSmokeTests.cs:40` asserts `WatchHostedService` is registered; no equivalent for
`ExtractionHostedService` — if the registration were dropped, nothing would fail.

**S10 — `CandidateLimit = 20` hardcodes a duplicate of the MCP tool's `limit ?? 20`**
(code-reviewer F2). No shared constant, no settings key — silent drift if the tool default changes. Suggest a key
(`extract.limit.global`) or a single constant.

### NITs (aggregated)

- **First tick waits one full interval** — `extract enable true` does nothing visible for up to 60 min; CLI output
  doesn't mention the delay (architect, code-reviewer F8, dotnet-engineer 4).
- **Multi-process TOCTOU on ShareAsync** (code-reviewer F3, low, owner decision): no UNIQUE constraint on `entries`; N
  processes ticking one bank can double-insert shared rows. Fix option: partial UNIQUE index on committed rows.
- **`SelectProjectIds` lacks `project_id IS NOT NULL`** (dotnet-engineer 5; unreachable via normal writes).
- **`ParseMode`/`ParseEnabled`/`ParseIntervalMinutes` silently coerce invalid values** to safe defaults (code-reviewer
  F7, architect 8) — fail-safe, CLI validates, worth a comment.
- **Promote mode bypasses the MCP access guard** (architect 6, code-reviewer Q1) — not a client-escalation hole (no tool
  can write settings); operator warnings at enable/mode are the guard. Residual: plan's "bad seeds poison all projects
  (owner-confirmed only)" posture.
- **50 ms real-time sync in the poll-loop test** (merged `f9e1eea` shape) is a mild flake risk — the lane's 15× re-run +
  suite green suggest acceptable, but a call-counter assertion (S6) would remove the need for it.
- **Class doc 7 lines** vs the 1-3 line minimal-comments guidance (code-reviewer F8).

### Pre-existing drift found by the orchestrator (not from #55)

- **Tool-count drift**: README.md:64 "19 tools: 16 memory + 3 file-watcher" and reference doc:25 "16 memory tools plus 3
  file-watcher" — actual surface is **20 tools (17 memory + 3 watch)**, `memory_share_extract` added by #50. Missed by
  #51's ADR refresh; still present on merged main. The reference tool TABLE got the `memory_share_extract` row and the
  `extract` verb block in merged #55, but the counts and prose stayed stale.
- **`extract` family absent from root README and packaged README** (0 mentions both) — merged #55 documented it in the
  reference doc only.

## Regressions checked (all clean)

- Tool surface 20/20 via E2E parity; no MemoryTools changes in #55.
- All six PR #55 safety claims verified against code (off by default / propose never shares / promote warns / dedup
  snapshot / idempotent / no delete path).
- #54 re-pin gates non-vacuous (test-engineer F13, spot-checked): S2 file-rank, nDCG floors,
  `GateViolations` null-as-violation all real; the softened gates are tie-tolerant, not vacuous.
- EventIds 500-505 clean (no collisions vs 1-5, 100, 200-205, 300-330, 400).
- Int→long extraction fix (#51) preserved — hosted service uses the identical candidate path.
- Invariants: LoggerMessage partial classes, static-class rule, no hand-rolled null checks, no secrets, clean layering
  (Infrastructure orchestration → Core pure scorer), one host per process (no double loop).

## Verdict

**APPROVE — no MUST-FIX, no regressions, gates all green.** The parallel session's changed points integrate cleanly: the
1.0.9 package-id migration is end-to-end verified (published tool, global-tool swap with bank preserved, RID
registration), the #54 re-pin cleared the 5 corpus failures that reddened main at the last review (1206/5/4 → 1211/0/4),
and #55's hosted extraction service merges without conflict, adds 14 passing tests, and holds all its safety claims. The
10 SHOULD-FIXes are follow-up polish (2 of them — S1 host-kill risk and S3 duplicate shared rows — are worth doing
before relying on the service; S2 dead knob and S4 contract mismatch are small contract fixes). Owner decisions are
gated below.

## Owner decisions (owner-gate form: docs/work/2026-08-06-integration-review-1-0-9-review.html)

D1 shape ratification · D2 unattended-promote posture · D3 interval knob · D4 in-pass dedup (S3) · D5 host-kill
robustness (S1) · D6 test-honesty fixes (S6-S9) · D7 docs drift · D8 1.0.10 bump scope.

## Post-merge checklist (owner)

- [x] PR #55 merged (f9e1eea, by owner mid-review)
- [ ] Ruling on owner-gate decisions D1-D8
- [ ] Version bump to 1.0.10 (owner f:) — includes the S-fixes the owner selects
- [ ] Re-run fresh-install protocol after the next publish (`AI_RACCOON_VERSION=<new>`)
