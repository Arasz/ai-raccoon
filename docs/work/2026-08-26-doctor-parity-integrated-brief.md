# Integrated implementation brief — task `doctor-feature-match`

2026-08-26. State: planning + MoE review complete; implementation lanes running. This brief is the
single source of truth for what ships; it supersedes the lanes where the review ruled differently.

## Frozen contract (R1 M1/M2/N4 — P1 wins; R2 J2/J3)

- **Migration line subject: `model migration:`** — never `memory migration:` (the table is
  bank-wide single-row; `MemorySql.SelectModelMigration`, DDL `id CHECK (id = 1)`).
- **Qualifier: `(all MCP tool calls are refused until it finishes)`** — ToolGate refuses *every* tool
  (`ToolGate.cs:23-30`), not memory tools.
- **Line order: engine lines adjacent** (code engine, then memory engine) so the live bank's
  `embedding.model == embedding.codeModel` accident is visible. Positions per P1 §2.4 (4, 7, 9).
- **Memory engine arms:** `memory engine: bundled` (bare — no parenthetical) · `memory engine:
  <dir> (<manifest name or leaf>)` (local) · `memory engine: openai:<model> (https://…)` (remote) ·
  not-configured when `embedding.provider` is absent (wording consistent with `model embedding show`'s
  `provider: (none — FTS5-only search)`) · em-dash suffix ` — no API key set; run 'ai-raccoon model
  embedding set openai <model> --api-key <key>'` when the remote engine's key is absent.
- **Grammar: `Value`, `?Detail`, `?Suffix`** — parenthetical only when Detail non-null, em-dash
  clause only when Suffix non-null. Nothing invented; renderer derived from the frozen literals.
- **`model migration: none open` is printed unconditionally** (R3 §4.2(b)) — line count stable
  across states; a line that only appears in trouble is the line nobody greps for.

## Exit code (R1 Ruling 4; R3 §4.3; R2 B3)

- `ExitCode.ModelMigrationOpen = 24` (24 is free; highest is 23). Doc comment cites task + review.
- Reachable **only** from the `Healthy` arm of the existing switch; 19/20 keep precedence.
- **Only** on a positively-read open row. Absent table / no row / `finished_at` set / `SqliteException`
  → `none open` arm + `Success` (0).
- Status when open: `status: MIGRATION IN PROGRESS (schema shape is healthy; MCP tool calls are
  refused until the re-embed finishes)`. `status: HEALTHY` never prints alongside a non-zero exit.
- Doc: how-to's `Exit code is 0 when healthy and non-zero on a mismatch…` sentence loses the word
  `mismatch` and gains the 24 row in the same commit (S10/N7: prefer a derive test over the table).
- Release notes MUST call exit 24 out; scripts migrate with `rc == 0 || rc == 24`.

## Component shape (R1 Ruling 1; S1/S2/S3; R2 J2)

Exactly these new declarations, in `src/AiRaccoon` (CLI project), namespace `AiRaccoon.Setup.Diagnostics`:

| type | kind | purpose |
|---|---|---|
| `CorpusEngineProbe` | internal sealed record, plain string fields + `EmbedCorpus` | per-corpus descriptor: label, configured-key, model-key, base-url-key, corpus table, pending SQL, not-configured text. NO `Func`/delegate. Keyed on the EXISTING `EmbedCorpus` enum (`Infrastructure.Embedding`); label via `corpus.ToString().ToLowerInvariant()` |
| `CorpusEngineState` | internal sealed record | `Value`, `Detail?`, `Suffix?`, `PendingRows` |
| `CorpusEngineLines` | internal static class, pure functions only | the single renderer; arm switch inside it |

The reader stays a `private static` method on `DoctorCommands` (owns the connection). API-key
presence read as `EXISTS`, never the value (S5). No UnixTime extraction (S8) — a one-line private
with a comment naming the `WatchCommands` twin. Doctor takes **no new log id** (Ruling 3).

## Tests (R2 — binding)

- Characterisation first: pin today's exact lines byte-identical BEFORE extraction (R2 B4 — the
  lane's R1 fixture referenced non-existent types; split: commit 1 pins, then extract).
- Anti-swap proof: keys swap, query swap, **PendingTable/guard-only swap** (R2 M3 — the mutation P3
  never named), intra-descriptor transposition (R2 M6 — `ProviderKey` ↔ `ModelKey` on memory) —
  driven directly against the probe in an **Integration + Fast** class (R2 J11); one argv
  end-to-end stays in the Slow doctor class.
- Honest counts: seed **mixed** states (3 pending + 2 embedded in `entries`; 2 pending + 1 embedded
  in `code_entries`) and assert pending numbers while totals differ (R2 B2).
