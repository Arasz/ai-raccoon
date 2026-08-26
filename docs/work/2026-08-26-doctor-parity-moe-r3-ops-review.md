# REVIEW R3 — ops / docs / release review

Independent review lane of task `doctor-feature-match`, 2026-08-26, by a reviewer who
wrote none of the planning lanes. Companion docs: the research record and lanes P1-P4 in this directory.

---

**Reviewer role:** operations, documentation, release. I wrote none of the four lanes. Nothing in the repo was edited. Worktree read: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/doctor-feature-match` @ `31274575` (branch `task/doctor-feature-match`); all `file:line` citations are that tree. `VERSION` = **1.35.0**.

**Lanes reviewed:** P1 contract (`docs/work/2026-08-26-doctor-parity-moe-p1-contract.md`), P2 implementation, P3 tests, P4 observability, plus the research record whose §3.3 is now corrected in place (`docs/work/2026-08-26-doctor-memory-embedding-research.md:83-99`) — I re-read it and confirm the correction landed.

**Note on evidence sourcing:** the repo's memory-first gate directed me to `memory_search` first; the `ai-raccoon` MCP server was unreachable (5 consecutive failures — itself consistent with the owner's bank being ToolGate-locked, §6). Every fact below is therefore read directly from source, which is the stronger evidence anyway per `check-sources-not-yourself`.

---

## 1. DOCUMENTATION SET — derived, not guessed

I enumerated the doc surfaces by grepping for what actually cites doctor's output, the drain series and the event ids, then checked each one against the tests that read docs at runtime. The authoritative list of **real** doc gates is the set of files opened by `TestData.RepoFile("docs/…")`:

```
docs/adr/README.md                        -> AdrIndexTests            (5 tests, derived)
docs/reference/logging-event-ids.md       -> LoggerMessageEventIdTests (5 tests, derived)
docs/reference/agent-memory-server.md     -> ToolInventoryTests:171,182 ; ToolRefusalsTests:541
docs/how-to/configure-embedding-engines.md-> DefaultCodeModelCommandTests:111-115
docs/plans/2026-08-14-…                   -> test *data* only, not a gate
```

Everything else is **human diligence**. I will say so plainly rather than inventing a gate, per the brief.

Two mechanical facts shape every row below:

- `build.yml:71` classifies a diff as code-relevant with `CODE_REGEX` that **includes** `^docs/adr/|^docs/reference/|^docs/how-to/|^docs/plans/` (comment at `:69-70`: "docs/ subdirs are included because 8 test files read docs at runtime via TestData.RepoFile — a docs-only PR that breaks one must go red (#541)"). So a docs-only edit to the how-to **does** run the fast lane. A gate placed on the how-to therefore fires.
- `CODE_REGEX` **excludes** `docs/work/**`, root `README.md`, and `VERSION`. A PR touching only those gets `code=false` and every heavy job skips its steps (`build.yml:105-107`). Nothing verifies them.

### 1.1 The doctor-output surfaces

| Surface | file:line | What must change | Gate |
|---|---|---|---|
| how-to healthy-bank sample | `docs/how-to/configure-ai-raccoon-server.md:330-337` (`user_version: 10` at `:333`, `application_id: -519479064` at `:334`) | **Regenerate from a real run**, not append. It is already wrong at v10/`-519479064` against a v11/`-1765263351` binary, and P1 adds lines 4/7/9 plus a status arm | **None today.** No test reads this file. Recommend the derived gate in §2 |
| how-to exit-code table | `:357-364` | New row `\| 24 \| MIGRATION IN PROGRESS — the model-migration outbox is open; all MCP tool calls are refused until it drains \|` | **None.** `ExitCodeTests.EveryExitCode_IsDistinct` / `EightStaysRetired` gate the enum, never the doc |
| how-to scriptability prose | `:355` — "Exit code is `0` when healthy and non-zero on a mismatch, so it composes into a script" | **Becomes false.** 24 is non-zero and explicitly *not* a mismatch (P1 Decision C: 24 is reachable only from the `Healthy` arm). Reword to distinguish "schema mismatch" from "bank not usable yet" | **None** |
| reference: embedding configuration matrix | `docs/reference/agent-memory-server.md:934-939` — "resolves exactly **two** engines", two rows (Local / OpenAI-compatible) | **Third row for the unconfigured state**, in the words the CLI already uses. This is the direct doc consequence of the corrected §3.3 | **None** |
| reference: doctor remedy sentence | `docs/reference/agent-memory-server.md:196-201` | Memory-side sentence once the memory setup command becomes a constant | `ToolInventoryTests` gates the tool-count heading and inventory, **not** this sentence |
| embedding how-to | `docs/how-to/configure-embedding-engines.md:144-147` | Extend "the one command every surface quotes" to the memory twin | **Real gate**, and it is the template: `DefaultCodeModelCommandTests.TheHowTo_QuotesTheCommandVerbatim` (`:111-115`) |
| README What's new | `README.md:31-46`; doctor mention at `:40` | New entry at the new VERSION | **None** for prose; `VersionContractTests` pins only VERSION's shape |

### 1.2 The new-log-id surfaces (P4)

| Surface | file:line | What must change | Gate |
|---|---|---|---|
| event-id registry count | `docs/reference/logging-event-ids.md:12` (**168**) | 168 → 174 | **Real derived gate**: `LoggerMessageEventIdTests.DocumentedCount_MatchesTheMeasuredCount` |
| event-id registry block | `:84` (`1002-1007 \| …EmbedDrainService.cs`) | `1002-1013 \| …EmbedDrainReporter.cs` + relocation note | **Real derived gates**: `EveryEventIdInSource_FallsInsideADocumentedBlock`, `EventIdBlocks_DoNotInterleaveBetweenOwners`, `EventIds_AreUniqueAcrossTheAssemblies` |
| metric-series semantics (how-to) | `docs/how-to/read-performance-metrics.md:74` — "`drain.<memory\|code>.rows` … **for an embed-drain pass** (EventId 1003, WP11)"; context at `:89`, `:98` | The sentence stops being true: after P4 one point can be an entire lease-held backlog drain | **None** |
| metric-series semantics (reference) | `docs/reference/agent-memory-server.md:320` — "(an embed-drain pass, EventId 1003)" | Same | **None** |
| ADR index | `docs/adr/README.md` | Nothing if P1 §7's "no new ADR" holds; **but** P4's 1012 changes ADR-0076's retry behaviour (§7) | **Real derived gate**: `AdrIndexTests` (directory ↔ index, both directions, plus gap/supersession checks) |

### 1.3 The work-record index

The brief asked whether the four lane docs must be registered. Ruling: **yes, and the repo says so in its own words.** `docs/README.md:29`: *"A directory's `README.md` is a complete map: every file in it, one line of purpose each."* `docs/work/README.md:11-17` carries an **Active records** table whose existing rows include "Active research, not yet implemented" — precisely what a research record plus three un-implemented planning lanes are.

Gate: **none, twice over.** No test reads `docs/work/README.md` (only `docs/adr/README.md` has `AdrIndexTests`), and `docs/work/**` is outside `build.yml:71`'s regex, so a PR that adds the four docs and forgets the index does not even run the test suite. Human diligence only.

Separately: 5 indexed rows against ~139 files in `docs/work/` is the `derive-or-delete-the-list` failure mode in its pure form. Either derive the table (the `AdrIndexTests` shape is ~40 lines and already proven in this repo) or amend the README text so "complete map" is honestly scoped to active records. Do not leave a hand-maintained mirror claiming completeness it does not have.

---

## 2. THE STALE SAMPLE, AS A CLASS OF DEFECT

**Ruling: add the derived gate. It is not over-engineering, and the two invariants named in the brief both point the same way.**

`derive-or-delete-the-list`: the sample block is a hand-maintained mirror of `ReportAsync`'s line set. It drifted from 3 lines at v10 to a binary printing 6 lines at v11 **across two releases** (1.32.0 added the code-engine lines, 1.33.0 added the threads line) and nothing noticed. That is the invariant's exact scenario.

`ask-if-simpler`: the simpler option is the one that already existed and already failed. Both 1.32.0 and 1.33.0 shipped with manual checklist rows exercising doctor's output live (`docs/work/checklist/2026-08-23-1.33.0-release.json` items `doctor-reports-code-engine-state`, `doctor-reports-active-code-model`) — the rows passed, the doc still rotted, because a checklist verifies the *binary*, never the *doc*. So the cheap option is empirically insufficient, which is what promotes the gate from nice-to-have to warranted.

And this is **not new machinery**: `tests/AiRaccoon.Tests/Unit/Docs/AdrIndexTests.cs:7-11` is a doc-index gate whose own doc comment cites `.ai-badger/invariants/derive-or-delete-the-list.md`, and `LoggerMessageEventIdTests` is a doc-content gate over a reference page. Two independent in-repo precedents ⇒ house pattern.

**What it compares — narrowly, on purpose.** Values in the sample are machine-specific (bank path, model directory, `embedding threads: 5 (halved-core default)` is core-count dependent). So the gate compares **structure, not values**:

1. the ordered list of `<label>:` prefixes in the fenced block after `A healthy bank:` equals the ordered label list the report emits — derivable because P1 §1.2 makes the corpus lines come from `CorpusEngine.All`, and P3's `Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder` consumes the same label set;
2. the line **count** matches (this is what catches a dropped line);
3. the sample's `user_version:` numerals equal `MemorySchema.CurrentVersion` and its `application_id:` equals `MemorySchema.SchemaDigest` (this is what catches the v10 rot specifically).

**Where it lives:** `tests/AiRaccoon.Tests/Unit/Docs/DoctorSampleDocTests.cs`, `[Trait(Category, Unit)] [Trait(Speed, Fast)]` — alongside `AdrIndexTests` in the established `Unit/Docs/` home, so it runs in the fast lane (`build.yml:134`) and, critically, fires on a docs-only edit to `docs/how-to/**` because that prefix is inside `CODE_REGEX` (`build.yml:71`).

**Prove-the-check-fails**: delete one label line from the doc block and watch it go red; restore and watch it go green. Per `prove-the-check-fails`, that witnessing is part of the work package, not optional.

**Scope boundary I would enforce:** the gate covers the healthy-bank block only. Do not extend it to the shape-mismatch sample (`:341-344`) — that one is a hand-built illustration of a hypothetical dropped index, and pinning it would couple the doc to `SchemaDoctor`'s finding text for no operator benefit. `ask-if-simpler` applies to the gate too.

---

## 3. RELEASE MECHANICS

### 3.1 Version segment — MINOR, and the precedent is direct

`scripts/version-bump.py` is the only writer of the single hand-written marker (`:13`, `:45`) and prints the What's-new reminder at `:49`. Judged against how this repo versioned comparable changes (`git log -- VERSION`):

- **`1.32.0 — default code model one-command install, 512-token code window, doctor code-engine state (#463)`** — the release that *added doctor output lines* was a **minor**.
- **`1.33.0 — bounded embed drain, ORT thread cap, embed-rows-per-run, code-corpus prune and watch signal (#521)`** — a **minor**, and it is the release whose What's-new entry ends "`doctor` shows the effective thread count" (`README.md:40`), i.e. another doctor line as a minor.
- **New exit codes have shipped as a minor too**: `NoBank=22` and `SettingsServerError=23` were verified live in the 1.33.0 checklist run (`2026-08-23-1.33.0-release.json`, item `cli-exit-codes-nobank-and-settings-error`, anchor "PR #433, #426").
- Patch releases in this repo are explicitly defect/republish: `1.33.5 (defect-fix release)`, `1.33.7 (defect-fix release)`, `1.33.9 (republish with Dapper materialization fix)`.

**Not MAJOR**, even with the exit-code behaviour change: the repo has never cut a major, has repeatedly shipped observable-behaviour changes (search default `kind=both` in 1.34.0, `vec_code` dimension-agnostic in 1.35.0) as minors, and P1 Decision C keeps 19/20 outranking 24 so no previously-non-zero verdict changes meaning. **Not PATCH**: new output lines, a new enum value and a new verdict word are not defect fixes.

**Verdict: MINOR. 1.35.0 → 1.36.0** (and 1.37.0 if the split in §3.3 is taken).

### 3.2 Release notes — and the mechanism that makes the PR title load-bearing

- There is **no CHANGELOG** and by policy there will not be one: `docs/README.md:35` — "The docs tree has no ledger yet — no `CHANGELOG.md` until a ledger exists to generate it."
- `release.yml:19-22` triggers on `push` to `main` filtered to `paths: ["VERSION"]`, validates bare semver (`:54-58`), refuses to move an existing tag (`:70`), then `gh release create … --generate-notes` (`:97`) with the comment: "builds the changelog from merged PRs since the previous tag … one PR per task means PR titles are already the changelog."

So **the PR title is the release note**. Two obligations follow:

1. The PR title must name the exit-code change, e.g. `feat(doctor): report the memory engine + open model migration; new exit 24 (MIGRATION IN PROGRESS)`. A title reading "doctor reports the memory engine" would ship a scripting break invisibly.
2. A README What's new entry at the new version, one line, braggable-feature style, matching the existing voice at `README.md:33-44`. Suggested:

   > **`doctor` reports the memory engine, its pending rows, and an open model migration.** The memory corpus gets the same report the code corpus already had, and an open re-embed outbox is now named out loud — `doctor` exits **24** (`MIGRATION IN PROGRESS`) instead of `HEALTHY`/0 while every MCP tool call is being refused. (1.36.0)

### 3.3 PR shape

File-collision analysis: P1/P2/P3 touch `DoctorCommands.cs`, the new `EngineDoctor`/`CorpusEngineReport`, `ExitCode.cs`, the how-to and the reference page. P4 touches `EntryEmbedder.cs`, `EmbedDrainService.cs`, the new `EmbedDrainReporter.cs`, `AppRegistrations.cs`, `MemorySql.cs` and `logging-event-ids.md`. **The only overlap is `MemorySql.cs`** (P2 §3.3 touches it only under option M3, which P1 did not take — so in practice, zero overlap).

**Recommendation: two PRs, each with its own MINOR bump — P4 first (→ 1.36.0), then the doctor contract (→ 1.37.0).** Reasons, all mechanical:

- `release.yml` makes one VERSION change on main = one tag + one auto-generated release. Two bumps give two releases whose notes each name one coherent change — that is `traceable-releases` satisfied maximally, not doubled work.
- `exit 24` deserves to be greppable to a single tag.
- P4-6 (the registry edit) **must** merge in the same PR as P4-1…5 — `DocumentedCount_MatchesTheMeasuredCount` and `EveryEventIdInSource_FallsInsideADocumentedBlock` go red if the doc leads *or* trails the code. That is an intra-P4 constraint and it argues for P4 being its own PR rather than a lobe of a larger one.
- P4 §7 itself says 1012 "arguably belongs in a defect PR of its own" — I agree it is a liveness fix, not observability, but splitting a third time buys nothing here; keep it in P4 and name it in the title.

If the owner prefers one release, one PR is defensible — but then the single PR title must carry both facts, because it *is* the release note.

**Sequencing tension, flagged because nobody else did:** the owner's stuck bank is the only live fixture for both changes, and P4 shipping first is what makes that bank drain (§6) — which destroys the fixture the doctor lane's manual `exit 24` row needs. Capture the evidence bundle in §6 **before** either PR's manual test, and run the doctor lane's live row against a *copy* of the captured bank.

### 3.4 Packaging and fresh-install

Read and cleared: `scripts/manual-fresh-install-test.py` (invokes the tool only as `[tool, "--data-root", dataroot, "--transport", "stdio"]` at `:154-156`), `scripts/verify-tool-package.py` → `scripts/src/package_verify.py` (packs and inspects the `.nupkg`, `:100-118`), `scripts/version-bump.py`, `scripts/nightly-triage.py`. **None invokes or parses `doctor`** — `grep -rin doctor scripts/` returns zero hits. No new files, assets or RIDs are added, so `publish.yml` (`workflow_dispatch` only, reads VERSION at `:78`) and the pack path are unaffected.

**`--nologo` audit** (asked for explicitly): clean. `scripts/nightly-triage.py:160` is a *comment* explaining why it is absent (fixed in `aa0c8bcf`). Every remaining occurrence is on `dotnet build` or `dotnet pack`, never `dotnet test`: `.github/workflows/build.yml:121,161,212,268`, `.github/workflows/publish.yml:70`, `scripts/src/package_verify.py:111`, `VersionContractTests.cs:56`. Under MTP that is harmless. No finding.

---

## 4. OUTPUT AS A CONTRACT

### 4.1 The real consumers, enumerated

| Consumer | Reads | Would it newly fail? |
|---|---|---|
| `scripts/**` (all of it) | **nothing** — zero `doctor` hits | No |
| `.github/workflows/**` | no doctor invocation | No |
| `tests/…/DoctorCommandsTests.cs` (11 tests) | `ShouldContain` substrings (`:86,104,129,148,162,178`) + 2 SHA-256 no-write witnesses (`:194-201`, `:219-231`) | No — substring matches survive insertion; `Doctor_HealthyBank_ReportsHealthyAndExitsZero` survives because a fresh bank has no `model_migration` row, so `HasOpenModelMigration` = 0 (P1 Decision E) |
| `docs/work/checklist/*.json` | historical quoted output | No — dated records, not executable. P2 R8 is right: do not rewrite them |
| `docs/how-to/configure-ai-raccoon-server.md` | sample block, exit table, `:355` prose | **Yes — all three** (§1.1) |
| Human/external scripts | recruited by `:355` "so it composes into a script" | **Yes, and this is the break** |

### 4.2 Insertion vs append

**New lines may be inserted between existing ones.** P1 §2.4 puts them at positions 4, 7, 9; I verified nothing in-repo indexes doctor's stdout by line number, and every test assertion is a substring. Insertion is materially better than appending because it puts the two engine lines adjacent — which is what exposes the live bank's real config accident (`embedding.model` == `embedding.codeModel`, research §6) without adding a line to say so.

The binding constraints on insertion are (a) every retained line's prefix stays byte-identical, and (b) the line **count is stable across states** — P1 satisfies (b) by printing `model migration: none open` unconditionally. Keep that; a line that appears only in trouble is the line nobody greps for.

### 4.3 The exit code is the actual compatibility break — rule it as such

Line order is a non-event. **`status: HEALTHY`/0 → `status: MIGRATION IN PROGRESS`/24 is a behavioural break**, and it breaks precisely the audience `configure-ai-raccoon-server.md:355` invited. It is defensible — P1 §3.3's analogy to `SchemaNewerThanBinary = 20` (a legitimate, self-clearing, actionable state that already exits non-zero) is sound, and a script that proceeds against a ToolGate-locked bank is a broken script — but *defensible* is not *silent*.

**Release notes MUST call it out explicitly**, in both the PR title (because `--generate-notes` turns titles into the changelog) and the README entry. The wording must give scripts the migration path P1 §3.4 already identified: `19` and `20` outrank `24`, and `24` is emitted only when the shape is clean, so `exit == 24` is itself a positive assertion that the schema is healthy — a caller wanting the old semantics tests `rc == 0 || rc == 24`.

---

## 5. MANUAL LIVE VERIFICATION

Shape taken from the real files, not invented: `docs/work/checklist/2026-08-23-1.33.0-release.json` rows carry `item`, `expected-result`, `anchor`, `command`, `evidence`, `observed-result`, `status`, and (at acceptance time) `accepted` / `acceptance-reason`. Rows below use the seven fields the brief asked for; `observed-result` and `status` are filled at run time.

Coverage rationale:

- **Row 3 is the stuck-bank row** and is reproducible **now**, non-destructively: `doctor` opens read-only (`DoctorCommands.cs:188-205`, never `MemorySchema.EnsureAsync`) and P1 adds only `SELECT`s, so the SHA-256 before/after is both the safety proof and the evidence. It never touches the lease.
- Rows 4 and 5 exist because `exit 24` must not leak: a settled bank must still be `HEALTHY`/0, and an *unreadable* migration row must fall back to 0 (P1 Decision D) — otherwise a broken bank gets a confident wrong verdict, which is the thing `DoctorCommands.cs:111-113` exists to prevent.
- Row 6 covers the one deliberate change to **existing** output (P1 §1.3's false-remedy fix), which no current test covers.
- Row 1 runs on a **fresh install**, the only place the corrected not-configured state (`embedding.provider` absent) is genuinely reachable.
- Rows 7 and 8 are P4's, and row 7 seeds the owner's exact stale-lease state on a scratch bank so the fixture survives the real bank being drained.
- Row 9 is the doc row, and it is the manual fallback if the §2 gate is declined.

The full rows are in SCHEMA-LAST block 3.

---

## 6. THE OWNER'S STUCK BANK — RUNBOOK (ADVICE ONLY; I RAN NOTHING)

### 6.1 Proof that starting the server is sufficient

Every link read from source in this worktree:

1. **The relay is due purely from the open row.** `ModelMigrationJob.Interval => null` (`src/AiRaccoon.Infrastructure/Maintenance/ModelMigrationJob.cs:23`, "Never due by the clock; HasWorkAsync is the only gate"); `HasWorkAsync` = `SELECT count(*) FROM model_migration WHERE id = 1 AND finished_at IS NULL` > 0 (`:25-29`). The owner's row has `finished_at NULL` ⇒ due.
2. **A cold start runs it before anything else.** `BankMaintenanceHostedService.ExecuteAsync`'s startup pass, comment at `:81-86`: *"Also the crash-recovery guarantee for an on-demand job (ADR-0076): RunOnceAsync always calls _jobRunner.RunDueAsync, which always asks each job's HasWorkAsync, regardless of any cadence or poll interval below."* Thereafter every `OnDemandPollInterval` = 15 s (`:79`).
3. **The expired lease is reclaimed, not blocked.** `ModelMigrationJob.RunAsync:38` → `IEntryEmbedder.DrainMigrationAsync` → `migrationLease.TryAcquireAsync` (`EntryEmbedder.cs:102`) → `SqliteModelMigrationLease.TryAcquireAsync:39-45` executes `MemorySql.AcquireModelMigrationLease` (`MemorySql.cs:495-497`):

   ```sql
   UPDATE model_migration SET lease_owner = @owner, lease_expires_at = @expiresAt
   WHERE id = 1 AND finished_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < @now)
   ```

   `lease_expires_at` = 1787740592 is in the past ⇒ the WHERE matches ⇒ 1 row affected ⇒ `true`. The interface states this as the design: *"a crashed holder never releases — expiry alone frees the lease for the next relay pass"* (`IModelMigrationLease.cs:8-10`).
4. **The holder died hard, which is why the columns are still populated.** `ReleaseAsync` nulls **both** columns (`MemorySql.cs:503-504`) and runs in a `finally` (`EntryEmbedder.cs:137-140`). Both are still set ⇒ that `finally` never executed ⇒ the process was killed, it did not throw. A throw would additionally have produced a 526 at `MaintenanceJobRunner.cs:86`. Nothing is wedged; nothing needs unwedging.
5. **It then drains to completion and reopens the bank.** `while (true)` over `BatchSize = 32` (`EntryEmbedder.cs:24`, loop `:118-130`), renewing the 60 s lease after every batch (`:129`; `LeaseTtl` `IModelMigrationLease.cs:34`), exits on an empty batch (`:123-126`), then `FinishModelMigration` (`:132-134` / `MemorySql.cs:491-493`) sets `finished_at` and nulls the lease ⇒ `HasOpenModelMigration` = 0 ⇒ `ToolGate.RequireBankAvailableAsync` stops throwing `model-migration-in-progress` (`src/AiRaccoon/Tools/ToolGate.cs:23-30`).

**Conclusion: the entire remediation is `ai-raccoon serve`. No SQL, no lease surgery, no file edits.**

### 6.2 Least-destructive sequence

All of step 1 is read-only. Nothing here is authorised — this is the sequence I would hand the owner.

**Step 1 — capture the evidence bundle first (read-only; this is the fixture three lanes' tests were designed from).**

```bash
D=~/ai-raccoon-evidence-2026-08-26 && mkdir -p "$D"
shasum -a 256 ~/.ai-raccoon/memory.db | tee "$D/sha-before.txt"
# consistent snapshot without touching the live file's WAL:
sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro" ".backup '$D/memory-evidence.db'"
sqlite3 -readonly ~/.ai-raccoon/memory.db \
  "SELECT provider,model,engine,started_at,finished_at,lease_owner,lease_expires_at FROM model_migration WHERE id=1;" > "$D/model_migration.txt"
sqlite3 -readonly ~/.ai-raccoon/memory.db \
  "SELECT count(*), sum(embed_state='pending') FROM entries;" > "$D/pending.txt"
sqlite3 -readonly ~/.ai-raccoon/memory.db \
  "SELECT key,value FROM settings WHERE key LIKE 'embedding.%' AND key <> 'embedding.apiKey';" > "$D/settings.txt"
ai-raccoon doctor > "$D/doctor-before.txt" 2>&1 ; echo "exit=$?" >> "$D/doctor-before.txt"
shasum -a 256 ~/.ai-raccoon/memory.db | tee "$D/sha-after-doctor.txt"   # must equal sha-before
```

The two sums must match — that is the same read-only proof `DoctorCommandsTests:219-231` asserts, executed on the real bank. Also capture one refused `memory_search` verbatim: it is the `evidence` field for the `doctor-explains-a-refused-memory-tool-call` row and it disappears the moment the drain finishes.

**Step 2 — decide the engine question BEFORE draining. This is the only irreversible choice.**

`embedding.model` and `embedding.codeModel` point at the same directory (`Salesforce__SFR-Embedding-Code-400M_R`, research §6): a 1024-dim code-tuned model is about to embed 47,723 prose rows. And the order is **forced**, not preferable: `MemorySql.StartModelMigration`'s upsert carries `WHERE model_migration.finished_at IS NOT NULL` (`MemorySql.cs:481-490`), whose own comment says it "only ever moves a closed (or absent) row to open, never overwrites one already open … affects 0 rows — the caller's signal to refuse rather than clobber." So `ai-raccoon model embedding set local <other-dir>` **cannot** take effect while this row is open. Therefore: drain first, then re-point — and re-pointing opens a *second* full migration. Cost of getting this wrong is one extra full drain.

**Step 3 — start the server with the idle watchdog disabled.**

```bash
ai-raccoon serve --idle-timeout 0        # add --port <n> per the owner's usual setup
```

`--idle-timeout 0` is not cosmetic. `IdleWatchdog` logs "shutting down after {IdleTimeout} without MCP activity" (`src/AiRaccoon/Hosting/Watchdog/IdleWatchdog.cs:82`) and is armed whenever `IdleTimeout > TimeSpan.Zero` (`WatchdogRegistrations.cs:12`). During the migration **every** MCP call is refused, so there may be no MCP activity at all for hours — the watchdog can therefore shut the server down mid-drain and recreate exactly the stale lease we are recovering from. `0` disables it (`IdleTimeoutParser.cs:14-17`), and it is what this repo's own checklist runs use.

**Step 4 — watch. Today there is almost nothing to watch, which is P4's defect.**

Expect `MaintenanceJobRunner[525] maintenance job 'finish an interrupted embedding-model migration' (model-migration) ran in …` and then silence for the duration: `EntryEmbedder` declares zero `[LoggerMessage]` methods. The only live progress signal available today is an external read-only poll:

```bash
watch -n 60 "sqlite3 -readonly ~/.ai-raccoon/memory.db \"SELECT count(*) FROM entries WHERE embed_state='pending';\""
```

**Step 5 — done when the row closes.** `finished_at` non-NULL and pending 0; MCP memory tools answer again.

### 6.3 What it costs — derived, not guessed

From the bank's own numbers: 51,947 − 47,723 = **4,224 rows** were embedded, and the last successful renewal was `lease_expires_at − LeaseTtl` = 1787740592 − 60 = 1787740532, i.e. **1,051 s (17.5 min)** of real work ⇒ **4.02 rows/s**.

| Quantity | Value |
|---|---|
| Rows remaining | 47,723 |
| At the measured 4.02 rows/s | **11,874 s ≈ 198 min ≈ 3.3 hours** |
| Batches (`BatchSize = 32`) | 1,492 |
| Lease renewals | 1,492 |
| MCP memory tools refused | **the entire ~3.3 h** (`ToolGate.cs:25-29`) |
| Log lines produced today | ~1 (`525`), then nothing |

So "a ~47.7k-row local embed run" is, on this machine, **a ~3.3-hour full-bank outage**, not a coffee break. Plan it accordingly.

### 6.4 What could go wrong

| Risk | Mechanism | Mitigation |
|---|---|---|
| Idle watchdog kills the drain | `IdleWatchdog.cs:82`; no MCP traffic because everything is refused | `--idle-timeout 0` |
| Machine sleeps / process killed again | no durable loss: rows stay `pending`, row stays open, lease expires in ≤60 s | just start again; this is the crash-recovery guarantee |
| Two drainers on the same rows | `PendingEmbedJob.HasWorkAsync` has no migration gate and `EmbedPendingBatchAsync` takes no lease (P4 §7) | not corruption (`MarkEmbedded` is per-row idempotent); costs duplicate inference only |
| Wrong engine gets committed | §6.2 step 2 | decide before starting |
| WAL-inconsistent evidence copy | plain `cp` of a live `-wal` set | use `.backup` as above, with no writer running |
| Thermal/throughput drift | 3.3 h is extrapolated from a 17.5 min sample | treat as an estimate; the pending-count poll is ground truth |

---

## 7. LOG / METRIC OPERATIONAL IMPACT (P4)

### 7.1 Is a time-strided line at the lease TTL the right cadence? Yes — but P4's own volume number is wrong by 11×

The **design** is right, and right for a derived reason: the stride is `SqliteModelMigrationLease.LeaseTtl` (60 s), a period the code already has meaning for ("a drain that renews is a drain that reports"), and it is O(elapsed time), not O(rows) — so it cannot flood no matter how large the backlog. P4-5's acceptance criterion (exactly one 1013 per stride crossed; **zero** when the whole drain fits inside one stride) is the right anti-flood assertion, and metric volume genuinely stays at 1 point per drain because progress lines record no measurement (`IOperationTelemetry.cs:15-21`).

**But the number P4 publishes is wrong.** P4 §7 states "**~18 lines** for the owner's 17.5-minute drain", and P4 §5's registry edit hard-codes it: "1013 a time-strided progress heartbeat at the lease TTL so a 47k-row drain reports **~18 lines** rather than 1,492". That conflates *the drain ran for 17.5 minutes* with *the drain is 17.5 minutes of work*. The 17.5 minutes cleared only 4,224 rows; the remaining 47,723 at the same 4.02 rows/s is **198 minutes ⇒ ~198 lines** (§6.3). The figure is 11× low, and it is destined for a **permanent row in a gated reference doc**.

Operationally ~198 Information lines over 3.3 h (≈1/min) is still the correct cadence — this is a wording/arithmetic defect, not a design defect. Correction: publish the invariant, not a row count — "one line per lease TTL; O(elapsed time), not O(rows)" — and if a number is wanted, derive it as `ceil(elapsed / LeaseTtl)`. Hard-coding "~18" in `logging-event-ids.md` recreates the stale-sample defect §2 is about, in the one doc the repo insists is measured rather than hand-maintained (`logging-event-ids.md:10-13`).

### 7.2 Does reusing the drain series break a documented assumption? It breaks two documented sentences; no dashboard is provable

- **Bimodality is real and P4 admits it** (128 vs 47,723). What P4 misses is that the meaning of the series is **documented prose in two gated-directory files**: `docs/how-to/read-performance-metrics.md:74` — "`drain.<memory|code>.rows` and `.duration_ms` **for an embed-drain pass** (EventId 1003, WP11)" — and `docs/reference/agent-memory-server.md:320` — "(an embed-drain pass, EventId 1003)". After P4 a point can be an entire lease-held backlog drain. P4 §5 asserts "**exactly two edits**" to docs; it is four. Neither of these two has a test gate, so this is human diligence and must be written into the work package.
- **Retention does not guarantee the outlier ages out.** P4 leans on "28-day retention ages the outlier out", but the how-to documents retention as *best-effort*: `metrics.retention-days.global`, default `28` days, annotated "(best-effort — holding more is not a violation)" (`configure-ai-raccoon-server.md:298`). Weaken the claim to what the contract actually says.
- **No dashboard exists in this repo.** I looked; the documented consumer is `memory_performance` under the reserved `__self_metrics__` project id (`read-performance-metrics.md:96-98`), plus the how-to's reading guidance. So "breaks a dashboard" is not provable and should not be claimed either way — say plainly that the affected consumer is the how-to's reading guidance and the `memory_performance` report, and fix the prose.
- **Cardinality genuinely unchanged**: `corpus` is the only dimension and `EmbedCorpus` has two values. Reusing the series adds points, not series. P4 is right, and right to reject a `migration` dimension against `InternalSeriesPrefixes`.

### 7.3 Does metrics config need a release note?

- **Buffer capacity: no note needed.** Default 1,000 (`configure-ai-raccoon-server.md:296`, `metrics.buffer-capacity.global`). P4's rejected per-batch design would have emitted 1,492 records — I confirmed 1,492 > 1,000, so the `metrics.dropped` prediction is correct. The **chosen** design records one point per drain, so there is no buffer pressure and nothing for an operator to retune. Do not put a knob in the notes that nobody needs to turn.
- **Retention: no note needed** either — nothing changes the window.
- **One note IS warranted**, and it is about reading the data, not configuring it: `drain.memory.*` can now contain a whole-backlog outlier alongside ordinary 128-row passes. That belongs in the how-to's own bullet (the doc edit above), and one clause in the release entry for operators who chart those series.

---

# SCHEMA-LAST

## Block 1 — findings

| id | severity | lane/section | finding | correction |
|---|---|---|---|---|
| OPS-01 | MUST | P4 §5 ("exactly two edits") | The doc list is incomplete. Reusing 1003 / `drain.memory.*` for a whole-backlog drain falsifies `docs/how-to/read-performance-metrics.md:74` ("for an embed-drain pass") and `docs/reference/agent-memory-server.md:320` ("an embed-drain pass, EventId 1003"). Neither has a test gate. | Change "exactly two edits" to four. Add both sentences to WP-P4-6 with the migration-drain case named, and record that the gate is reviewer diligence, not a test. |
| OPS-02 | MUST | P4 §5 edit 2, §7 | "~18 lines for the owner's 17.5-minute drain" is wrong by ~11×: 17.5 min cleared only 4,224 of 51,947 rows (4.02 rows/s), so the remaining 47,723 rows ≈ 198 min ⇒ ~198 lines. The figure is written into a permanent row of a doc the repo insists is measured (`logging-event-ids.md:10-13`). | Delete the hard-coded count. State the invariant: one line per `LeaseTtl`, O(elapsed time) not O(rows). If a number is needed, derive `ceil(elapsed / LeaseTtl)`. |
| OPS-03 | MUST | P1 §0.1 / §5 consequence (unowned) | `docs/reference/agent-memory-server.md:934-939` says the engine "resolves exactly **two** engines" with two rows and no unconfigured row — the exact claim the corrected research §3.3 refutes. Listed by no lane. | Add a third row for `embedding.provider` absent, phrased with the CLI's existing words (`no engine (FTS5-only search)`, `SettingsCommands.cs:273`; `provider: (none — FTS5-only search)`, `:314-317`), and drop "exactly two". |
| OPS-04 | MUST | P1 §3.4 | Only the exit-code *table* is named. The prose at `docs/how-to/configure-ai-raccoon-server.md:355` — "Exit code is `0` when healthy and non-zero on a mismatch, so it composes into a script" — becomes false: 24 is non-zero and explicitly not a mismatch (Decision C). | Reword `:355` to separate "schema mismatch" (19/20) from "bank not usable yet" (24), and give scripts the `rc == 0 \|\| rc == 24` migration path. |
| OPS-05 | MUST | P1 §3.4 | The exit-code table `:357-364` must gain row `24`, and it is an ungated hand-maintained mirror of `ExitCode.cs` (`ExitCodeTests` gates only distinctness and 8's retirement). | Add the row in the same PR. Then either derive the table in a `Unit/Docs` test (the `AdrIndexTests` shape) or state in the doc that it is hand-maintained — `derive-or-delete-the-list`. |
| OPS-06 | MUST | cross-lane (docs index) | The four lane docs are not registered in `docs/work/README.md:11-17`, which `docs/README.md:29` defines as "a complete map: every file in it". No gate exists, and `docs/work/**` is outside `build.yml:71`'s `CODE_REGEX`, so such a PR runs no tests at all. | Add four rows (research record + P1/P2/P3/P4) to the Active-records table in the PR that lands them. Gate: human diligence — say so, do not imply CI covers it. |
| OPS-07 | MUST | P1 §2.3 constant-source note | The obligation is flagged but owned by no work package. `ai-raccoon model embedding set local` is hand-spelled at `EmbeddingAvailability.cs:34,37` and `BundledModel.cs:88,98`; doctor would be the fifth. | Make it a gated WP: a `Core/Memory` constant plus a `DefaultCodeModelCommandTests`-shaped test (`:84-118`) asserting doctor's line, the runtime message and `configure-embedding-engines.md` all quote the one constant. |
| OPS-08 | MUST | release mechanics | Segment and notes unstated. Precedent is direct: doctor lines shipped MINOR twice (`1.32.0 … doctor code-engine state (#463)`; `1.33.0 …` whose What's-new carries the threads line, `README.md:40`), and new exit codes 22/23 shipped in MINOR 1.33.0; PATCH is reserved for "defect-fix release" (1.33.5/1.33.7). | Bump MINOR: 1.35.0 → 1.36.0 (and 1.37.0 if split). Not MAJOR — 19/20 still outrank 24, no existing verdict changes meaning. Not PATCH. |
| OPS-09 | MUST | release mechanics | `release.yml:97` cuts notes with `gh release create --generate-notes` from merged PR titles ("one PR per task means PR titles are already the changelog"), and there is no CHANGELOG by policy (`docs/README.md:35`). A neutral PR title would ship the scripting break invisibly. | PR title must name it, e.g. `feat(doctor): report the memory engine + open model migration; new exit 24 (MIGRATION IN PROGRESS)`. Add a README What's new entry at the new VERSION that states the exit-code change and the `rc == 0 \|\| rc == 24` path. |
| OPS-10 | MUST | P1 §2.4 | Claim that the how-to sample and checklists "survive as *incomplete*, not as *wrong*" is false for the sample: it is **already** wrong (`user_version: 10` at `:333`, `application_id: -519479064` at `:334`) against a v11/`-1765263351` binary. | Restate: order-preserving insertion keeps the *checklists* merely incomplete; the sample block must be **regenerated from a real run**, not appended to. |
| OPS-11 | SHOULD | P3 §8 option (1) | The derived doc-sample gate is offered as an option; it should be adopted, and it is effective — `docs/how-to/` is inside `build.yml:71`'s `CODE_REGEX`, so a docs-only edit still runs the fast lane. Two in-repo precedents exist (`AdrIndexTests`, `LoggerMessageEventIdTests`). | Add `tests/AiRaccoon.Tests/Unit/Docs/DoctorSampleDocTests.cs` (Unit/Fast) comparing the fenced healthy-bank block's ordered label prefixes + line count to the report's label set, and its `user_version`/`application_id` numerals to `MemorySchema.CurrentVersion`/`SchemaDigest`. Values are excluded (machine-specific). Witness RED by deleting a label line. |
| OPS-12 | SHOULD | P4 §2 S3 / §7 | 1012 is a behaviour change, not observability: today a blank provider throws every 15 s forever with the bank ToolGate-locked; after, it returns `false` and logs. That changes ADR-0076's retry story. `docs/adr/README.md` is gated by `AdrIndexTests`. | Record the amendment on ADR-0076 (or its index row) in the same PR, as ADR-0091's row already does with "Amends ADR-0076". Name 1012 in the PR title as a liveness fix. |
| OPS-13 | SHOULD | cross-lane (PR shape) | No lane rules PR shape, and `release.yml:19-22` cuts one release per VERSION change landing on main. Files are disjoint except `MemorySql.cs` (untouched by P2 once M3 is dropped). | Two PRs, one MINOR bump each: P4 → 1.36.0, then doctor contract → 1.37.0. P4-6's registry edit must stay inside P4's PR (the id tests go red if doc leads or trails code). One PR is acceptable only if the title carries both facts. |
| OPS-14 | SHOULD | cross-lane (fixture) | The owner's stuck bank is the only live fixture for both changes, and shipping P4 is what drains it — destroying the fixture the doctor lane's `exit 24` row needs. | Capture the §6.2 step-1 evidence bundle before either manual test; run the live `exit 24` row against the captured copy via `--data-root`. Row 7 of block 3 seeds the same state synthetically so the fixture outlives the bank. |
| OPS-15 | SHOULD | P4 §7 | "28-day retention ages the outlier out" overstates the contract: retention is documented best-effort — "holding more is not a violation" (`configure-ai-raccoon-server.md:298`). | Weaken to: retention *may* age it out; the operator-facing guarantee is 1008's `{Owed}` count, so nobody needs the histogram to get the number. |
| OPS-16 | SHOULD | release mechanics (gate hole) | `VERSION` is not in `build.yml:71`'s `CODE_REGEX`, so a bump-only PR gets `code=false`, skips the fast lane — including `VersionContractTests`, the very test pinning the version contract — and merging it immediately tags and releases (`release.yml:19-22`). Latent, not triggered here (this PR also touches `src/`). | Add `^VERSION$` to `CODE_REGEX`. Cheap, and it closes a hole on the one file whose landing on main is irreversible. |
| OPS-17 | SHOULD | P2 R6 / P3 §0 | A committed merge marker `>>>>>>> origin/main` sits at `docs/reference/agent-memory-server.md:196`, immediately beside text this task edits. No gate catches it. | Fix it in its own commit with its own message, before the doc edits, so it does not ride silently inside a drift diff. |
| OPS-18 | SHOULD | P1 §0.1 wording consequence | Four spellings of "memory engine unconfigured" now exist: `no engine (FTS5-only search)` (`SettingsCommands.cs:273`), `provider: (none — FTS5-only search)` (`:314-317`), the code-doctor grammar (`DoctorCommands.cs:67-69`), and P1's proposed memory line. Doctor adopting doctor's own grammar is **correct** — but the reference page only documents the FTS5-only consequence for the *code* corpus (`agent-memory-server.md:84-85,976`). | Keep P1's grammar. Fix the asymmetry in OPS-03 by using "FTS5-only search" in the new reference row, so an operator grepping that phrase finds the memory case too. Enforce the command-string constant via OPS-07. |
| OPS-19 | NICE | scripts | `scripts/manual-fresh-install-test.py` never runs `doctor` (`:154-156` launches stdio only; zero `doctor` hits anywhere in `scripts/`). A fresh install is the only place the corrected not-configured arm is genuinely reachable. | Add one `doctor` step asserting `memory engine: not configured — run '…'` and `memory rows pending: 0` on the freshly installed tool. |
| OPS-20 | NICE | P2 R8 / research §4 note | Release checklists quote `ai-raccoon model set code default` while the constant is `ai-raccoon model code set default` — quoted command strings demonstrably drift, and nothing compares them. | Optional `Unit/Docs` gate scanning `docs/work/checklist/*.json` for `ai-raccoon model …` strings absent from the command tree. Low value on historical records; the real fix is OPS-07's constant. |
| OPS-21 | NICE | P3 §7 | The doctor gate filter would silently pass if the class filter is ever loosened; P3 notes `--minimum-expected-tests` is available but does not adopt it. | Add `--minimum-expected-tests 11` (rising with the new tests) to the class-filter gate command. `--nologo` audit: clean — every occurrence is on `dotnet build`/`dotnet pack`, and `nightly-triage.py:160` is a comment explaining its absence. |

## Block 2 — documentation surfaces

| doc surface | change | gate |
|---|---|---|
| `docs/how-to/configure-ai-raccoon-server.md:330-337` | Regenerate the healthy-bank sample from a real run: 6 → 11 lines, `user_version: 11`, `application_id: -1765263351`, plus `memory engine:`, `memory rows pending:`, `model migration: none open` | **None today.** Proposed: `Unit/Docs/DoctorSampleDocTests` (labels + count + version numerals). Runs on docs-only edits because `^docs/how-to/` is in `build.yml:71` |
| `docs/how-to/configure-ai-raccoon-server.md:355` | Reword the scriptability sentence: 24 is non-zero but not a mismatch; give `rc == 0 \|\| rc == 24` | **None** — reviewer diligence |
| `docs/how-to/configure-ai-raccoon-server.md:357-364` | Add `\| 24 \| MIGRATION IN PROGRESS — the outbox is open; all MCP tool calls are refused until it drains \|` | **None** (`ExitCodeTests` gates the enum only). Optional derived gate over `ExitCode.cs` |
| `docs/reference/agent-memory-server.md:934-939` | Drop "exactly two engines"; add the unconfigured row using "FTS5-only search" | **None** — `ToolInventoryTests` gates only the tool heading/inventory |
| `docs/reference/agent-memory-server.md:196-201` | Memory-side remedy sentence once the constant exists; fix the `>>>>>>> origin/main` marker at `:196` first, separately | **None** for this prose |
| `docs/reference/agent-memory-server.md:320` | "an embed-drain pass, EventId 1003" → also the lease-held migration drain (whole backlog, one point) | **None** |
| `docs/how-to/read-performance-metrics.md:74` (context `:89`, `:98`) | Same: `drain.<corpus>.*` now covers a bounded pass **and** a whole-backlog migration drain; note the bimodality for anyone charting it | **None** |
| `docs/how-to/configure-embedding-engines.md:144-147` | Extend the "one command every surface quotes" paragraph to the memory twin | **Real gate**: `DefaultCodeModelCommandTests.TheHowTo_QuotesTheCommandVerbatim:111-115` — replicate it for the memory constant |
| `docs/reference/logging-event-ids.md:12` | **168** → **174** | **Real derived gate**: `DocumentedCount_MatchesTheMeasuredCount` |
| `docs/reference/logging-event-ids.md:84` | `1002-1007 \| EmbedDrainService.cs` → `1002-1013 \| EmbedDrainReporter.cs` + relocation note **without** the "~18 lines" figure | **Real derived gates**: `EveryEventIdInSource_FallsInsideADocumentedBlock`, `EventIdBlocks_DoNotInterleaveBetweenOwners`, `EventIds_AreUniqueAcrossTheAssemblies` |
| `docs/adr/README.md` | No new ADR (P1 §7). Amend ADR-0076's row for 1012's retry-behaviour change | **Real derived gate**: `AdrIndexTests` (5 tests, both directions + gaps + supersession) |
| `README.md` What's new | One entry at the new VERSION naming the memory-engine report **and** exit 24 | **None** — and note root `README.md` is outside `CODE_REGEX`, so a README-only PR runs no tests |
| `docs/work/README.md:11-17` | Four new Active-records rows (research + P1 + P2 + P3 + P4) | **None**, and `docs/work/**` is outside `CODE_REGEX` so no job runs. Human diligence; consider deriving the table |
| `docs/work/checklist/<date>-1.36.0-*.json` | New dated run file with the rows in block 3 | **None** — historical record. Do not edit prior checklists |
| PR title (release note) | Must name exit 24 — `release.yml:97` generates notes from PR titles | **None** — reviewer diligence, but mechanically load-bearing |

## Block 3 — manual live-verification rows

```json
[
  {
    "item": "doctor-reports-memory-engine-state-on-a-fresh-install",
    "expected-result": "On a scratch --data-root with no embedding.provider row, `ai-raccoon doctor` prints `memory engine: not configured - run 'ai-raccoon model embedding set local' to enable semantic memory search` and `memory rows pending: 0`. After `ai-raccoon model embedding set local`, the same command prints the resolved engine arm naming the model and its directory, and the not-configured wording is gone. The remedy string is byte-identical to the one constant the runtime message and configure-embedding-engines.md quote (no fifth hand-spelling).",
    "anchor": "P1 SS0.1, SS2.3 arm 4; SettingsCommands.cs:119,158,273,314-317; EmbeddingAvailability.cs:34; BundledModel.cs:88",
    "command": "ai-raccoon --data-root root-fresh doctor ; ai-raccoon --data-root root-fresh model embedding set local ; ai-raccoon --data-root root-fresh doctor ; grep -n 'model embedding set local' docs/how-to/configure-embedding-engines.md",
    "evidence": "<paste both doctor blocks verbatim and the how-to grep hit>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "doctor-memory-rows-pending-is-alive",
    "expected-result": "On a fresh scratch bank `memory rows pending: 0`; after ingesting one markdown file with no engine configured (so the row lands embed_state='pending'), `memory rows pending: N` with N > 0 matching an independent read-only COUNT. The number moves from a genuine 0 to a genuine N - not a static or always-0 report.",
    "anchor": "P1 SS2.2; MemorySql.CountPendingEmbed MemorySql.cs:362-363; twin of the 1.33.0 item doctor-reports-code-engine-state",
    "command": "ai-raccoon --data-root root-pending doctor ; memory_ingest_file one .md ; ai-raccoon --data-root root-pending doctor ; sqlite3 -readonly root-pending/memory.db \"SELECT count(*) FROM entries WHERE embed_state='pending';\"",
    "evidence": "<paste the 0 -> N pair and the sqlite3 count>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "doctor-open-migration-reports-it-and-exits-24-on-the-live-bank",
    "expected-result": "Against the owner's live bank in its 2026-08-26 state (model_migration id=1 open, lease_owner set, lease_expires_at in the past, ~47,723 of 51,947 pending), `ai-raccoon doctor` prints `model migration: open since 2026-08-26T10:18:01Z (all MCP tool calls are refused until it finishes)`, `memory rows pending: 47723`, `status: MIGRATION IN PROGRESS (schema shape is healthy; memory search is refused until the re-embed finishes)` and exits 24 - where 1.35.0 printed status: HEALTHY and exited 0. The bank is byte-identical before and after (doctor must not touch the lease or the row), and a `memory_search` attempted in the same window is still refused with model-migration-in-progress. NON-DESTRUCTIVE: read-only, no remediation, evidence preserved.",
    "anchor": "P1 SS0.3, SS3.2 Decisions A/C/E, SS SCHEMA-LAST line 9/11; MemorySql.cs:470-475; ToolGate.cs:23-30; DoctorCommands.cs:188-205; DoctorCommandsTests.cs:219-231",
    "command": "shasum -a 256 ~/.ai-raccoon/memory.db ; ai-raccoon doctor ; echo \"exit=$?\" ; shasum -a 256 ~/.ai-raccoon/memory.db ; sqlite3 -readonly ~/.ai-raccoon/memory.db \"SELECT provider,started_at,finished_at,lease_owner,lease_expires_at FROM model_migration WHERE id=1;\" ; sqlite3 -readonly ~/.ai-raccoon/memory.db \"SELECT count(*), sum(embed_state='pending') FROM entries;\"",
    "evidence": "<paste the doctor block, exit=24, the two identical sha256 sums, the model_migration row, the counts, and one refused memory_search envelope>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "doctor-settled-bank-still-healthy-and-exit-0",
    "expected-result": "On a bank with no model_migration row, and again on one whose row has finished_at set, doctor prints `model migration: none open` and `status: HEALTHY` and exits 0. Exit 24 does not leak onto a settled bank, and a closed row is reported identically to an absent one.",
    "anchor": "P1 SS3.2 Decision E; P2 SS2 (row null or FinishedAt not null -> None)",
    "command": "ai-raccoon --data-root root-settled doctor ; echo \"exit=$?\" ; sqlite3 root-settled-closed/memory.db \"UPDATE model_migration SET finished_at=strftime('%s','now'), lease_owner=NULL, lease_expires_at=NULL WHERE id=1;\" ; ai-raccoon --data-root root-settled-closed doctor ; echo \"exit=$?\"",
    "evidence": "<paste both doctor blocks and both exit codes>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "doctor-unreadable-migration-falls-back-to-exit-0",
    "expected-result": "On a COPY of a bank with the model_migration table dropped, doctor prints `model migration: unreadable` and still exits 0 (never 24) - a broken bank must never receive a confident migration verdict. The pending count and both engine lines still print. Run against a copy only; the live bank is untouched.",
    "anchor": "P1 SS3.2 Decision D; DoctorCommands.cs:111-113; P2 SS4 TableExistsAsync guard table",
    "command": "cp -R root-settled root-nomig ; sqlite3 root-nomig/memory.db 'DROP TABLE model_migration;' ; ai-raccoon --data-root root-nomig doctor ; echo \"exit=$?\"",
    "evidence": "<paste the doctor block showing 'model migration: unreadable', the engine and pending lines, and exit=0>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "doctor-unreadable-settings-says-unreadable-not-a-false-remedy",
    "expected-result": "On a COPY with the settings table dropped, both engine lines read `unreadable (settings table missing or unreadable)` rather than the not-configured remedy - closing the pre-existing false-remedy bug for BOTH corpora. This is the one deliberate change to existing 1.35.0 output and no automated test covered it before. code rows pending / memory rows pending still print.",
    "anchor": "P1 SS1.3, Ruling 5c; DoctorCommands.cs:116-118,124-127; P2 SS1.4 (WP5)",
    "command": "cp -R root-settled root-nosettings ; sqlite3 root-nosettings/memory.db 'DROP TABLE settings;' ; ai-raccoon --data-root root-nosettings doctor ; echo \"exit=$?\"",
    "evidence": "<paste the doctor block showing both engine lines as unreadable and the exit code>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "migration-drain-reports-stale-lease-start-progress-and-finish",
    "expected-result": "On a scratch bank seeded with the owner's exact stale state (model_migration id=1 open, lease_owner='dead:1:x', lease_expires_at in the past) and enough pending rows to cross one lease TTL, the server log shows 1009 Warning naming the previous holder and the age, then 1008 Information with the rows owed, then at least one 1013 progress line, then 1003 `Embed drain pass finished for Memory: N row(s)`; memory_performance for the self-metrics project shows drain.memory.rows and drain.memory.duration_ms recorded once; finished_at is set and MCP memory tools answer again. A drain that fits inside one stride emits zero 1013.",
    "anchor": "P4 WP-P4-2/4/5; MemorySql.cs:495-497; IModelMigrationLease.cs:8-10,34; EntryEmbedder.cs:118-134; PromotionQueueService.cs:313-315 (709 precedent)",
    "command": "seed root-stale per above ; ai-raccoon --data-root root-stale serve --port 0 --idle-timeout 0 (backgrounded, log captured) ; grep -E '\\[(1008|1009|1013|1003)\\]' serve.log ; memory_performance projectId=__self_metrics__ ; sqlite3 -readonly root-stale/memory.db \"SELECT finished_at, lease_owner FROM model_migration WHERE id=1;\"",
    "evidence": "<paste the four log lines in order, the two drain.memory series, and the closed row>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "blank-provider-migration-warns-once-and-stops-the-throw-loop",
    "expected-result": "On a scratch bank with model_migration open and embedding.provider absent, each relay pass emits 1012 Warning naming that no provider is configured and that the migration stays open with tools refused, returns without throwing, and leaves finished_at NULL. No 526 generic maintenance-job failure appears. On 1.35.0 the same bank throws every 15s poll forever - that is the RED this row proves.",
    "anchor": "P4 SS2 S3, WP-P4-3; MaintenanceJobRunner.cs:82-89; ToolGate.cs:25-30; BankMaintenanceHostedService.cs:79",
    "command": "seed root-noprov (open migration, no embedding.provider row) ; ai-raccoon --data-root root-noprov serve --port 0 --idle-timeout 0 ; sleep 45 ; grep -cE '\\[1012\\]' serve.log ; grep -cE '\\[526\\]' serve.log ; sqlite3 -readonly root-noprov/memory.db \"SELECT finished_at FROM model_migration WHERE id=1;\"",
    "evidence": "<paste the 1012 line, the 526 count of 0, and finished_at NULL>",
    "observed-result": "",
    "status": "pending"
  },
  {
    "item": "docs-match-the-shipped-binary",
    "expected-result": "The healthy-bank sample block in docs/how-to/configure-ai-raccoon-server.md equals a real run of the built binary on a scratch bank line for line at the current user_version and application_id; the exit-code table lists 24; the scriptability sentence no longer claims non-zero implies a schema mismatch; docs/how-to/read-performance-metrics.md and docs/reference/agent-memory-server.md no longer describe drain.<corpus>.* as only an embed-drain pass; docs/reference/agent-memory-server.md's engine matrix carries the unconfigured row; docs/work/README.md lists all four new lane docs.",
    "anchor": "OPS-01, OPS-03, OPS-04, OPS-05, OPS-06, OPS-10; build.yml:71; docs/README.md:29",
    "command": "dotnet run --project src/AiRaccoon -- --data-root scratch doctor > real.txt ; diff <(sed -n '/A healthy bank:/,/^```$/p' docs/how-to/configure-ai-raccoon-server.md) real.txt ; grep -n '^| \\`24\\`' docs/how-to/configure-ai-raccoon-server.md ; grep -n 'embed-drain pass' docs/how-to/read-performance-metrics.md docs/reference/agent-memory-server.md ; grep -c '2026-08-26-doctor' docs/work/README.md ; dotnet test --project tests/AiRaccoon.Tests --filter \"FullyQualifiedName~DoctorSampleDocTests\"",
    "evidence": "<paste the empty diff, the exit-table row, the reworded metric sentences, the count of 4 work-index rows, and the green doc gate>",
    "observed-result": "",
    "status": "pending"
  }
]
```
