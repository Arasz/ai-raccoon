# Phase 5 plan review — adversarial (base `5bca1900`)

Sources read in full: `PHASE5-PLAN.md` (146 lines), `REVIEW-ASSEMBLED.md` (843 lines),
`PHASE3-VERDICTS.md` (58), `PHASE4-CALIBRATION.md` (79). Every mechanism below was re-derived from
the working tree at `5bca1900` (clean except the campaign persona files) and, for the SDK-surface
claims, from the shipped `ModelContextProtocol` 2.2.0 assembly. No live product run, no
`~/.ai-raccoon` access. Severity = how much the finding should change the plan before it executes.

### R1 — P1.1's header claims F49 and F39; its change fixes neither [READ]

**Severity:** HIGH

**Evidence:** PHASE5-PLAN.md:19 names `(F6, F49, F39, F50)` but :20-25 changes only the warning
composition and three docs. F49 is the token minted at `<data-root>/mcp-token`
(`McpTokenFile.cs:41-44`) while the bank/log land in `<data-root>/.ai-raccoon/` — no plan row moves
it, ignores it, or documents it. F39 is the mistyped `--data-root` minting a bank (`CliWriteOptOuts.cs:16`
exempts only `encryption`); no plan row touches the auto-start policy. Wave 3 and Wave 4 do not
mention either id.

### R2 — The two default-firing retrieval MEDIUMs have no package at all [READ]

**Severity:** HIGH