- Exit precedence: open+healthy ⇒ 24 + `MIGRATION IN PROGRESS` + `ShouldNotContain("status:
  HEALTHY")`; open+shape-broken ⇒ 19 and `ShouldNotBe(24)`; migration read unreadable ⇒ 0 (R2 B3).
- Whole-line assertions (`Lines(outp).ShouldContain(<whole line>)`), never unanchored `pending: N`
  substrings (R2 J9).
- Migration timestamp asserted, derived in-test from the seeded `started_at` via
  `DateTimeOffset.FromUnixTimeSeconds(...).UtcDateTime` + the `WatchCommands.cs:185` format (R2 J4).
- Doc-sample drift gate: required (R2 N6, precedent `ToolInventoryTests.cs:169-176`) — the how-to's
  healthy-bank sample must not drift a third time. Exit-code table derive gate (R2 N7) if a table is
  edited.
- Every gate: `--minimum-expected-tests <n>` mandatory (R2 N5); bare-pipe filters (`A\|B`) corrected
  to two `--filter-class` values or the `OR` form (R2 J1/E6/E7).
- `[RetryFact]` + `[Trait(Speed, Fast)]` per WP in P4's classes; logger/recorder built fresh inside
  the test, not on a class field (R2 J8 — retry surface by folder, state persists across attempts).

## Observability side (R1 M5/M6/S7/N2; M8; R2 J5/J6/J8; R3 §7)

- Factory commit FIRST: route all 38 `new EntryEmbedder(` sites (28 files) through
  `TestData.CreateEntryEmbedder(...)`; behaviour-free; gate = whole Fast lane. Then the constructor
  change once.
- `EmbedDrainReporter`: the ENTIRE moved Log block (1002-1007 **including 1007
  SelfReSignalNotQueued**), 1008-1013 new. Registry: count 168 → 174, row rewritten to 1002-1013,
  same commit as code. Reuse 1003/1005 from the migration path (legal: reflection counts
  declarations, not call sites).
- `IModelMigrationLease.LeaseTtl` (interface static — M6/J6).
- Keep the under-lease re-check at `EntryEmbedder.cs:109-114` (S7); pre-read only the stale-lease
  fields for 1009.
- 1012: observability only, NOT a no-throw "fix" — emit once per process per migration (M8).
  The permanent-lockout defect (`model reset` while a migration is open) is its own follow-up item
  with an ADR-0076 amendment; NOT part of this PR unless the guard is small and tested (orchestrator
  decision).
- 1013 stride: exactly one per `LeaseTtl` (60 s), zero within one stride; derive the boundary at
  exactly `IModelMigrationLease.LeaseTtl` (R2 J6). R3 §7: ~18 lines for a 17.5-min drain is right;
  the two documented metric sentences in `docs/how-to/read-performance-metrics.md` /
  `docs/reference/agent-memory-server.md` that call drain series per-corpus get reworded.

## Release (R3 §3; user ruling: ONE PR, ONE bump)

- Bump: **minor** → 1.36.0 (`python3 scripts/version-bump.py minor`; `VERSION` is the only
  hand-written marker; `VersionContractTests` proves the derivation).
- PR title carries BOTH facts (it feeds `--generate-notes`): doctor memory-engine report + migration
  verdict AND relay observability; exit 24 called out.
- `README.md` What's new: one compact entry (doctor reports memory engine + migration verdict;
  drain relay now logs progress) — braggable user-facing; link ADR-0076.
- Docs to move in the same PR (R3 §1): `docs/how-to/configure-ai-raccoon-server.md` (stale healthy
  sample block: 3 lines @ v10 → real output @ v11 + new lines; exit table; `:355` prose),
  `docs/reference/logging-event-ids.md` (registry), `docs/work/README.md` (index: research record +
  4 lane docs + 3 review docs = 8 rows), README What's new, and the P4 metric sentences.
- Manual live verification: checklist JSON per R3 block 3 (9 rows; row 3 = stuck-bank row,
  reproducible NOW non-destructively; rows 4-5 = exit-24 non-leak; row 1 = fresh install; row 7 =
  P4 stale-lease on a scratch bank). **Evidence bundle of the owner's stuck bank must be captured
  BEFORE the bank is drained** (R3 §3.3 sequencing tension): copy the bank file + settings +
  migration row state read-only, then the doctor manual row runs against the copy.

## Sequencing

1. D1 doctor lane: characterisation commit → extraction → lines → exit 24 → tests (Fast + Slow).
2. D2 observability lane: factory commit → reporter move → 1008/1003/1005 → 1010/1011/1012(cadence) →
   1009 → 1013 → registry.
3. Orchestrator: integrate both; resolve any seam conflicts; docs + bump; gates; PR.
4. Post-merge follow-up (tracked, not in this PR): `model reset` vs open migration (ADR-0076
   amendment), 1009/1013 if the lane deferred them, exit-table derive gate if not landed.
