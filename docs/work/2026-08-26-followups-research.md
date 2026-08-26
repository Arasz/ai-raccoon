# Research record — task `doctor-feature-match-cont` (three follow-ups)

2026-08-26. Continuation of `doctor-feature-match` (merged as #591, v1.36.0). Every fact cites
its source; the previous task's research/lane/review docs (committed on main under
`docs/work/2026-08-26-doctor-parity-*`) are the base evidence and are not re-derived here.

## The three follow-ups (owner-scoped)

1. **Block `model reset` while a model migration is open** (issue #592; R1 M8/Ruling 5-6). Owner
   ruling: the SIMPLER option — refuse, do not close the outbox row, no ADR amendment needed.
2. **Complete the manual live-checklist rows** for the exit-24 surface (R3 block 3 vs the 7-row
   draft in `docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json`).
3. **Derive-gate the how-to exit table** (R2 N7 / R1 S10): a Fast test comparing the table in
   `docs/how-to/configure-ai-raccoon-server.md` against the `ExitCode` consts doctor can return.

## Follow-up 1 — where the guard can and cannot live (traced, not guessed)

- CLI verb: `ConfigCommands.cs:58,62` → `SettingsCommands.ModelResetAsync(store, streams, ctx)`
  (`SettingsCommands.cs:260-275`): `foreach` over the six `EmbeddingSettingsKeys` consts
  (Provider FIRST, then Model/BaseUrl/Engine/ApiKey/Dimensions) calling `store.DeleteSettingAsync`.
- `store` is the settings-server client proxy (`ServerSettingsStore`/`LazyServerSettingsStore`);
  `CliWriteOptOuts.cs` allows exactly ONE CLI command family to open the bank directly
  (`encryption`), and `CliCommandsDoNotOpenTheBankTests` derives that from `ConfigCommands`'s
  constructor — so **the CLI cannot do the check itself**.
- `ServerSettingsStore.HasOpenModelMigrationAsync` (`ServerSettingsStore.cs:107-109`) **throws by
  design** when a CLI caller invokes it ("the CLI never calls it") — a deliberate
  ToolGate-server-side-only check.
- Writes route: CLI → settings server HTTP → `SettingsEndpoint.cs:62` (per-key DELETE handler)
  → server-side `SqliteMemoryStore.DeleteSettingAsync` (`SqliteMemoryStore.cs:567`) →
  `SqliteSettingsStore.DeleteSettingAsync` (`SqliteSettingsStore.cs:43`).
- Consequence: the guard belongs **server-side**, in the delete path. Two candidate shapes:
  - (a) in `SettingsEndpoint`'s delete handler: refuse when the key is
    `EmbeddingSettingsKeys.Provider` and `HasOpenModelMigrationAsync` is true (server-side store
    has the check; the endpoint already has a store reference). Provider is deleted FIRST in
    `ModelResetAsync`, so one refusal aborts the whole reset before any key is gone — atomic by
    ordering, no partial state.
  - (b) in `SqliteSettingsStore.DeleteSettingAsync` itself: refuse deleting
    `embedding.provider` while a migration is open — the class-level guard covering every future
    deleter (SyncCommands etc. delete other keys, so they are unaffected).
  - Owner's "just block reset" fits both; (b) is the class-level fix, (a) is narrower. The plan
    must pick one and justify (fix-the-class vs minimal-surface).
- Error surface: the settings client maps server errors to `SettingsServerError = 23`
  (`ExitCode.cs:68-71`); the refusal message must be actionable ("model migration in progress —
  model reset refused until it finishes"). Whether the endpoint should return a distinct status
  (409) and whether the CLI needs a distinct exit code (new const 25?) or 23 suffices is a plan
  ruling — `ExitCode` doc comments record rationale (precedent 17/18/23).
- A migration can never be born with a blank provider (`SqliteMemoryStore.ModelMigration.cs:28`
  guard), so the ONLY route into the blank-provider-open-migration state is reset-while-open;
  blocking reset closes the state for good — which means the relay's blank-provider throw-loop
  (1012, P4 §2 S3) becomes unreachable-by-design after this fix.

## Follow-up 2 — checklist diff (R3 block 3, 9 rows, vs the 7-row draft)

Semantic mapping (names differ; content overlaps): the draft covers fresh-install, healthy
11-line, stuck-bank-on-copy (exit 24 + SHA-256), settled→0, shape-mismatch→19, drain logs,
registry+drift gates. **Missing rows to add:**
- `doctor-memory-rows-pending-is-alive` — 0 → N after a real ingest with no engine, checked
  against an independent read-only COUNT (the honest-count bar, R2 B2).
- `doctor-unreadable-settings-says-unreadable-not-a-false-remedy` — a shape-broken settings
  table must print `unreadable (settings table missing or unreadable)`, not a false
  "not configured — run …" remedy (R1 S6).
- `doctor-unreadable-migration-falls-back-to-exit-0` — unreadable migration row must exit 0,
  not 24 (R1 Ruling 4 Decision D; R2 B3).
- `blank-provider-migration-warns-once-and-stops-the-throw-loop` — **interacts with follow-up 1**:
  after the reset guard, this state is unreachable-by-design. The plan must rule: keep the row
  (verify the guard test instead) or reword it as "unreachable — proven by the reset-guard
  test". Do not leave a checklist row that can never be executed.
- R3's `docs-match-the-shipped-binary` and `migration-drain-reports-stale-lease-*` are covered
  by the draft's registry/drift-gate and drain-log rows (optionally tightened).

## Follow-up 3 — the derive gate (precedents verified)

- Table to pin: `docs/how-to/configure-ai-raccoon-server.md` ("Diagnose a bank's schema" section,
  `:357-365` area after #591) — rows 0/1/2/19/20/22/24 with a Meaning column; prose above says
  exit code "composes into a script".
- `ExitCode.cs:78` has `ModelMigrationOpen = 24`; doctor-reachable set is
  {0 Success, 1 FailedToResolveEncryptionKey, 2 FailedToOpenEncryptedBank, 19
  SchemaVerificationFailed, 20 SchemaNewerThanBinary, 22 NoBank, 24 ModelMigrationOpen}.
- Precedent patterns in-repo: `DefaultCodeModelCommandTests` (const ↔ CLI parse/quote pinning,
  `tests/AiRaccoon.Tests/Unit/Setup/DefaultCodeModelCommandTests.cs`), `ToolInventoryTests`
  (derives a doc inventory), and the sample drift gate from #591
  (`CorpusEngineLinesTests.HowToHealthyBankSample_MatchesTheReportContract` — regex-extracts a
  fenced block from the how-to, asserts line-by-line).
- Minimal honest shape: parse the table's numeric codes and Meaning cells; assert (a) the code
  set equals the doctor-reachable consts, (b) each row's Meaning contains the const's doc-comment
  key phrase (e.g. `ModelMigrationOpen`'s comment) — so a rename or a meaning drift goes RED, not
  just a missing row. Test lives in `AiRaccoon.Tests.Unit.Setup` (Fast), next to the drift gate.

## Constraints that bind all three

- TDD mandatory; RED witnesses required (the reset-guard test's RED: reset succeeds today, bank
  left locked — that is the defect).
- User gates for fix PRs: version bump (patch → 1.36.1) + tests + MANUAL live test (checklist).
- `CliCommandsDoNotOpenTheBankTests` — no new CLI-side bank access.
- `ExitCode` additions need doc comments citing the issue; event-id registry untouched (no new
  log ids expected unless the refusal path needs one — prefer none; the settings-server error
  surface already carries the message).
- All work in the task worktree `.ai-badger/worktrees/doctor-feature-match-cont` on branch
  `task/doctor-feature-match-cont`; one PR; no push to main.
