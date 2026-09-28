# Review: config D chunk budgets plan (pre-implementation)

**Reviewed:** `docs/work/2026-09-28-config-d-chunk-budgets-plan.md` (575 lines, uncommitted in `docs/work/`).
**Lane:** review only; this file is the only artifact written.
**Shape:** `## Findings` — `F<n> [MUST|SHOULD|NIT] claim — evidence path:line — concrete fix`; `## Facts corrected` — plan citations that are wrong; `## Verdict` — last section. Every claim carries `path:line` or is labelled `[INFERRED]`/`[UNVERIFIED]`.

---

## Findings

### F1 [MUST] (resumed thread, settled) P1b's per-group re-chunk does NOT keep `chunk` columns or note ordering consistent as specified — its single column-maintenance step cannot even see plain notes, its fill can corrupt citing-note partitions, and skipped groups are stamped as migrated and never retried.

Evidence, three mechanisms plus the stamp trap:

1. **`RecomputeChunkColumnsBankWide` excludes plain notes entirely.** The statement's `grp` CTE is
   `FROM entries WHERE source_file IS NOT NULL` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:823`),
   and it only ever fills the `-1` sentinel (`MemorySql.cs:826-828`). A plain `memory_write` note has
   `source_file = NULL` (`src/AiRaccoon.Infrastructure/Sqlite/WriteChunks.cs:37` sets it from
   `source.SourceLocator`, null for a plain note) and the write path skips column maintenance for it
   (`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:150-153`, `if (request.SourceFile is not null)`),
   so plain notes sit at `chunk_index = -1, total_chunks = 0` by design (`WriteChunks.cs:52-53`) and
   their order is recovered from **id order** by `NoteTextOrder.Find`
   (`src/AiRaccoon.Core/Memory/NoteTextOrder.cs:96-121`: id order and position order, each rotated
   by at most `MaxShifts = 4`). P1b's "insert new rows `pending`, then `RecomputeChunkColumnsBankWide`"
   (plan §2 P1b) therefore leaves every plain-note replacement group's columns exactly as inserted,
   and the plan never says what the insert writes. Copied naively from `WriteChunks` the rows stay
   `-1`/`0` (defensible), but the ordering then depends on insertion order being text order — which
   the plan also never pins (see mechanism 3).
2. **For source-citing notes the planned step is the wrong recompute.** A citing note's rows share the
   `(ctx, source_file)` partition with the file's mirror rows and every other note citing that file
   (`MemorySql.cs:798-801` partitions on `ContextKeyExpression(""), source_file`). The planned
   `RecomputeChunkColumnsBankWide` fills `-1` sentinels with raw `ROW_NUMBER() ... ORDER BY id` over
   the **whole mixed partition** (`MemorySql.cs:814-815`, `:826-828`) while file rows keep their
   document positions (`CASE WHEN entries.chunk_index < 0 ... ELSE entries.chunk_index`). When ids
   interleave (a file re-ingested after the note write gives mirror rows higher ids than the note's),
   the filled values collide with kept file positions — the exact numbering defect ADR-0123 rules
   untrustworthy ("a gap or a duplicate is not a numbering to trust",
   `docs/adr/0123-chunk-index-repair-keeps-a-valid-stored-order.md`) and the exact misorder ADR-0122
   repaired (`docs/adr/0122-note-chunk-positions-follow-text-order.md`). The order-preserving forms
   already exist and are what the write/sync path's steady state uses:
   `RecomputeChunkColumnsBankWideKeepingOrder` (`MemorySql.cs:834-881`, "Notes citing the file follow
   its rows in stored order", "Only rows whose values change are written") and
   `NoteChunkOrderRepair.RunAsync(connection, paths)` (`src/AiRaccoon.Infrastructure/Ingestion/NoteChunkOrderRepair.cs:47-58`),
   which repositions a note's rows in `NoteTextOrder`-proven text order. The plan cites neither and
   its §4 shared-file list never mentions them.
3. **P1a's greedy overlay strip turns provable notes into "unverifiable" ones, and P1b then skips them forever.**
   P1a strips "the longest string that is both a suffix of the previous chunk and a prefix of the
   current one" (plan §2 P1a). The write path's overlay is whole units of the body repeated verbatim
   (`src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:58-59`, `:82-83`, `:169-193`), and where body text
   repeats at a boundary the longest suffix-prefix match is longer than the real overlay — it eats new
   content. The hash check then fails and P1b "leaves the group untouched and counted" (plan P1 AC1).
   `NoteTextOrder` solves exactly this ambiguity by enumerating **every** whole-line overlay candidate
   and backtracking until the join hashes to the path stem (`NoteTextOrder.cs:128-166`, `:169-193`) —
   the greedy "longest match" of P1a is one candidate among several, not the algorithm. The plan thus
   ships a weaker reassembly than the codebase already has and test #4's "repeated-boundary ambiguity
   fails verification instead of corrupting" enshrines giving up where `NoteTextOrder` succeeds
   (`tests/AiRaccoon.Tests/Unit/Memory/NoteTextOrderTests.cs` covers exactly these notes).
4. **The stamp trap: a skipped group is never migrated again.** P1c stamps `embedding.chunkBudget`
   with the resolved budget "when the phase completes" (plan §2 P1c) and the drift trigger keys off
   that row (plan P1 AC6). A group the phase skips (verification failure — mechanism 3) or drops
   silently (mechanism 1/2 column corruption) is still counted as done: the phase completes, the stamp
   reads 1022, the fingerprint matches, and no future pass retries the group. The bank then carries
   254-budget chunks forever while `docs/reference/breaking-changes.md`'s promised "every bank
   re-chunks and re-embeds once" (plan §5 doc line 3) is false for those rows. Owner decision 2
   ("existing banks migrate: re-chunk AND re-embed, both") is violated silently — a MUST against the
   settled decisions.

Concrete fix (P1a/P1b/P1c):

- **P1a:** implement `ChunkReassembly` as a thin wrapper over `NoteTextOrder`: `Find` proves the row
  order and the join (it already backtracks over whole-line overlay candidates and verifies
  `sha256(joined) == path stem`, `NoteTextOrder.cs:44-56`, `:186-191`); factor `Joins` to also return
  the verified merged text instead of writing a new greedy matcher. Keep the sha256-stem check as the
  acceptance, but a group that fails it must first be handed to `NoteTextOrder`-style backtracking
  before it counts as unprovable.
- **P1b:** the reconciler writes the replacement rows' columns itself and pins **insert order = text
  order** (ADR-0122's write convention, `docs/adr/0122-...`: "memory_write inserts a note's chunks in
  text order", which is what keeps `NoteTextOrder`'s id-order candidate working). For plain notes
  (`source_file IS NULL`) copy `WriteChunks`'s `-1`/`0` sentinel — nothing recomputes them
  (`MemorySql.cs:823`) — or write explicit text-order positions; state which. For citing notes run
  `NoteChunkOrderRepair.RunAsync(connection, [path])` after the per-group replace (it runs the
  keeping-order renumber for `-1` rows and repositions the note in proven text order,
  `NoteChunkOrderRepair.cs:33-42`, `:47-58`), not `RecomputeChunkColumnsBankWide`. If a bank-wide
  recompute is kept at all, use `RecomputeChunkColumnsBankWideKeepingOrder` — and never inside every
  per-group transaction (see F8).
- **P1c:** do not stamp `embedding.chunkBudget` while any group is counted skipped/unprovable; either
  the migration stays open (tool calls refused until resolved — harsh but honest) or the stamp records
  the shortfall so the drift trigger retries on the next poll. The phase report count must gate the
  stamp.

### F2 [MUST] (the owner's explicit ask) The MLX precondition gate binds nowhere and its stated RED mutation is dead — P2 AC2 / test #10 as specified cannot fail from the regression it claims to pin, and P5 AC2 re-pins the precondition on a seam that hard-codes the answer.

Three compounding defects:

1. **The test skips itself into vacuity on every host the suite runs on.** P2 AC2 places its case in
   `BundledEngineMlxSessionTests.cs`, which `Assert.Skip`s unless macOS + MLX plugin + rewritten graph
   are present (`tests/AiRaccoon.Tests/Integration/Embedding/BundledEngineMlxSessionTests.cs:29-34`),
   and the class doc states the files "are [absent] in every environment this test suite runs in
   unless scripts/download-mlx-runtime.py was run" (`BundledEngineMlxSessionTests.cs:12-15`). The
   plan admits the gate only reddens "on an MLX host" (P2 RED proof) but names no host or run where
   that happens. Owner decision 5 requires the precondition be "verified and pinned by tests" — a
   pin whose only exercise is a manual run nobody names is not verification; P2's gate
   (`dotnet test --filter ...`) passes green on any machine whether or not real MLX padding exists.
2. **The specified assertion is insensitive to the specified mutation.** Test #10 asserts "a
   1022-token row reports `LastSequenceLength == 1024`" and claims it goes red when
   `_bucketRows = true` is removed from `OnnxEmbeddingGenerator.cs:611`. A full-budget chunk is 1022
   content tokens + [CLS]/[SEP] = 1024 ids (`OnnxEmbeddingGenerator.cs:20-27`), and `RunBatch` starts
   from `maxLen = items.Max(i => i.Ids.Length)` and pads only upward
   (`OnnxEmbeddingGenerator.cs:679-684`): `PaddedLength(1024) == 1024` with or without padding. The
   mutation leaves `LastSequenceLength == 1024` and the CPU-cosine assertion intact — the test stays
   green everywhere, MLX host included. Config D's zero-waste alignment (1022 + 2 = 1024) is exactly
   what defeats this pin.
3. **P5 AC2 re-pins the precondition on "the MLX seam", which cannot regress.**
   `AttachMlxExecutorForTesting` sets `_bucketRows = true` unconditionally
   (`OnnxEmbeddingGenerator.cs:244-248`) — the seam path is always padded by construction, so "a
   full-size row pads to 1024" over the seam detects nothing (and a full-size row is 1024 ids anyway,
   defect 2). P5 AC2 as worded is a tautology.

Concrete fix (tests only, per owner decision 5):

- Test #10 must assert padding with a **short** row on the real MLX session — e.g. a 3-token row
  reports `LastSequenceLength == 64` while the CPU session reports its own length — which the
  `_bucketRows` removal at `:611` genuinely reddens. (The existing
  `BundledEngineMlxSessionTests.PreferMlx_FilesPresent_RunsOnMlx_WithTheCpuSessionsVectors` already
  carries exactly this live shape via `(mlx.LastSequenceLength % 64).ShouldBe(0)`
  (`BundledEngineMlxSessionTests.cs:47`) — see Facts corrected 2; the new case should follow it.)
- Make the skip fail-closed somewhere named: an env flag (e.g. `AIRACCON_REQUIRE_MLX=1`) that turns
  `Assert.Skip` into `Assert.Fail` when the plugin is absent, set on the designated MLX host run
  (nightly or the pre-release manual checklist); P2's AC must name that run. Otherwise the gate's
  green means "not exercised", and the ADR's "precondition verified by tests" claim is false.
- P5 AC2 should assert the short-row padding property on whatever executor the end-to-end run uses,
  or be deleted as vacuous.

### F3 [MUST] The §1.4 inventory is not exhaustive despite the plan claiming it covers "everything that pins 254 / 510 / 256" — it misses a test that goes red at the P3 flip and two user-facing statements that go false, and P4 AC2's grep gate cannot catch any of them.

Own diff of `git grep -n -E '(^|[^0-9A-Za-z])(254|510|256|1022)([^0-9A-Za-z]|$)' -- src tests scripts` (216 raw hits, sha256/crypto/event-id noise filtered) and the same over current-state `docs`:

1. **`tests/AiRaccoon.Tests/Integration/Storage/SqliteMemoryStoreChunkingTests.cs:65-68` — breaks at the flip, absent from the inventory and from every gate.** The test ingests a ~60-section note and asserts
   `entries.ShouldAllBe(entry => tokenizer.CountTokens(entry.Value) <= 256)` with a literal 256
   ceiling (o200k counted) over chunks budgeted by `ResolveChunkBudgetFor` (`FileIngestor.ChunkSizeForAsync`,
   `FileIngestor.cs:378-392`). After `chunkTokens: 1022` the chunks tokenize to ~1022 WordPiece
   tokens — the assertion fails. The plan caught its twin (`WriteChunksToBudgetTests.cs:29-30`,
   inventory row 14) and missed this one. None of P3's or P5's `--filter` gates run it
   (`SqliteMemoryStoreChunkingTests` matches no listed filter), so it first fails in the CI full suite
   on the release PR. Fix: add the row (disposition **moves**: derive the ceiling from the resolved
   budget + reservation as row 14 does) and add `~SqliteMemoryStoreChunkingTests` to P3's AC5 gate.
2. **`src/AiRaccoon/Tools/MemoryTools.cs:139-145` — the MCP `search` tool's query description, agent-facing:** "Semantic matching only sees roughly the first N tokens — the active memory embedding engine's
   own window (254 tokens for the bundled model; ...)". After the flip the bundled window is 1022;
   every agent reading the tool schema is told the wrong trim point. **Moves** (de-number or say
   1022). Not in the inventory; P4's gate (`grep -rn "254" docs/explanation/architecture.md
   docs/reference/breaking-changes.md`, P4 AC2) greps only two docs files and cannot see it.
3. **`docs/how-to/configure-embedding-engines.md:42` — user-facing engine table:** "8,190 tokens
   (chunked to 254 for memory, 510 for code)". **Moves** (1022 for memory). Not in the inventory.

Also missing (lower impact, mostly "stays" rows the plan's exhaustive claim should still name):
`src/AiRaccoon.Core/Memory/QueryGuard/QueryLengthGuard.cs:30` is row 7 but the same file's scaling
constant is derived (`:33-35`) — fine; `src/AiRaccoon/Tools/MemoryTools.cs:320-323` (doc comment
"the bundled model's fixed 254", **moves**/de-number); `src/AiRaccoon.Infrastructure/Embedding/IEmbeddingService.cs:25-26`
(interface contract doc "254 for bundled/legacy, min(510, ctx − 2) for manifest models" — **moves**,
bundled now resolves through `chunkTokens`); `src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:22`
and `tests/AiRaccoon.Tests/Unit/Chunking/CodeChunkerTests.cs:85` ("not the memory chunker's 254" —
the same wording plan row 28 moves in architecture.md:877 but these are missed);
`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:194-195` ("Static bundled-default budget
(254 local / window for others)" — the phrase "bundled-default" turns misleading);
`scripts/retrieval_tuning/llamaindex_harness/ingest.py:67` ("i.e. 256 tokens once the two special
tokens are added" — row 35 covers only line 66);
`tests/AiRaccoon.Tests/Integration/ChunkingCorpusGuaranteeTests.cs:43,51,104-105` (row 13 cites only
`:21,:44`; the 256 literals at :43/:51 and the hostile-fixture ceiling at :104-105 move with it);
`tests/AiRaccoon.Tests/Integration/Embedding/CodeManifestBudgetGuardTests.cs` (whole file of 510
pins — "stays", but the exhaustive table omits it while P3 AC2 gates on it);
`src/AiRaccoon/Setup/AppRegistrations.cs:307`, `src/AiRaccoon.Core/Memory/Code/CodeSearchWarnings.cs:20`,
`docs/adr/0120-chunk-boundaries-fall-on-whitespace.md:69`, `docs/features/code-corpus/code-corpus.feature:15-18`
("stays" rows). Noise correctly excluded by the plan: vocab/tokenizer data, `P-256`, `AES-256`,
`EventId = 510`, `MaximumLength(256)`, fixture windows in
`EmbeddingManifestLoaderTests`/`VecDimensionReconcileWorkTests`/`MiniLmGoldenVectorTests`.

Fix: extend §1.4 with at least items 1-3 as "moves" rows, widen P4 AC2's gate to
`git grep -n 254 src/ tests/ docs/ scripts/` minus a named noise allowlist (or a reviewer walk that
explicitly covers `src/AiRaccoon/Tools/MemoryTools.cs` and `docs/how-to/`), and schedule item 1's
test edit in P3.

### F4 [SHOULD] P1c's drift trigger cannot open a migration through the mechanism the plan names — `StartMigrationAsync` short-circuits to a settings-only write when the fingerprint matches, which is exactly the drift case — and P1's "behavior today is unchanged" claim is false for the row-absent case.

- P1c says `ReconcileFingerprintAsync` ("the mechanism" of F2) "also opens a migration when a new
  `embedding.chunkBudget` settings row is absent ... or differs". But `ReconcileFingerprintAsync`'s
  only open path is `StartMigrationAsync` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:56-65`),
  and `StartMigrationAsync` returns after a settings-only upsert — no `StartModelMigration`, no
  `MarkAllEmbeddedPending`, no migration row — whenever `previous is null || previous == engine`
  (`EntryEmbedder.cs:74-85`). Budget drift is by definition an **equal-fingerprint** case (the
  manifest is unchanged; only the stamp row is stale), so the named mechanism silently no-ops and
  P1 AC6 cannot be satisfied by it. Fix: specify a force-open path (e.g.
  `StartMigrationAsync(..., force: true)` or a dedicated `StartBudgetDriftMigration` that runs the
  same outbox transaction `EntryEmbedder.cs:88-121`) and say so in P1c.
