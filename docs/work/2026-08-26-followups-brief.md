# Implementation brief — task `doctor-feature-match-cont` (integrated after review)

2026-08-26. Source plan: `docs/work/2026-08-26-followups-plan.md`. Reviews: `…-R1-arch-review.md`,
`…-R2-qa-review.md` (both committed). This brief is binding; where it differs from the plan, the
brief wins and the difference is the review finding.

## WP1 — the reset guard (TDD, RED first)

Rulings confirmed: guard in `SettingsEndpoint.MapDelete` (SettingsEndpoint.cs:54-65) when
`key == EmbeddingSettingsKeys.Provider && HasOpenModelMigrationAsync` → throw
`ModelMigrationInProgressException(FROZEN_MESSAGE)` → `Results.Conflict(ex.Message)` (mirror
MapPost :84-87). Client: `ServerSettingsStore.DeleteSettingAsync` maps 409 →
`ModelMigrationInProgressException(body)` (mirror StartModelMigrationAsync :96-99).
CLI: catch INSIDE `ModelResetAsync` (wrap the foreach), write `ex.Message` to stderr
unreformatted. `ExitCode.ModelResetRefused = 25` with the doc comment from plan A3.

FROZEN MESSAGE (verbatim, travels endpoint → 409 body → stderr):
`ai-raccoon: model reset refused: a model migration is in progress — every MCP tool call is refused until it finishes; nothing was deleted`

Tests (TDD; the two RED witnesses MUST be run and captured before production changes):
1. `ModelReset_WithAnOpenMigration_IsRefused_Exit25_DeletesNothing` — `[RetryTheory]` over BOTH
   verb spellings (`settings model reset`, `settings model embedding reset`; ConfigCommands.cs:58,62).
   Asserts: exit 25; **stderr == the frozen message exactly** (R2 F2); stdout lacks the success
   line; the six keys still present; migration row still open. RED today: exit 0, keys gone.
   The test asserts literal `25` at RED time; the const lands in the same commit (R2 F1).
2. `ModelReset_NoOpenMigration_StillResets_Exit0_ClearsAllSixKeys` — `[RetryTheory]` over both
   verbs. Asserts exit 0 + all six keys gone + migration row absent/closed.
3. `Delete_NonProviderKey_WhileMigrationOpen_StillSucceeds` — direct endpoint DELETE of
   `embedding.model` → 204/OK while the migration is open (SyncCommands and friends unaffected).
4. `ModelReset_StoreThrowsModelMigrationInProgress_Exits25_WithTheUnprefixedMessage` — Fast,
   dispatcher-level, fake store throwing `ModelMigrationInProgressException` on
   `DeleteSettingAsync`; asserts exit 25 and stderr starts with `ai-raccoon: ` exactly once (no
   doubled prefix). RED today: exit 15 with doubled prefix (verify by running).
5. `ServerSettingsStore_DeleteSetting_OnConflict_ThrowsModelMigrationInProgressWithTheReason` —
   Fast/Unit direct client-mapping test (precedent SettingsEndpointTests.cs:220-236): the 409
   body must surface as `ModelMigrationInProgressException` carrying the frozen message.
6. `ModelEmbeddingSet_WhileMigrationOpen_StillExitsInvalidArgument` — Fast, pins the
   OUT-OF-SCOPE behavior: `model embedding set` while open keeps today's exit 15 (a
   dispatcher-level catch would silently change it to 25 — R2 F3).

Fixture determinism: seed `embedding.provider/.model` + open `model_migration` (id=1,
`finished_at NULL`, `lease_owner='test-holder'`, `lease_expires_at = now + 1h`) by raw SQL
`DELETE`+`INSERT` inside the test method; the acquire WHERE `(lease_owner IS NULL OR
lease_expires_at < @now)` (MemorySql.cs:501-503) blocks the real maintenance relay. The bank is a
live file under a running server — keep the seed transaction short and set a busy timeout (R2
F11); `[RetryFact]`/`[RetryTheory]` + `[Integration][Slow]` traits per the retry-surface rule.

Harness truth (R2 F6): `SettingsChannelExitCodeTests` (Unit+Fast) already drives the real
`ServerSettingsStore` through `ConfigCommands` at argv level, and `DelegatingMemoryStore` exists
in Integration/Setup — use these; do NOT invent a new adapter. The Integration tests copy the
`SettingsEndpointTests` harness (`McpServerSetup.CreateWebHost`) and assert the CLI→HTTP→
endpoint→`SqliteSettingsStore` seam end-to-end.