**Evidence:** PHASE4-CALIBRATION.md lists F19 and F20 under "Fire by default (no configuration
needed)". A search of PHASE5-PLAN.md finds neither id; there is no Lane B package anywhere in the
plan (F5, F19, F20, F21 all absent, despite the record's four Lane B owner questions). F20's
mechanism (`limit=100` serving 41 under the default 0.6 relative floor) is exactly the shipped
default, and the `limit` description still promises "Maximum results (default 8)"
(`src/AiRaccoon/Tools/MemoryTools.cs:139-141`).

### R3 — P1.3's F70 fix is not implementable as written and does not stop the measured adversary [MEASURED]

**Severity:** HIGH

**Evidence:** `McpTokenGate.cs:31` keeps only `/observability` open; every `/mcp` call without the
token gets a 401 JSON-RPC body — which is why the current probe counts it as `Answered`
(`ServerProbe.cs:69`). I found no `server/discover` in `ModelContextProtocol` 2.2.0
(`Directory.Packages.props:12-13`; case-insensitive scan of the net10.0 DLL = 0 hits), and reading
`serverInfo.name` requires completing an MCP handshake, i.e. transmitting the token first. The only
pre-token identity is the self-asserted `/observability` name already used at `ServerRestart.cs:69-70`,
which a squatter can return; the plan's "fall back to a warning" would then hand the token over anyway.
Changing the shared `ProbeAsync` verdict also alters `serve` attach and `WaitForPortToFreeAsync`'s
`NotListening` requirement — the fix must live at the token-send decisions, not in the probe.

### R4 — P2.1's tombstone statement would push workspace-scope tombstones and delete peers' scratch rows [READ]

**Severity:** HIGH

**Evidence:** The proposed `SELECT DISTINCT … COALESCE(scope,'workspace') … WHERE <same predicate>`
(PHASE5-PLAN.md:71) mirrors `DeleteCoreAsync`'s predicate `MemorySql.cs:167-168`, which has no
workspace filter. `StripNonSyncableAsync` deletes workspace *entries* only (`SyncService.cs:620`) —
tombstones still push; the receiver applies them with no workspace guard
(`SyncService.cs:520-523`: `DELETE FROM entries WHERE (hash, COALESCE(scope,'workspace'), project_id)
IN (SELECT hash, scope, … FROM remote.sync_tombstones)`). One `memory_delete` of a workspace/committed
twin therefore tombstones the scratch row too, deleting same-hash workspace rows on other replicas and
leaking the scratch content hash off-machine. The safe model is `ProjectIdsRepair.cs:203-207`
(`WHERE … AND scope IN ('project','custom','shared')`).

### R5 — `INSERT OR IGNORE` diverges from `UpsertTombstone`'s refresh semantics [READ]

**Severity:** MEDIUM

**Evidence:** The existing writer is `MemorySql.cs:186-188`:
`INSERT … ON CONFLICT(project_id, hash, scope) DO UPDATE SET deleted_at = excluded.deleted_at`. The
plan's derived insert uses `INSERT OR IGNORE`, so a delete-after-recreate leaves the *older*
`deleted_at` in place. With P2.2's `created_at <= deleted_at` guard, the second deletion can then fail
to suppress a peer's re-created row that is newer than the first tombstone but older than the second.
The derived statement should use the same upsert shape, not OR IGNORE.

### R6 — P2.2 guards the apply-DELETE but not the merge-suppression leg [READ]

**Severity:** MEDIUM

**Evidence:** The measured self-delete is fixed by the `created_at` guard on `SyncService.cs:520-523`,
but a replica that already holds the tombstone still suppresses the remote re-created row through
`NOT EXISTS (SELECT 1 FROM sync_tombstones …)` at `SyncService.cs:450-455`, which has no age
comparison. Convergence then depends on tombstone GC running in a *later* pull (gc at :533-539);
the plan's single-bank gate cannot see that window. The F31 fix is sufficient for the finding as
measured, partial for multi-replica convergence.

### R7 — P1.1 breaks a pinned test and has no memory-leg warning channel [READ]

**Severity:** HIGH

**Evidence:** `MemorySearchKindToolTests.Search_KindMemory_HasNoEngineNotConfiguredWarning`
(`tests/…/Unit/Mcp/MemorySearchKindToolTests.cs:272-280`) asserts `Warning.ShouldBeNull()` for
`kind=memory`; the plan's file list omits this test, so the wave lands red until it is rewritten.
The channel also does not exist: `SearchResults` (`src/AiRaccoon.Core/Memory/SearchResults.cs:11-16`)
and `SearchDispatchResult.cs:15` carry only `CodeWarning`, and `MemoryTools`' primary constructor
(`MemoryTools.cs:22-27`) has no settings port — composing a provider-unset warning needs the store,
dispatcher and 26 test constructions touched, none listed.

### R8 — F6's remedy also leaves `McpServerInstructions` pitching hybrid memory [READ]

**Severity:** MEDIUM

**Evidence:** `src/AiRaccoon/Setup/McpServerInstructions.cs:14-25` — the `initialize` instructions
every agent receives — says the server is "hybrid keyword + semantic search over notes you write" and
names only the *code* engine command. F6's evidence line names this file; P1.1's file list
(PHASE5-PLAN.md:23-25) does not. After the wave, an agent on a fresh install still relays a code-only
remedy and believes semantic memory is on.

### R9 — F38's fix, scoped to `ServeArguments`, changes the proxy path too [READ]

**Severity:** MEDIUM

**Evidence:** Both the proxy and the CLI settings verbs call `BackendLaunchArguments.ServeArguments`
(BackendSessions.cs:71; CliSettingsBackend.cs:50), so adding a short `--idle-timeout` there stops the
backend under an idle interactive client, not just CLI orphans; ProxyForwarder re-acquires on a lost
backend, so it self-heals with a cold start, but that is a behavior change to the primary path the
plan does not mention. "Stop the backend the CLI started" needs ownership plumbing: `BackendResult`
(`IBackendLauncher.cs:12`) carries no PID and `Start` (`BackendLauncher.cs:132-160`) discards the
`Process`. I found no concrete `serve --restart` breakage — that risk is sited on the wrong path.

### R10 — Three Wave 3 rows offer two options; their watched-red gates assert one [READ]

**Severity:** MEDIUM

**Evidence:** F24's change says "Refuse (or make `workspace_id` win)" while its gate asserts "the row
lands in the outbox" (PHASE5-PLAN.md:110). F52 says "an absolute-relevance floor, or mark the response
as unranked" while its gate accepts "empty or explicitly marked" (:107). F7 says "the documented
open-failure code" while its gate says "exit 2 (or the documented code)" (:111) and Lane E's question
(correctly) asks whether `FailedToOpenEncryptedBank` is the right code for a corrupt bank at all. Each
pair needs one owner ruling before the gate can be watched red.

### R11 — F9's gate is not watchable red as written [READ]

**Severity:** LOW

**Evidence:** PHASE5-PLAN.md:125 says "lower to 1050/27 and watch it still pass" — but the cap is
currently 1066/27 (`SqliteMemoryStoreSizeRatchetTests.cs:80-81`) against a measured 1046/26, so the
test already passes and 1050/27 has *more* headroom. A watched-red gate for a ratchet requires a cap
below the measurement (e.g. 1045) seen failing, then the real value. The row also contains the
contradictory phrase "the cap test must fail on the current headroom".

### R12 — F51 and F53 are remedy *strings*, not doc rows, and F53's remedy text is wrong for two refusals [READ]

**Severity:** MEDIUM

**Evidence:** F51's refusal is produced at `src/AiRaccoon/Settings/ServerSettingsStore.cs:231`
("refused this credential — it may serve another data root") with no `--port` remedy; a doc-sweep
assertion cannot fix or see it. F53's plan row (:109) appends "`settings access …`" to access-denied
*and* watch/scope refusals; the actual remedies differ — `settings ingest scope add` for
path-outside-scope and `settings watch enable` for watching-disabled, as the record's own shortest-fix
sentence ("the CLI pointer to the other two") says.

### R13 — F42 and F62 are placed in the Docs sweep under a gate that cannot verify them [READ]

**Severity:** MEDIUM

**Evidence:** PHASE5-PLAN.md:127 lists F42 ("quiet.log single writer") and F62 beside F43/F50/F51 and
gates the set with "each doc claim asserted by the existing exit-table/ToolInventoryTests pattern".
F42 is a two-writer file-corruption defect (`BackendLaunchArguments.cs:53` makes the spawned backend
inherit `--quiet`; `QuietLogging.cs:30` fixes one path; QuietFileLoggerProvider locks per process).
F62 is `tests/…/TestHelpers/FakeMemoryStore.cs:34-36` discarding the `scope` argument. Neither is a
doc claim; the planned gate leaves both unverified.

### R14 — F66's "extend to the 33" ignores the corrected CI-dependency failure [READ]

**Severity:** MEDIUM

**Evidence:** The Phase 3 correction (G6) records 35 collected under the CI dependency set, 34 fully
green, and one failing test — `test_llamaindex_harness_cli.py::test_ingest_runs_as_module` (chromadb
absent). PHASE5-PLAN.md:120 extends the list or records a reason but never decides which; the
file-inventory gate passes even if the extension reds CI, so the package can land with a red lane and
a green gate.

### R15 — F63's reconciliation does not say which package id counts as "installable" [READ]

**Severity:** LOW

**Evidence:** The Phase 3 correction says six 1.0.x releases live under the former
`arasz.ai-raccoon` id and 15 have no package under either id. PHASE5-PLAN.md:122 reconciles "every
`VERSION`/tag against nuget.org" and its gate dry-runs "the 21 known-missing versions" — a
current-id-only check, which would report the six former-id releases as failures and cannot
distinguish them from the 15. Owner decision 6 asks whether tagged-but-unpublished is acceptable but
not which id satisfies "published".

### R16 — The merge-conflict marker in the file P1.1 edits is left in place [MEASURED]

**Severity:** LOW

**Evidence:** `docs/reference/agent-memory-server.md:212` is exactly `>>>>>>> origin/main`, verified
present at `5bca1900`; the same file is in P1.1's edit list (PHASE5-PLAN.md:24). One line, in the
canonical agent-facing contract, already prescribed as OPS-17 four weeks ago (F1). Leaving it while
editing the file is not defensible.

### R17 — F8's retry-diagnostic half has no change, only a gate parenthetical [READ]

**Severity:** MEDIUM

**Evidence:** PHASE5-PLAN.md:123 gives F64 a runner so the flake ledger is live and appends
"(F8 gate)" for "a retry-recovered failure emits a diagnostic". F8's verified mechanism is that
`RetryFact` recovery leaves no trace (`RetrySurfaceGateTests.cs:20-38`; 1835 retry-attribute sites),
so the diagnostic emission is a separate change in the retry machinery with no file named anywhere in
the plan. The ledger runner alone leaves "passed on the third try" invisible.

## Coverage matrix

| id | package | sufficient? | note |
|---|---|---|---|
| F1 | none | no | merge marker left in a file P1.1 edits (R16) |
| F2 | none | no | @ignore BDD scenario; deferrable |
| F3 | ratified residual | n/a | register + Phase 3 correction |
| F4 | Wave 4 | yes | — |
| F5 | none | no | ORT provenance/recapture decision absent |
| F6 | P1.1 | partial | unit test pinned, no warning channel, instructions file missed (R7/R8) |
| F7 | Wave 3 | partial | exit-code choice undecided (R10) |
| F8 | F64 row | partial | diagnostic half missing (R17) |
| F9 | Wave 4 | partial | gate not watchable red (R11) |
| F10 | Wave 4 | yes | consistent with Phase 3 correction; decision-gated |
| F11–F16 | none | no | structural cleanup; deferrable |
| F17 | positive | n/a | nothing to fix |
| F18 | withdrawn lead | n/a | open follow-up, not a defect |
| F19 | none | no | default-firing MEDIUM (R2) |
| F20 | none | no | default-firing MEDIUM (R2) |
| F21 | none | no | trust-boundary owner question |
| F22 | P1.2 | yes | mechanism verified (`MemoryWriteService.cs:64-65`) |
| F23 | none | no | has a ratified plan (WP1); not referenced |
| F24 | Wave 3 | partial | gate contradicts the two options (R10) |
| F25 | P1.2 | yes | — |
| F26 | Wave 3 | partial | "or advertised N" arm does not make the write deletable |
| F27–F28 | none | no | contract/nit; deferrable |
| F29 | P2.1 | yes | resurrection fixed; package adds the R4 leak |
| F30 | P2.1 | partial | derivation unsafe for the workspace twin (R4) |
| F31 | P2.2 | partial | apply arm fixed; merge leg unguarded (R6) |
| F32–F34 | none | no | doctor contract/legacy shape questions; deferrable |
| F35 | P2.1 | yes | — |
| F36 | none | no | two-machine clock arm deliberately out of scope |
| F37 | Wave 3 | yes | 130 conventional; "command did nothing" in gate |
| F38 | Wave 3 | partial | shared `ServeArguments`; stop-mechanism unplumbed (R9) |
| F39 | P1.1 (claimed) | no | no change anywhere (R1) |
| F40–F41 | none | no | watch-gated race / logging convention |
| F42 | Wave 4 docs | no | code defect under a doc gate (R13) |
| F43 | Wave 4 docs | yes | — |
| F44–F49 | none | no | docs/nits; F49 claimed by P1.1 but absent (R1) |
| F47 | P1.1 docs (implicit) | partial | file named, id not |
| F50 | P1.1/Wave 4 | yes | tutorial `sessionId` |
| F51 | Wave 4 docs | no | runtime string, not a doc (R12) |
| F52 | Wave 3 | partial | floor-vs-warning undecided (R10) |
| F53 | Wave 3 | partial | remedy text wrong for two refusals (R12) |
| F54–F60 | none | no | docs/nits/test-gate; deferrable |
| F61 | Wave 4 docs | yes | drop `--nologo` |
| F62 | Wave 4 docs | no | test-fake fix under a doc gate (R13) |
| F63 | Wave 4 | partial | former-id handling undecided (R15) |
| F64 | Wave 4 | partial | F8 half missing (R17) |
| F65 | Wave 4 | yes | — |
| F66 | Wave 4 | partial | corrected CI-dep failure unhandled (R14) |
| F67 | Wave 4 | yes | add `mkdir -p dumps` + upload |
| F68–F69 | none | no | release hygiene; cheap, deferrable |
| F70 | P1.3 | no | mechanism unimplementable/ineffective (R3) |
| F71 | ratified residual | n/a | S2/O3 acceptance |
| F72 | Wave 3 | partial | caller-text vs log-text contradiction; files unnamed; shadow log untouched |

## Still open

**Findings with no planned fix — indefensible as omitted:** F19, F20 (both fire by default; the plan
has no retrieval lane), F39 and F49 (listed in P1.1's header, changed nowhere — the exact
"claims to fix but does not" case), F51/F42/F62 (named in the docs sweep but they are a refusal
string, a two-writer file defect and a test-fake behaviour), F1 (one line in a file the plan edits),
F8's retry-diagnostic half. **Defensible deferrals:** F2, F11–F16, F21, F23 (a ratified WP1 exists
but is not cited), F27/F28, F32–F34, F36 (out of scope by the plan), F40/F41, F44–F48, F54–F58,
F59/F60, F68/F69 — all non-default or cosmetic, provided the plan records *why* rather than silence.
F3/F71/S6b/ADR-0102 (F10)/"no engine by default" are correctly left to the register.