- Sub-point: on the P1-standalone build (before P3 merges) every existing bank has no
  `embedding.chunkBudget` row, so the "absent" trigger opens a migration — full
  `MarkAllEmbeddedPending` re-embed plus the tool-gate outage (`IToolGate.RequireBankAvailableAsync`,
  `src/AiRaccoon/Tools/IToolGate.cs:5-6`) — for a no-op re-chunk at an unchanged budget. "Behavior
  today is unchanged (the phase finds nothing to do while the budget matches)" (P1 preamble) is
  therefore wrong about the trigger even when the phase no-ops. No release ships between the merges
  so users never see it, but the drift design should either stamp-and-close without re-embedding when
  the phase changes nothing, or the claim should be corrected.
- The slot itself checks out (brief's question — **true for every migration-closing path**):
  `FinishModelMigration` has exactly one caller (`EntryEmbedder.cs:210`, verified by `git grep`),
  `DrainMigrationAsync` cannot reach it before the embed loop drains to zero (`EntryEmbedder.cs:190-207`),
  and `ModelResetAsync` is refused while a migration row is open
  (`src/AiRaccoon/Settings/SettingsEndpoint.cs:70-75`) — so a phase placed at
  `ReconcileVecDimensionsAsync`'s slot runs before every close. (Line drift: the call is at
  `EntryEmbedder.cs:185`, not 183-184; `SelectAllPendingForEmbed` is first read at `:194` as cited.)