Acceptance: (a) both verbs exit 25 + frozen message verbatim + delete nothing while open;
(b) both verbs still exit 0 + clear six keys when closed; (c) non-Provider deletes unaffected;
(d) `model embedding set`-while-open stays 15; (e) both RED runs witnessed; (f)
`CliCommandsDoNotOpenTheBankTests` green; (g) no event-id changes.

## WP2 — checklist JSON completion

File: `docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json` — update `_note` and
`derived.run/version/pr` to the 1.36.1 run (R1 F9). Rows: keep the 7, add the four R3 rows and
the new `model-reset-refused-while-migration-open` row → **12 rows**, with these rulings:
- Blank-provider row: REWORD per R1 F5 — "cannot be newly created through any supported product
  surface after 1.36.1" (birth path is one transaction; reset is refused); keep the raw-SQL seed,
  the 1012/no-526/finished_at-NULL assertions, and fold in `settings model reset` → EXIT=25.
- Decision recorded (R2 F10a): the live-stuck-bank leg is covered by `stuck-bank-reproducible-on-
  copy-exit-24` (copy is strictly safer than live) and the stale-lease drain case by
  `migration-drain-logs-1008-1013` (its command greps the 1009 line) — no extra rows.
- Fix draft row 3's command copy-path/data-root mismatch; spell out the new row's exit-0 leg
  (close-the-row SQL then reset succeeds) (R2 F10c).
Gate: JSON parses, exactly 12 ids, all 7 fields present (the `python3 -c` one-liner from the plan,
with `==12`).

## WP3 — derive gates

New Fast class(es) in `tests/AiRaccoon.Tests/Unit/Setup/Diagnostics/`, `[Unit][Fast]` traits:
- `HowToExitTableTests` — regex-anchor to the `| Exit code | Meaning |` table header (R2 F12),
  NOT any backticked-number table. Set half: hand-enumerated consts BY NAME (Success,
  FailedToResolveEncryptionKey, FailedToOpenEncryptedBank, SchemaVerificationFailed,
  SchemaNewerThanBinary, NoBank, ModelMigrationOpen) (R1 F6). Phrase half: rows 0/1/2 are
  table-own claims (no doc comments — R1 F1/R2 F4); rows 19/20/22/24 cross-check the Meaning
  cell against the const's doc comment with these pinned phrases, asserted on BOTH sides:
  19 → "the bank's actual schema", 20 → "user_version", 22 → "no bank file exists at the
  resolved path", 24 → "every MCP tool call is refused until" (R2 F8). RED witnesses (runtime):
  delete the 24 row; renumber 24→25 in the doc; reword a Meaning. ("Rename const" is
  compile-time protection, not a witnessed RED — R2 F7.)
- `HowToSettingsExitTableTests` — same pattern for the SETTINGS table
  (configure-ai-raccoon-server.md:185-189): rows 17/18/23 + the new 25 (R1 F2 MUST).
- Same-PR prose fix (R1 F13): "non-zero on a mismatch" → the post-24 wording.

## WP4 — release + docs

- `python3 scripts/version-bump.py patch` → 1.36.1; `VersionContractTests` gate with
  `--minimum-expected-tests <exact>` (R2 F9).
- README What's new one-liner (defect-fix voice, names exit 25); PR title carries the guard.
- `docs/work/README.md`: index rows for the new follow-ups docs (research, plan, 2 reviews,
  brief) (R1 F10).
- `docs/how-to/configure-ai-raccoon-server.md`: settings exit table gains the 25 row in WP1's
  commit (R1 F2); the "non-zero on a mismatch" prose fix (R1 F13).
- Residual-risk row in the PR body: already-stranded banks (like the owner's pre-drain state)
  still need manual remediation — out of scope here (R1 F15); set-verb partial writes
  (ApiKey/Dimensions mutated before a refused set) are pre-existing out-of-scope behavior (R1 F7);
  the guard's doc comment notes future server-side `IMemoryStore.DeleteSettingAsync` callers
  bypass the endpoint guard (R1 F12).

## Gates (all with `--minimum-expected-tests`, exact counts observed at GREEN then verified to
fail with a wrong number — R2 F9)

- `TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~ModelResetGuardTests|FullyQualifiedName~ModelResetGuardEndpointTests" -v m --minimum-expected-tests <n>`
- `TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~HowToExitTableTests|FullyQualifiedName~HowToSettingsExitTableTests" -v m --minimum-expected-tests <n>`
- Fast lane `Speed=Fast&Performance!=Benchmark` with a loose floor (~3300); VersionContractTests
  with the exact count; `dotnet build` clean. NEVER `--nologo`.