**Owner decisions missing that block or shape a package:** F24 refuse-vs-workspace-wins (its gate
already assumes the latter); F52 absolute-floor-vs-warning (a floor is a new retrieval-semantics
ruling, a warning is not); F7 which exit code a corrupt bank returns (`FailedToOpenEncryptedBank` is
semantically wrong); F26 chunk-vs-path as the delete unit; F42 quiet.log single-writer ownership;
F8/F64 revive-vs-remove the flake ledger; F63 which package id satisfies "published". The six listed
decisions (F22, F31, F38, F70, F9/F10, F63) are the right ones as far as they go; F70's decision
should also settle what "fall back to a warning" means, since that fallback keeps the confirmed
vulnerability for every existing backend.

**Sequencing:** Wave 1 → 4 is defensible (default-firing first, sync second, MEDIUMs third,
process last), and within Wave 1 the packages are independent. Two adjustments would reduce risk:
ship P2.2's `created_at` guard with, or ahead of, P2.1's wider tombstone derivation (P2.1 multiplies
tombstone shapes while the re-create protection is separate), and split the Wave 4 docs sweep into
doc-only rows (F43/F50/F61) versus code/test rows (F42/F51/F62) with their own gates.

**Not verified by this review:** no live product run was performed, so the R4 workspace-tombstone
leak is derived from source, not reproduced end-to-end; F70's squatter was not re-run; the R9 proxy
idle-timeout consequence is traced, not measured. Everything else is cited path:line and can be
re-checked read-only.
