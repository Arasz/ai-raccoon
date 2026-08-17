# Lane review: architecture, layering, consumer surface

Target: `docs/plans/2026-08-17-issue-close-357-367.md` (1002 lines), base `a2a48b3e`.
Scope: layering (WP6), the fusion-confidence flag design, `doctor` as a 2am consumer surface,
`source_state`'s shape, ADR-0077/0078 quality, the bm25 section-weight risk, and reinvented
mechanisms. All findings below were checked against source at the stated paths/lines, not
inferred from the plan's own prose.

---

## 1. Layering — WP6 (major, with one blocker-adjacent gap)

**Verified correct: the pure/impure split.**

- `ReciprocalRankFusion.Fuse` (`src/AiRaccoon.Infrastructure/Sqlite/ReciprocalRankFusion.cs:9-56`) is
  `internal static`, in `AiRaccoon.Infrastructure.Sqlite`. Confirmed.
- `MemorySearchResult` (`src/AiRaccoon.Core/Memory/MemorySearchResult.cs:3-10`) is a Core record with
  a single `double Ranking` and no per-leg fields. Confirmed.
- Critically, the **pre-fusion per-leg lists already exist as Core-typed data** at the fusion call
  site: `SqliteMemoryStore.cs:260-264` builds `ModalityCandidates.ByBm25(ftsBatches, ...)` and
  `ModalityCandidates.ByCosine(vectorBatches, ...)`, both `IReadOnlyList<MemorySearchResult>`, ordered
  by each leg's own raw score (`ModalityCandidates.cs:12-26`) — bm25 ascending, cosine descending —
  before RRF ever combines them. So "the two ranked lists" WP6 6b's `FusionConfidence.cs` takes as
  input are genuine Core types, already materialized in Infrastructure, requiring no new coupling.
  **The plan's placement is correct**: a pure function over `IReadOnlyList<MemorySearchResult>` +
  `LegAvailability` + config, in Core, with no SQLite and no I/O, is achievable exactly as specified.
- `Core/Search/` as a new top-level Core folder (not nested under `Core/Memory/`) is precedented:
  `DegradationPolicy` lives at `Core/Degradation/` and `RatingPolicy` at `Core/Rating/` — both
  top-level, both pure-policy folders, exactly the precedent WP6 6b cites. Verified via `find
  src/AiRaccoon.Core -maxdepth 1 -type d`. Not a screaming-architecture violation.