### F5 [SHOULD] The accepted-cost list understates what a replacement insert must carry — the plan names only rating/access_count/last_accessed_at loss but the obvious template (`ChunkBackfill`) also drops `agent_id` and resets `created_at`, and P1b never specifies the insert's carried columns.

`ChunkBackfill`'s re-insert writes `agentId = null, createdAt = now`
(`src/AiRaccoon.Infrastructure/Ingestion/ChunkBackfill.cs:99-103`) while a note's original rows carry
`section`, `sourceId`, `agentId`, `createdAt` (`src/AiRaccoon.Infrastructure/Sqlite/WriteChunks.cs:36-53`).
P1b's "insert new rows `pending`" names no column list; an implementer copying `ChunkBackfill` (the
plan's own comparison point) silently loses `agent_id`, `source_id` (breaks the citation link),
`section`, and the write timestamp — more than the ADR draft's "per-row ratings and access counts
reset" (§5 doc line 3). Fix: P1b's insert spec enumerates the columns carried forward from the old
group (`path`, `source_file`, `section`, `scope/context/workspace`, `source_id`, `agent_id`,
`created_at` — or explicitly accept resetting `created_at`), and the ADR's accepted-cost list matches.

### F6 [SHOULD] Mirror groups whose source file no longer exist are silently left un-migrated while the stamp and the user-facing docs claim "every bank re-chunks" — name the population and let it gate the stamp.

