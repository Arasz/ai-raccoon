# Plan — post-delta session 5 (rev 0.1 — carry-over draft)

**Date:** 2026-08-23 · **Base:** main `05625928` (`VERSION` = 1.33.0) ·
**Status:** **rev 0.1 — waits for session 4 to close.** Written during session 4's wave 1 as the
hard precondition G8 attached to WP6 (*"3 now, 2 later — create plan for the next session before
starting"*). Nothing here opens until session 4 hands over. ·
**Task:** `post-delta-5` · **Lane:** architect (plan), Opus.

**What this document is.** The carry-over ledger. Every item names the state session 4 leaves it in
and the exact point session 5 resumes from. Two rows are *conditional* — they only exist if session 4
does not reach its own waves 3 and 4 — and one row is a deliberate blank, filled at session 4's close.

**Sources.** `#455`'s branch state, the drift count, the `#519` call-site count and the
`ParityGateTests` traits were re-derived against `05625928` today, not quoted from session 4's plan.
Re-derive them again when this session opens; the numbers below are evidence of *shape*, not of state.

## Session todo — execution order (rev 0.1)

1. Re-derive this plan against main at session-5 open: fill the placeholder row in §Carry-over from
   session 4's close (`.ai-badger/state.json`, `status-notes.json`), re-check every count, bump to rev 1.0.
2. **WP1** — ADR-0089 part 4: CLI `project id generate` / `project id convert`. **Blocked** until
   session 4's `6a` + `6b` are merged.
3. **WP2** — ADR-0089 part 5: storage guidance + the `ai-raccoon.ignore` entry. Parallel to WP1.
4. **WP3** — WP7's measured arms, **only if** session 4 did not reach its wave 3. Quiet machine, alone.
5. **WP4** — WP9 (#519, #493, the `logging-event-ids.md:53` fix), **only if** session 4 did not reach
   its wave 4. Late, after every other PR that adds a test has merged.
6. **WP5** — **#455, strictly LAST**: merge `origin/main` into the branch, re-run the parity and
   retrieval gates in the foreground, regenerate the golden if they move, mark #499 ready.
7. Hand the owner **S6b (#414)** the moment #455 merges — the runbook and
   `scripts/verify-history-scrubbed.py` are already written. **This session executes none of it.**
8. Close: update `.ai-badger/state.json` and `status-notes.json`; write the session-6 plan if anything
   is left.

---

## Carry-over — what session 4 leaves, and where session 5 picks it up

| # | Item | State at session 4's close | Resume point |
|---|---|---|---|
| **WP1** | ADR-0089 **part 4** — CLI `project id generate\|convert` | Not started. Moved out of session 4 by **G8** (*"2 later"*). Its substrate — `6a` (the `projects` table + registration write path) and `6b` (`project_id_token_get`) — is session 4's wave 2 | Read ADR-0089 decisions 6 + 7, confirm `6a`/`6b` are on main (`git grep -n "project_id_token_get" -- src` must now hit), then open the branch. **If `6a`/`6b` did not merge, WP1 does not open** — it carries again |
| **WP2** | ADR-0089 **part 5** — storage guidance + the `ai-raccoon.ignore` entry | Not started. Moved out by **G8** with part 4. Docs-only; no dependency on `6a`/`6b` | Open immediately; it is the one item here with no precondition at all |
| **WP3** | WP7's **measured arms** (quantized / CoreML, three arms on the S3–S5 protocol) | **Conditional.** Session 4's desk half ran in wave 1; the measured arms were scheduled for its wave 3 on a quiet machine. If wave 3 ran, this row is closed at handover and drops | Read session 4's research record under `docs/work/`. Re-run only the arms it did not measure, on the S3–S5 protocol verbatim (`docs/work/2026-08-22-code-ingestion-profile.md` §3 + §9) |
| **WP4** | WP9 — #519, #493, the `logging-event-ids.md:53` fix | **Conditional.** Session 4's wave 4. If it ran, this row drops | `git grep -c "CreateMemoryStore(" -- tests ':!tests/AiRaccoon.Tests/TestData.cs'` — today **83 call sites across 73 files**. Non-zero optional Null-object defaults at `TestData.cs:86,89` mean it did not run |
| **WP5** | **#455** — the re-derived corpus, queries and parity golden | Untouched, by **G10** (*"move it to the next session"*). Branch `task/pd3-455-public-benchmark-corpus` @ **`ea174faf`**, draft PR **#499** OPEN, 5 commits ahead of merge-base `a747da1a` | `git fetch origin && git merge origin/main` on the branch, then the three gates in the foreground. **Stays LAST** — standing owner ruling |
| **owner** | **S6b** — the #414 history rewrite | Prepared, never run. Runbook `docs/work/2026-08-22-414-s6b-history-rewrite-runbook.md` (240 lines) + `scripts/verify-history-scrubbed.py` shipped in part 3 (#473) | Owner-only, and **behind #455**: runbook precondition 4 is *"#455 is merged, so paths 2 and 3 are off HEAD"* |
| **—** | The two **#524** owner calls | **Already taken — do not re-ask.** G4 APPROVE fixed one rendering of the resolved thread count across `settings model show`, the `settings model threads` confirmation and `doctor`; EventId 428's `Threads`-as-a-string fix rode along ungated (*fix-what-you-find*, the G5 card was withdrawn). Session 4's WP4 built both | Nothing. If WP4 did not merge, it carries as code — not as a question |
| **—** | *whatever session 4 leaves* | **Filled at session 4's close.** Any wave-1 WP that did not merge (WP1 drain re-signal, WP2 ignore root + #485, WP3 job metrics, WP4 #524, WP8 manifest repairs) and any `6x` sub-PR of wave 2 lands here with its branch, its last commit and the gate it was last seen red or green on | — |

**Standing rulings that survive into this session, so nothing re-proposes them.** **WP5 of session 4
is DROPPED, not deferred** — G7 REJECT, *"dont delete"*: `PendingEmbedJob` and `CodeReindexJob` stay,
and the three guarantees behind that (the fingerprint reconcile, the coalesced-signal recovery path,
the same-pass sweep ordering) are in session 4's §Review (f). **#455 is last**, always. **The
`projects` table takes no `CurrentVersion` bump** (ADR-0089 decision 5, ADR-0086's rule).

---

## Work packages

### WP1 — ADR-0089 part 4: `project id generate` and `project id convert`

**Blocked on session 4's `6a` + `6b`.** Verify both are on main before opening the branch.

- **Scope.** Two verbs under a **new top-level `project` family** — ADR-0089 decision 6. `generate`
  mints a guidv7, registers it, prints it, takes an optional `--name`. `convert <old-id>` rewrites an
  existing raw-text project to a guid in **one transaction, one way** (no guid-to-text verb — reversing
  restores the guessable name) and registers the new id with `name` = the old raw id, overridable
  with `--name`.
- **Files.** `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (655 lines; top level, beside `watch
  registered` / `extract prune` / `model set`, per the placement argument in decision 6),
  a new `src/AiRaccoon/Setup/Cli/Commands/ProjectCommands.cs`, and its line in
  `Commands/CommandsRegistration.cs`. **No `project` node exists today** — `git grep '"project"' --
  src/AiRaccoon/Setup/Cli/` is empty, so this is all new surface.
- **Plus the CLI-side `IProjectRegistry` wiring, carried here from session 4's 6a** (see
  `docs/work/2026-08-23-wp6-adr-0089-implementation-plan.md` §6a "Not in 6a"). 6a registers
  `IProjectRegistry` in the **server** graph only (`Setup/AppRegistrations.cs:304`); the CLI graph
  reaches the bank through `AppRunner.cs:227`'s `AddSingleton<T>(lazyServerStore)` lines, backed by
  `LazyServerSettingsStore`, which implements its eight interfaces by forwarding through
  `InnerAsync` (probe-and-start the server, ADR-0075 §5.1). Adding `IProjectRegistry` there is
  **three forwarding members plus an `AsProjectRegistry` cast helper — not one line**, and both
  verbs need it. **Budget it in this WP's first commit rather than discovering it mid-implementation;**
  nothing before 6d resolves `IProjectRegistry` from the CLI graph, which is why 6a deferred it.
- **The part nobody should discover late — decision 7.** `convert` must re-derive the stored vec0
  `ctx` values, not only `project_id`. `ctx` is written once by trigger at insert time —
  `MemorySql.ContextKeyExpression` (`MemorySql.cs:705-714`) for the memory corpus
  (`MemorySchema.cs:158,195`), `NEW.project_id` verbatim for the code corpus (`MemorySchema.cs:487`) —
  and those triggers fire on the embedding columns, **not** on `project_id`. A plain
  `UPDATE entries SET project_id = …` leaves every vector row partitioned under the old string and the
  converted project's corpus goes unreachable. Settings keys are in the same position:
  `access.mode.project:<id>` is built from the id (`src/AiRaccoon.Core/Access/AccessModePolicy.cs:13`).
  Watches, sync payloads and metrics rows also carry the id; **enumerating every table that does is
  this WP's first commit**, and the ADR says so explicitly (§Not addressed).
- **The ignore-file append belongs here, not to WP2** *(architect's call, logged rather than gated —
  ADR-0089 decision 8 already binds the behaviour; only its packaging was ambiguous)*. If a verb writes
  a token file into the tree, **that verb** appends the path to `ai-raccoon.ignore` when it is not
  already matched. WP2 is the docs and this repo's own entry.
- **RED first.** Three tests, seen failing before any production line: (1) `generate` prints a
  guidv7 **and** the id is registered afterwards — red today, no command; (2) `convert` on a bank with
  code and memory rows leaves **zero** vec0 rows partitioned under the old `ctx` and search returns the
  same hits under the new id — red today, and it is the test that catches decision 7 being skipped;
  (3) argv-level: `project id convert` with no argument fails with a usage error. **Test the argv, not
  just the handler** — System.CommandLine option names keep their `--` prefix at `GetValue`.
- **Acceptance.** `generate` mints + registers + prints, honouring `--name`; `convert` is one
  transaction, one way, and moves `project_id`, every vec0 `ctx`, the `access.mode.project:<id>`
  settings key and every other table the first commit enumerated; the old id resolves to nothing
  afterwards; a converted project's search results are unchanged; no `CurrentVersion` bump.
- **Gate command.**
  `dotnet test --filter "FullyQualifiedName~ProjectCommands|FullyQualifiedName~CliCommandTree|FullyQualifiedName~ProjectId" --nologo -v m`
  plus `dotnet build`. New test classes carry **class-level traits** or they fall outside every filter.
- **Lane.** architect / **Opus** sizes the `convert` transaction against the live schema first (it is
  the risk); dotnet-engineer / **Sonnet** implements; code-reviewer / **Opus** reviews — never the
  implementer.

### WP2 — ADR-0089 part 5: storage guidance and the `ai-raccoon.ignore` entry

Docs and one config line. **No precondition** — it can open on day one.

- **Scope / files.** `docs/reference/agent-memory-server.md` gains the storage guidance: where the
  project id lives, that it is a registered guidv7 and not a secret, and that the token file is kept
  out of the **memory bank** — not out of git. `ai-raccoon.ignore` at the repo root gains the token
  path in the existing commented-section style.
- **Why the ignore file and not `.gitignore`** — the owner reversed this in the #448 review
  (*"Not gitignored - ai-raccoon.ignore - we dont want this file in memory"*), and ADR-0089 decision 8
  carries the mechanism: a directory walk already skips a hidden ancestor segment
  (`FileIngestor.cs:140,410`), but an **explicit `memory_ingest_file`** of the same path is not
  covered — the single-file guard tests only the leaf filename for a leading dot
  (`FileIngestor.cs:47,398-401`), so `.ai-raccoon/project-id` passes it. Only the ignore entry closes
  that path. **A `.gitignore` line would have closed neither.**
- **Two constraints inherited from ADR-0086.** One ignore file per watch/ingest root, **no nested
  discovery** (`IgnoreRulesProvider.cs:5-8`) — so the entry must sit at the root that actually covers
  the token file; and editing the file triggers a **full re-scan** of the covering watch, single-flighted.
- **RED first.** A test that an explicit `memory_ingest_file` of a path matched by
  `ai-raccoon.ignore` ingests **zero** rows in either corpus — red only if the guard is missing;
  if it already passes, say so and keep the WP docs-only rather than inventing a failure.
- **Acceptance.** The reference doc states where the id lives, that it is not access control, and the
  ignore-not-gitignore reason in one sentence; `ai-raccoon.ignore` carries the entry with its comment;
  no `.gitignore` line is added anywhere.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~FileIngestorIgnore" --nologo -v m`
  plus a read-through of the changed doc section against the ADR.
- **Lane.** dotnet-engineer / **Sonnet**. No file collision with WP1.

### WP3 — WP7's measured arms *(only if session 4 did not reach its wave 3)*

- **Scope.** No production file. Three arms — fp32 CPU today, `code-daemon-embed-v1` int8, the CoreML
  EP — plus a vector-drift check of the same 1,762 chunks against fp32. Output: the research record and
  an ADR-0049 amendment draft if an arm wins. **No engine swap** until the owner rules.
- **Protocol, verbatim or the numbers are not comparable.** `docs/work/2026-08-22-code-ingestion-profile.md`
  §3 and §9: set the thread cap, kill and restart `serve` (sessions cache per engine fingerprint, so the
  restart is mandatory), re-activate the code engine, count `embed_state='pending'` at the start and end
  of a fixed **150 s** window. Scratch bank on **port 7931**, `--idle-timeout 0`, the 469-file corpus.
  Baselines: **S4 = 2.347 rows/s** (cap 5), **S5 = 1.902** (cap 0), **S2 = 1,061.3 s** end to end.
- **Acceptance.** Every figure carries its command and a measured/read/inferred tag; every rows/s comes
  from a 150 s window after a restart and re-activate; the record ends in a recommendation, not an edit.
- **Gate.** No test filter — the gate is the record's own evidence rule. **Alone on a quiet machine:**
  running this beside live lanes is what made #511's numbers unusable (same-binary variance 2.8×).
- **Lane.** architect / **Opus**.

### WP4 — WP9: #519, #493, and the event-id doc fix *(only if session 4 did not reach its wave 4)*

- **Scope.** #519 — drop the two optional Null-object defaults at `tests/AiRaccoon.Tests/TestData.cs:86,89`
  and make every caller pass its double explicitly: **83 call sites across 73 files**, measured today,
  mechanical. #493 — `PerformanceTools`' description stops hand-listing phase names (the
  *derive-or-delete-the-list* answer) or a test derives them from `SearchResults.PhaseNames`.
  Plus the one-line fix at `docs/reference/logging-event-ids.md:53`, which claims EventIds **517-519**
  on `BankMaintenanceHostedService`; its `Log` class declares 510-516 and 520-524 only.
- **Acceptance.** No optional Null-object default remains on a `TestData` factory and the suite builds
  with every caller explicit; `PerformanceTools` no longer hand-lists phase names; `logging-event-ids.md:53`
  names only the EventIds that class declares.
- **Gate command.** `dotnet build` + `dotnet test --filter "FullyQualifiedName~Performance" --nologo -v m`,
  **plus a full-scope fast run** — #519's whole proof is that the suite still builds.
- **Lane.** dotnet-engineer / **Sonnet**. **Runs late**: it touches 73 test files and conflicts with
  every open PR that adds a test.

### WP5 — #455: the re-derived corpus, queries and parity golden. **LAST.**

- **Scope.** Resume `task/pd3-455-public-benchmark-corpus` @ **`ea174faf`** (draft **#499**, OPEN,
  `mergeable: UNKNOWN` — a stale computation, not a conflict signal). Merge `origin/main` in first —
  **never rebase a pushed branch** — re-run the gates **in the foreground on a quiet machine**,
  regenerate the golden if they move, then mark ready.
- **Drift, re-derived today against `05625928`.** Merge base `a747da1a`; main has moved **21 commits /
  139 files** since; the branch owns **11 files**; `comm -12` over the two changed-file sets returns
  **zero collisions**. Waiting stayed cheap, exactly as G10 assumed. **Re-derive this at session open** —
  it is a measurement, not a fact.
- **Files.** `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs`, `…/RealWorldQueries.cs`,
  `benchmarks/README.md`, `docs/reference/embedding-benchmark.md`, `scripts/generate-benchmark-corpus.py`,
  `scripts/src/benchmark_corpus.py`, `scripts/tests/test_benchmark_corpus.py`,
  `tests/AiRaccoon.Tests/Integration/Retrieval/GoldenFileRegenerationTool.cs`,
  `tests/AiRaccoon.Tests/Unit/Retrieval/CorpusFixtureGuardTests.cs`, `…/Unit/Retrieval/README.md`,
  `…/Unit/Retrieval/assets/reference-topk.json`.
- **Acceptance.** No `jsaa-*` id and no verbatim private prose survives in any of the three artefacts;
  `CorpusFixtureGuardTests` is **seen failing** when one is reintroduced; the parity and retrieval gates
  pass against the regenerated golden; any metric that moved is recorded before/after.
- **Gate commands — three, in the foreground.**
  `dotnet test --filter "FullyQualifiedName~ParityGateTests" --nologo -v m` — `ParityGateTests`
  (`tests/AiRaccoon.Tests/Integration/ParityGateTests.cs`) carries `[Trait(Speed, Nightly)]` at `:16-17`
  (class at `:19`), so it is **in no Fast lane** and must be named explicitly. Then
  `dotnet test --filter "Category=Retrieval" --nologo -v m` (**19 files** carry
  `TestCategories.Retrieval`, `TestCategories.cs:19`), then
  `dotnet test --filter "FullyQualifiedName~CorpusFixtureGuardTests" --nologo -v m`.
- **Lane.** dotnet-engineer / **Sonnet** runs and regenerates; architect / **Opus** reviews any metric
  that moved before the golden is accepted.

---

## How work lands

Unchanged from session 4's §How work lands, by reference, not restated: one WP = one PR on
`task/pd5-<slug>` in its own worktree; draft at commit #1 and **push after every commit**; **RED
first** with the expected failure named; `@pd5-<wp> Ready to review` then poll every 5 min and merge on
the substantive review with a green rollup; class-level traits on every new test class; **no wall-clock
assertions**; `dotnet build` plus the WP's one `--filter`, never the unfiltered suite; merge
`origin/main`, never rebase, never `git stash`; scratch banks only (`--data-root`, `--port 79xx`,
`--idle-timeout 0`), never `~/.ai-raccoon`, never port 7721; **Sonnet implements, Opus plans and
reviews, and the reviewer is never the implementer**.

## Sequencing

**Wave 1 — WP1 and WP2 in parallel** once `6a`/`6b` are confirmed on main. File-disjoint: WP1 owns
`CliCommandTree.cs`, `Commands/ProjectCommands.cs`, `Commands/CommandsRegistration.cs`; WP2 owns
`docs/reference/agent-memory-server.md` and `ai-raccoon.ignore`.
**Wave 2 — WP3, alone on a quiet machine**, if it survives handover. A measurement collision, not a
file one.
**Wave 3 — WP4**, after every other PR that adds a test has merged.
**Wave 4 — WP5 (#455), strictly last**, then the owner's S6b behind it.

## Owner-only items

| Item | The session prepares | Strictly the owner's |
|---|---|---|
| **S6b (#414)** | Nothing new — the runbook and `scripts/verify-history-scrubbed.py` are merged | Lifting the push guard, running `filter-repo` over **all five path spellings** (three current paths plus two historical spellings of `reference-topk.json`), re-planting refs, telling every session to re-clone. **Preconditions: zero open PRs, no worktree but main, and #455 merged** |
| **1.33.x publish** | Nothing | The production-environment approval on the publish job |
| **`~/.claude/settings.json` soft-deny** | The exact patterns, in #474 §5.2 | Editing the file — an agent must not touch it |

## Owner gate — **none yet**

There is no gate form for this session, and inventing one would waste the device. Every decision this
plan needs has already been ruled on: **G8** moved parts 4 and 5 here and named the trio that precedes
them; **G10** moved #455 here and kept it last; **G7** is a standing ruling; **G4** and the EventId 428
fix settled both #524 calls. The one ambiguity this plan met — whether the `ai-raccoon.ignore` *append*
is code (part 4) or docs (part 5) — is resolved by ADR-0089 decision 8 itself, and the architect took
it rather than spending a card on it.

**A gate becomes necessary only if session 4's close changes the shape**, and there are exactly two
ways that happens: `6a`/`6b` do not merge (then WP1 cannot open, and whether session 5 finishes them
or re-scopes is an owner call), or a wave-1 WP lands partially and its remainder needs a verdict rather
than a resume point. **Raise the gate at rev 1.0, from the filled placeholder row — not before.**