**Gap: `LegAvailability`'s own file is never named.** WP6's file table (6b) lists
`FusionConfidence.cs`, `FusionConfigKeys.cs`, `SqliteMemoryStore.cs`, three CLI files, one test file.
6a says only "thread a `LegAvailability` record — `FtsAvailable`, `VectorAvailable`, reason — to
`SqliteMemoryStore.cs:259`" with no file for the record type itself. Since `FusionConfidence.cs`
(Core, pure) takes `LegAvailability` as a parameter, the record **must** live in Core (Infrastructure
can't be a dependency of a Core file) — but the plan's own Context Map for WP6 never says so. This is
exactly the class of gap the architect gate exists to catch before implementation starts: an
implementer following the file table literally has no file to put `LegAvailability` in, and might
reach for the path of least resistance (define it next to `SqliteMemoryStore.cs` in Infrastructure,
which would then force `FusionConfidence.cs` to reference an Infrastructure type — a real layering
violation, not a hypothetical one). **Fix: add `src/AiRaccoon.Core/Search/LegAvailability.cs` (or fold
it into `FusionConfidence.cs` as a nested type) to WP6's file table before implementation starts.**

---

## 2. The flag — verified sound, one internal contradiction (major)

**Declaration.** `src/AiRaccoon.Core/Search/FusionConfigKeys.cs`'s proposed shape —
`ConfidenceEnabledGlobal`, `DefaultConfidenceEnabled = false`, `ParseConfidenceEnabled` — is a
byte-for-byte copy of the existing template. Verified against
`src/AiRaccoon.Core/Memory/QueryGuard/QueryGuardConfigKeys.cs:27-32`:
`StructuralEnabledGlobal = "queryGuard.structural.enabled.global"`, `DefaultStructuralEnabled = false`,
`ParseStructuralEnabled(string? value) => string.Equals(value, "true", OrdinalIgnoreCase)`. Same shape,
same naming convention (`<subsystem>.<feature>.enabled.global`). Correct.

**Read path and live-toggle behavior.** `SqliteMemoryStore` already has `ISettingsStore settings`
injected (`SqliteMemoryStore.cs:28,33`), so the flag reads through the same channel the store already
uses — no new dependency. The proven precedent for "does toggling take effect without a restart" is
`QueryGuardService` (`src/AiRaccoon.Core/Memory/QueryGuard/QueryGuardService.cs:28,36`): it takes
`ISettingsStore` and calls `GetSettingsByPrefixAsync` **fresh on every invocation**, no caching. If
WP6 follows the same pattern (settings resolved fresh at `SqliteMemoryStore.cs:259` on each search),
toggling `settings fusion enable/disable` takes effect on the very next search with no server restart
— consistent with every other settings-backed toggle in this codebase. The plan doesn't say this
explicitly but it falls out of following the established pattern; worth stating in the ADR so it's not
left to be discovered.

**Write path.** `settings fusion enable|disable|show` under `settings` is correct per ADR-0076 and
needs no `CliWriteOptOuts` exception — verified `src/AiRaccoon/Settings/CliWriteOptOuts.cs:14-15`
returns `commandPath is ["encryption", ..]` only, so every other settings write already routes through
the server via `LazyServerSettingsStore`. No new opt-out required, and the plan correctly does not add
one.

**Does it need to reach the MCP tool surface? No — and that is correct, not an oversight.**
`MemoryTools.cs`'s `memory_search` tool already exposes `rrfK`, `ftsWeight`, `vectorWeight` as
per-request overridable parameters (`MemoryTools.cs:120-125`). The fusion-confidence flag is
deliberately a bank-wide settings toggle, not a per-search parameter — and that's the right call for
WP6c's stated purpose (accumulating evidence from "real queries carrying real outcome signals" across
the whole traffic stream, not one caller's opt-in). Exposing it as a `memory_search` parameter instead
would fragment the very evidence WP6c is built to collect, and would add a knob to the 1:1
tool-to-backend mapping for a decision that belongs at the operator level. **No MCP-thin violation.**
This distinction (global evidence-gathering flag vs. per-request tuning knob) is sound but is implicit
in the plan — WP6 should say explicitly why this flag does *not* get an MCP parameter, mirroring how
rrfK/ftsWeight/vectorWeight *do*, so a future reader doesn't propose adding one.

**Contradiction: does 6a ship under a WP5 G1 outcome? (major, needs resolution before implementation.)**
The WP6 header states: *"Gated on WP5. If WP5 returns G1, this ships ADR-0077 alone, recording a
change measured and not shipped."* Read literally, **nothing but the ADR ships** under G1. But §6a's
own heading is *"Leg availability signal (ships regardless)"*, and its text argues it's "independently
valuable" regardless of the confidence heuristic's fate. These two statements are not reconcilable as
written: "ships ADR-0077 alone" and "ships regardless" cannot both be true of the same code under the
same outcome. This matters for PR shape too — PR4's contents (§4) are "WP6 — leg availability + fusion
flag + ADR-0077", stated as one unit; if 6a genuinely ships under G1 and 6b doesn't, PR4 needs to say
so, and the gates table (C1-C10, all of which exercise the heuristic) needs a G1-branch subset.
**Fix before implementation:** state explicitly which of {6a leg-availability signal, 6b heuristic
code (built but flag stays default-off), 6c telemetry} ship under each of G1/G2/G3, not just the ADR.
My reading of intent — 6a ships regardless because it's a genuine, independent defect fix
(`QueryFtsBatchAsync` swallowing `SqliteException` silently is a real observability gap on its own) —
is probably right, but the plan should say it, not leave it to be inferred from a heading parenthetical
that contradicts the section's own topic sentence.

---

## 3. `ai-raccoon doctor` as a 2am consumer surface — sound, one convention worth codifying

**The top-level-vs-settings question is correctly resolved**, verified against
`CliCommandTree.cs:8-11,76-79,80-95`: the `SettingsCommand()`'s own description string, read verbatim
from source, is *"Runtime configuration, one node per subsystem. Operations live at the top level:
'watch registered', 'extract prune', 'noise entries', 'model set', 'encryption', 'serve'."* — an exact
match to the plan's quote. `doctor` sets no settings key and mutates nothing, so by this rule (config
under `settings`, operation top-level) it is unambiguously an operation. Today's top-level operations
split into two flavors: ones that read a *data table* that's a byproduct of runtime activity (`watch
registered` reads `watches`, `noise entries` reads `noise_entries`, `extract prune` reads/deletes
`promotion_queue` rows) and one that mutates state and starts a background process (`model set`).
`doctor` introspects the *schema itself* rather than a data table — a third flavor the existing
top-level set doesn't yet have an example of, but the binary rule (configures nothing → top-level)
still applies cleanly. **Not a defect.** Minor suggestion: extend the `SettingsCommand()` XML doc
comment (`CliCommandTree.cs:76-79`) to name `doctor` in the enumerated operations list once it ships,
the same way the list was presumably updated for each prior addition — otherwise the hand-written
comment silently drifts from the tree the moment `doctor` lands, which is exactly the kind of
mirror-list rot the project's own "derive or delete the list" invariant warns about. (This one genuinely
can't be derived — it's prose — so the alternative is deleting the enumeration from the comment and
pointing at the source instead; either is fine, but note it as a small follow-up.)

**Exit codes: correct reuse, correct new code.** `ExitCode.cs` already follows one-code-per-reason with
codes retired rather than repurposed (`RestartFailed` (8) was split into 10-14 and retired outright per
its own comment, `ExitCode.cs:17-19`). Doctor's reuse of `FailedToResolveEncryptionKey` (1) and
`FailedToOpenEncryptedBank` (2) for key-resolution/open failure is the right call, not a violation of
the convention — those are the same *reason* recurring in a new verb, not a new reason wearing an old
code. Both codes already have multiple call sites (`AppRunner.cs`, `EncryptionCommands.cs`,
`NodeRunner.cs`), confirming this is the established reuse pattern, not a first-of-its-kind judgment
call. `BankSchemaMismatch = 19` is the correct next number (18 is the last one claimed,
`SettingsServerUnavailable`).

**Encrypted-bank / unresolvable-key distinguishability.** The plan's requirement — "a bank that cannot
be opened has an unknown shape, not a wrong one, and the output must use that word" — is the right
call and is gated (A6). This correctly separates "I can't tell you" from "it's wrong," which is the
single most important thing a 2am diagnostic tool must not conflate. Confirmed no existing mechanism
already does this that the plan is reinventing — `EncryptionCommands.cs` reuses the same two exit codes
for its own resolve/open failures with no schema-shape framing, so this is new, correctly-scoped work.

**Actionability.** A1-A3's gate names ("naming the table and column", "naming the index") are the right
bar for a 2am tool — a finding that says "mismatch" without naming the object is useless under
pressure, and the gates correctly forbid that. Confirmed no `--json` and no repair path, both correctly
justified and both correctly listed under §5 "would not build" rather than silently omitted.

---

## 4. `source_state` — correctly shaped, correctly *not* co-located with `memory_source`

Verified `memory_source`'s actual DDL (`MemorySchema.cs:303-312`):

```sql
CREATE TABLE IF NOT EXISTS memory_source (
    id            INTEGER PRIMARY KEY,
    source_type   TEXT NOT NULL CHECK(source_type IN ('file','transcript','manual')),
    source_locator TEXT NOT NULL,
    section       TEXT NULL,
    heading_path  TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source
    ON memory_source(source_type, source_locator, COALESCE(section, ''));
```

No `ctx`/project-scoping column at all, and keyed per-*section*, not per-file. The plan's claim that
`source_state` at a different key would be "how a marker starts lying" is well-founded: the operations
it gates — `FileIngestor`'s reconciliation and (formerly) `RecomputeChunkColumnsForContext` — both
partition by `(ContextKeyExpression, source_file)` (verified: `MemorySql.cs:550-553,564-565` both use
`PARTITION BY {ContextKeyExpression("")}, source_file`, and `ContextKeyExpression` at
`MemorySql.cs:577-586` builds the same derived `shared`/`project:<id>`/`workspace:<len>:<id>:<wid>`/
`custom:<len>:<id>:<label>` string `ContextKeyFor` parses back at `:593-611`). `source_state.ctx TEXT`
matches this derived-string convention exactly — it's a text column carrying the same key shape used
elsewhere, not a foreign key into a `contexts` table that doesn't exist. Correct choice, correctly
distinct granularity from `memory_source`.

Name: `source_state` reads as "state of a source" — plain, accurate, matches the invariant ("simplest
accurate word"). No objection.

One thing worth a sentence in WP2, not a blocker: `chunker_version` on `source_state` and the constant
`ChunkingDefaults.ChunkerVersion` it's compared against are two names for one number living in two
files. That's fine — it's a version-vs-stamp relationship, the normal shape for "is this row stale"
schemes — but the plan should say explicitly that bumping `ChunkerVersion` is the *only* way a source
becomes stale under this table, so a reviewer of WP7 (which bumps it) doesn't have to reconstruct that
from WP2's prose.

---

## 5. ADR-0077 / ADR-0078 — records decisions, not summaries; both index rows are specified

Checked the plan's ADR-0077 spec against the two model ADRs named as the standard (`0072`, `0058`).
`docs/adr/0072-*.md` has the shape: `# NNNN. Title` / `Date:` / `Status: Accepted`, a framing paragraph,
`## Context` (with a measured mechanism, not an assertion), `## What was measured` (numbers, per
option), `## Decision` (each rejected alternative **named with its own reason**, so — per the file's
own comment — "a reader who cannot see what lost does not propose it again"), `## Consequences`
(explicitly split into what's settled, what's not, and what would unblock it). ADR-0077's spec in WP6
asks for exactly this shape: `# 0077. Title`, `Date:`, `Status: Accepted`, `## Context`/`## Decision`/
`## Consequences`, citing 0006/0056/0058/0072 by number and quote, requiring the ADR to "state plainly
why shipping-behind-a-flag is honest where 0072 was right to ship nothing" (the alternatives-considered
content, in this repo's actual convention rather than a generic Nygard template), a named sample-size
number stated *before* data collection (closing off after-the-fact goalpost-moving), and a requirement
to record that "#367's stated root cause was wrong." This is a decision record with consequences, not
a change log — it holds up against the model.

ADR-0078's spec (WP7) explicitly requires "the arms that lost, with their numbers" — the same
ADR-0072/0048 discipline — plus the specific citation list (0048, 0069, 0070, 0036/0063, 0056/0058/0072,
#371) and the chunk-7 inspection. Also holds up.

**Both add their README index row, correctly, and correctly handle the conditional case.** Verified
`docs/adr/README.md`'s actual format (`| [0072 — Title](0072-slug.md) | summary |`, em dash) matches
what WP6/WP7 specify. Verified `AdrIndexTests.cs` enforces exactly what the plan claims:
`Index_ListsEveryAdrOnDisk` (every file needs an index row), `AdrNumbers_HaveNoUnrecordedGaps` (gaps in
the number sequence must be recorded under "Numbers never used" or the file must exist), and
`RecordedSkips_NameNoAdrThatExists` (a recorded skip becomes stale the moment the number is used).
WP7's fallback — "if WP4 cannot adjudicate... record 0078 under 'Numbers never used'" — is exactly the
mechanism this test suite expects; confirmed the "Numbers never used" table already exists in
`docs/adr/README.md` with one entry (0028) in the same format the plan would extend. No gap here.

---

## 6. The 4× bm25 `section` weight risk — handled architecturally, not left for research to discover

Verified the mechanism directly. `MemorySql.cs:101-110`: `bm25(entries_fts, 1.0, 8.0, 4.0)` weights
`source_file`/`section` matches 8×/4× a body match. `HeadingPathParser.Parse`
(`src/AiRaccoon.Core/Chunking/HeadingPathParser.cs:9-81`) only recognizes `#`/`##` lines **within the
markdown string it's handed** — it has no access to anything outside that string, no parent-chunk
context, nothing. A chunk that is a bare table row or cell (any axis-2 arm finer than "whole table")
will contain no `#` line and `Parse` returns `""`, forfeiting the entire 4× weight. This is exactly what
the plan says, verified line-for-line.

This is genuinely handled, not left as an unstated risk: WP4's grid explicitly carries axis 3 (section
heading prepended, on/off) as a modifier applied to every axis-2 arm finer than whole-table, with cell
D vs D′ specifically isolating its contribution, and F5 ("does the arm break FTS rank 1") functioning as
a second, independent check against exactly this failure mode. §5's "what looks over-engineered" section
also calls this out again as a named caution, not buried once and forgotten. This is one of the plan's
stronger pieces of design discipline — flagging it as correctly done, not as a finding.

---

## 7. Places the plan reinvents an existing mechanism

None found, checked deliberately:
- `LegAvailability` is not a rename of `DegradationPolicy` (`Core/Degradation/DegradationPolicy.cs`) —
  that policy governs per-entry TTL expiry, an unrelated domain concept (memory aging, not search-leg
  health). No overlap.
- `source_state` is not a duplicate of `memory_source` — different key, different concern, addressed
  in §4 above.
- The `metrics`-table-as-new-rows design for WP6c telemetry is correctly *not* a new table; verified
  `metrics` (`MemorySchema.cs:342-353`) already carries `name`/`value`/`tags`/`correlation_id`/
  `project_id` and its own comment states the rule the plan invokes verbatim: *"Every other deferred
  dimension stores as a row under a new `name`, not a new column."* WP6 follows this rule rather than
  reinventing a metrics table.
- The read-only schema-inspection connection pattern WP1 proposes (`Mode = SqliteOpenMode.ReadOnly`,
  `Password` set from the resolved key, `EnableExtensions()`, `LoadVector()`, no `EnsureAsync`) is not
  invented — it already exists as `OpenSnapshotReadOnly` in `AppRegistrations.cs` (verified around
  lines 93-126 as claimed, exact shape matches), used today by `SyncService`'s snapshot path. WP1
  correctly cites and reuses this rather than writing a second connection-opening helper.

---

## Still open

1. **WP6 file table must name a home for `LegAvailability`** (§1) — Core, most likely
   `src/AiRaccoon.Core/Search/LegAvailability.cs` or a nested type in `FusionConfidence.cs` — before an
   implementer is handed this package, or the natural path of least resistance produces a real
   Core→Infrastructure layering violation.
2. **Resolve the "ships ADR-0077 alone" vs. "6a ships regardless" contradiction** (§2) explicitly,
   stating per-outcome (G1/G2/G3) which of {6a, 6b, 6c} ship, and update PR4's stated contents to
   match. My best read is 6a ships under all three outcomes and only 6b/6c are gated on avoiding G1 —
   but the plan should say this, not leave it inferable from a contradiction.
3. Not verified in this lane, hand to whichever lane owns test/gate design: whether the WP6 Gates table
   (C1-C10) has a stated subset that still applies/still runs if 6a ships alone under G1 — right now
   C1-C10 all appear to assume the heuristic exists.
4. Not checked here: whether `FusionConfidence.cs`'s "config" input parameter (mentioned but untyped in
   the plan's file table) is meant to be the parsed bool from `FusionConfigKeys.ParseConfidenceEnabled`
   or a richer options object — small, but worth nailing down since it's the seam between the
   Infrastructure settings read and the Core pure function.