P1 AC2 blesses "files that no longer exist are untouched" and `ReingestRepair` indeed skips an
unusable file (`src/AiRaccoon.Infrastructure/Ingestion/ReingestRepair.cs:93-96`). Those mirror rows
keep 254-budget chunks forever: their `path = source_file` (no sha256 content commitment like a
note's `WritePathFor`, `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:837`), so
overlay-stripped reassembly of their rows cannot be hash-verified and skipping is the honest option.
But `docs/reference/breaking-changes.md`'s planned entry promises "every bank re-chunks and re-embeds
once" (§5 doc line 3) and F1's stamp trap counts these groups as done. Fix: name the population
(rows whose `source_file` no longer resolves — deleted/renamed docs) in ADR-0125's Consequences and
the breaking-changes entry as not re-chunked (still re-embedded at their stored bounds), and tie it
into F1's stamp gate: the phase report's skip count includes them and the ADR cites the count's
meaning.

### F7 [SHOULD] §4's shared-file list is incomplete and one of its cross-references dangles — P1c's `EntryEmbedder` constructor change ripples into files no lane owns.

P1c adds a collaborator to `EntryEmbedder`'s primary-constructor list
(`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:18-27`). Compilation ripple beyond the
files P1 lists: `src/AiRaccoon/Setup/AppRegistrations.cs:406`
(`AddRequiredSingleton<IEntryEmbedder, EntryEmbedder>()` — the new collaborator needs its own
registration here), `tests/AiRaccoon.Tests/TestData.cs` (`CreateEntryEmbedder` factory used by
current tests), `tests/AiRaccoon.Tests/Integration/Embedding/EntryEmbedderMigrationDrainReportingTests.cs:137`
and `tests/AiRaccoon.Tests/Integration/Embedding/QueryTruncationTests.cs:69` (both construct
`EntryEmbedder` positionally — and `QueryTruncationTests.cs` is owned by lane C per §3, so lane A
must either touch it first or lane C must absorb the ctor churn: §4 does not say). Also, P5's "Files
owned: ... the test-list entries marked cross-package in §3 (items 14-15)" is a dangling reference —
§3 marks no row "cross-package"; rows #14/#15 carry pkg = P3. Fix: extend §4's shared-file table
with `AppRegistrations.cs`, `TestData.cs`, `EntryEmbedderMigrationDrainReportingTests.cs`, and the
lane-A/lane-C split for `QueryTruncationTests.cs`; make P5 name its rows explicitly.

### F8 [SHOULD] "`RecomputeChunkColumnsBankWide` per group" is a full-bank window recompute inside every per-group transaction — O(groups × bank) time and full-bank write amplification, on top of F1's correctness problem.

`RecomputeChunkColumnsBankWide`'s `grp` spans every `source_file IS NOT NULL` row in the bank
(`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:811-823`) and its UPDATE rewrites `total_chunks`
for **all** of them on every run (`MemorySql.cs:824-830` — unlike
`RecomputeChunkColumnsBankWideKeepingOrder`, which writes "only rows whose values change",
`MemorySql.cs:834-881`). At 254→1022 nearly every multi-chunk group re-chunks; a 23k-row bank with
thousands of groups pays thousands of full-bank recomputes, each inside the group's transaction —
the re-chunk phase's own runtime then dominates the very outage window F4's estimate calls
"minutes-scale" (the [INFERRED] cost paragraph counts embed tokens only, not the phase's scan,
file reads, `NoteTextOrder` verification search, and these recomputes). Fix (composes with F1):
prefer the group-scoped `RecomputeChunkColumnsForContext`/`NoteChunkOrderRepair.RunAsync(connection,
paths)` per replaced group and at most one `RecomputeChunkColumnsBankWideKeepingOrder` at the end of
the phase; add the phase's wall-clock to the post-merge measurement items in §5.

### F9 [SHOULD] Gate-failability audit — named vacuous gates, per the structure attack.

Assessed every AC gate for "can it actually FAIL on the criterion":

| AC | gate | verdict |
|---|---|---|
| P1 AC1-AC5 | named test filters (ChunkReassembly/ChunkRebudget/ModelMigration...) | failable ✓ (test rows 4-7 exist; AC4's idempotence is inside row 5 ✓) |
| P1 AC2 (mirror groups re-chunked from disk; missing files untouched) | `~ChunkRebudget` | **vacuous — no test row in §3 covers mirror/file groups** (row 5 is note-only). A filter that matches only note tests cannot red on AC2's regression. Add a mirror-group test row (file rows at 254 → 1022 pieces from disk; deleted file untouched). |
| P1 AC6 | `ChunkBudgetDriftOpensAMigrationTests` (row 7) | failable ✓ — but its implementation is blocked by F4's mechanism trap |
| P2 AC1 | `BundledManifestBucketAlignment|CoreMlWindowBudgetTests|LengthBucketsTests` | failable ✓ (rows 8, 9, 11; RED mutations real) |
| P2 AC2 (the owner's precondition) | same filter | **vacuous — F2**: skips on every suite host, and the assertion is insensitive to its own mutation |
| P2 AC3 | `BundledEngineBucketPaddingTests` (existing, kept) | failable ✓ (unpadded `% 64 != 0` assertion reddens if padding leaks onto CPU) |
| P3 AC1-AC5 | named filters (rows 1-3, 12-17) | failable ✓ (RED mutations real) — but AC5's gate misses the flipped test in F3 item 1 |
| P4 AC1 (ADR exists, Nygard shape, content checklist) | "reviewer checklist" + `LoggerMessageEventId` filter | named gate tests event ids, not the criterion; reviewer-judged is acceptable for prose but the filter is decorative — say "reviewer sign-off" as the gate and drop the filter, or add a structural check |
| P4 AC2 (every moves row edited) | two-file grep + reviewer walk | **too narrow — F3**; passes while MemoryTools.cs:141 and configure-embedding-engines.md:42 stay wrong |
| P4 AC3 | file existence + review | failable ✓ (weak but conventional) |
| P5 AC1 | `ConfigDEndToEndMigrationTests` | failable ✓ (row 18; genuinely cross-package ✓) |
| P5 AC2 | same filter, "on the MLX seam" | **vacuous — F2 defect 3**: the seam hard-codes `_bucketRows = true` (`OnnxEmbeddingGenerator.cs:247`) and a full-size row pads to 1024 tautologically |
| P5 AC3 | combined P1+P2+P3 filter, once | failable ✓ (and test-economy-compliant ✓) |
| P5 AC4 | CI runs full suite | procedural, unverifiable locally — conventional |

Fix: add the P1 AC2 test row, fix F2/F3, and either make P4 AC1's gate honest or drop the decorative
filter.

## Facts corrected

1. **Plan F10 gap 1 ("Nothing asserts that a **real MLX session** pads ... a regression that stops
   setting `_bucketRows` ... leaves every existing test green") is wrong on its face.**
   `BundledEngineMlxSessionTests.PreferMlx_FilesPresent_RunsOnMlx_WithTheCpuSessionsVectors` runs a
   real MLX session on a short row and asserts `(mlx.LastSequenceLength % 64).ShouldBe(0)`
   (`tests/AiRaccoon.Tests/Integration/Embedding/BundledEngineMlxSessionTests.cs:47`) — removing
   `_bucketRows = true` at `OnnxEmbeddingGenerator.cs:611` reddens it (a ~13-token row runs at its
   own length unpadded). The true gap is narrower and it is the one that matters: that test
   `Assert.Skip`s on every host this suite runs on (`BundledEngineMlxSessionTests.cs:29-34` + class
   doc `:12-15`), so the assertion exists but binds nowhere. The plan's proposed replacement
   (test #10's `LastSequenceLength == 1024` at full budget) is strictly weaker than the test it
   claims to supplement — see F2.
2. **Plan F5 ("The code engine's fingerprint is the same bundled manifest hash",
   `CodeEmbedder.cs:175`) holds only for the bundled-default code engine.** `CodeEmbedder`
   fingerprints `EngineFingerprint("local", codeModel, null)` where `codeModel` is the
   `embedding.codeModel` setting (`src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:167-175`):
   for `BundledModel.SettingValue` this is `local:bundled#<bundled manifest hash>` — the same string
   as the memory engine, pinned as "memory's unset model and code's 'bundled' setting are the same
   engine" (`tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:217-226`) —
   so the F5 churn is real there. A bank whose code engine is a separately-activated model directory
   fingerprints that directory's own manifest (`EmbeddingService.cs:310-320`); flipping the bundled
   manifest's `chunkTokens` does not touch it and no code re-embed churn occurs. The ADR's churn cost
   statement should say "banks running the bundled-default code engine".
3. **Plan's P1-slot citation drift (matters only for implementers grepping by line):**
   `ReconcileVecDimensionsAsync` is called at `EntryEmbedder.cs:185` (the plan says 183-184); the
   embed loop spans `:190-207` and `FinishModelMigration` is at `:210` (plan cites :210 ✓). The
   substantive claim — the slot precedes every migration close — is TRUE (F4's third bullet).
4. **Plan F1/F2, F3, F6, F7, F8, F11's substance verified true** against source: manifest-bytes
   fingerprint (`EmbeddingService.cs:305-315`, manifest `chunkTokens: 254` at
   `src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json:75`);
   fingerprint mismatch → `StartMigrationAsync`'s outbox transaction marks all embedded rows pending
   (`EntryEmbedder.cs:88-121`, `MemorySql.cs:456-464`) and the drain closes only at zero pending
   (`EntryEmbedder.cs:190-210`); tool-gate refusal (`IToolGate.cs:5-6`, `IModelMigrationStore.cs:21-24`);
   `ChunkBackfill`'s delete-before-insert gap (`ChunkBackfill.cs:84-107`, plan's characterization
   correct); note chunks duplicate a whole-unit overlay (`MarkdownChunker.cs:52-65`, `:165-195`);
   `WritePathFor` commits `sha256(utf8(content)).md` (`SqliteMemoryStore.cs:837`) so reassembly is
   hash-provable; the query-trim coupling (`EmbeddingService.cs:152-154`, `:468-481`) and the guard
   arithmetic exactly (1022 × 1,000/254 → 4,024 chars, `QueryLengthGuard.cs:25-52`); 1022 + 2 = 1024
   fills a 64-token bucket with zero waste (`LengthBuckets.cs:10`, `:24-31`, and
   `LengthBucketsTests.cs:34-42` already pins 1022); `chunkTokens: 1022` passes
   `EmbeddingManifestValidator.cs:49-52` (1,022 ≤ 8,190 − 2 = 8,188 — the plan's "8188" is exact);
   `_bucketRows` set only at MLX session creation + test seam (`OnnxEmbeddingGenerator.cs:610-611`,
   `:244-248`), CoreML 256-step/1024-window buckets with CPU fallback over 1024
   (`LengthBuckets.cs:13-16`, `CoreMlGraph.cs:20-24`, `NeuralEngineEmbeddingGenerator.cs:140-146`,
   `:358-361`), CPU unpadded (ADR-0114 + `BundledEngineBucketPaddingTests.cs:39-46`); the cost anchors
   (25,917 rows / 357 s at `docs/adr/0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md:294-298`;
   eval corpus 1,608 → 383 memory chunks at 2026-09-24 record `:36`, `:40`; 46-57 ms per 1k tokens
   and 3.7 GB padded peak at 1022 in the MLX record `:64`, `:76`). The plan's three self-corrections
   (anchor verification 1-3) are themselves accurate.

## Verdict

**APPROVE-WITH-FIXES.** The plan's spine — fingerprint-triggered migration with a `ChunkBudgetReconciler`
phase slotted before the embed loop inside `DrainMigrationAsync`, per-group atomic replacement,
hash-proven note reassembly, drift stamp, tests-only precondition work, package and merge order —
stands on verified source, and the great majority of its ~40 citations and all of its arithmetic
check out exactly. But two MUST findings block approval as written: P1b's column/ordering handling is
inconsistent with the write path's steady state and ADR-0122/0123's invariants (its one recompute
step cannot even see plain notes, its fill can duplicate citing-note positions, and the stamp then
makes every skipped group permanently un-migrated — a silent deviation from owner decision 2), and
the owner's explicit precondition gate is doubly vacuous (skips on every suite host and its stated
assertion cannot detect its own mutation) while a third MUST shows the "exhaustive" inventory misses
a test that goes red at the flip and two user-facing statements that go false. All are fixable
without changing the plan's shape: route reassembly and column repair through `NoteTextOrder` /
`NoteChunkOrderRepair` with insert-order = text order and a stamp that gates on zero skips (F1),
pin MLX padding with a short row and a named fail-closed run (F2), and extend the inventory + P3/P4
gates (F3). Fix those before implementation merges; SHOULD items F4-F9 land with their packages.

