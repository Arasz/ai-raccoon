# Architect review — combined code-corpus implementation plan (2026-08-21)

**Reviewer:** ARCHITECT lane (independent of the plan's four author lanes)
**Plan under review:** `docs/work/2026-08-21-code-search-implementation-plan.md` (rev 2, combined)
**Lane docs read:** `...-moe-{architecture,engineer,qa,ops}.md` (same dir)
**Prerequisites read:** `docs/work/2026-08-21-code-search-exploration.md` (merged #401) and
`docs/work/2026-08-21-arbitrary-embedding-models-plan.md` (embedding-model-support worktree, rev 2, G0-approved)
**Source verification:** all claims below were checked against the code in this worktree
(`src/…`) on 2026-08-21.

**Verdict overall:** APPROVE-WITH-CHANGES. The corpus schema, trigger family, watch
channeling, and search-surface decisions are architecturally sound and verified against
source. Three MUST-FIXes: (1) the code re-embed drain mechanism is contradictory across
lanes and the combined plan never resolved it in §7; (2) the QA test contract pins a
`memory` section key that contradicts the combined plan's own `results` key; (3) the
"byte-identical kind=memory" promise has no serialization mechanism behind it. Below:
16 findings — 3 MUST-FIX, 8 SHOULD-FIX, 5 INFO.

---

## MUST-FIX

### 1. The code re-embed drain mechanism is three contradictory designs; the combined plan asserts all of them (§3.3, §4-2, §7)

The plan's single biggest architecture gap: the drain path for a code-engine change is
described three incompatible ways and the §7 join dispositions never reconcile them.

- **Architecture lane D-D** (`moe-architecture.md` §D-D, lines 182–192): `model_migration`
  gains a `corpus` column via ladder v11; "The outbox transaction, lease, relay
  (`ModelMigrationJob`), and ADR-0076 ToolGate semantics are **reused unchanged**; the drain
  embeds code rows with the code engine". Combined plan §4-2 adopts this (v11b).
- **Engineer lane D-E9** (`moe-engineer.md` §2 D-E9, lines 51; §6.5, line 291): "Code
  re-embed has **no outbox, no lease, no ToolGate**"; `ConfigureCodeAsync` invalidates
  inline (`UPDATE code_entries SET embed_state='pending'` in the configure transaction) and
  the `code-reindex` maintenance job drains at 4×32 rows per 15 s poll (§7, line 302).
- **Ops lane §3.6** (lines 223–225): "code re-embed drains through the **same outbox/lease
  machinery per corpus** … HYPOTHESIS H4: the shared outbox can express a per-corpus drain
  without new machinery — **engineer lane verifies**". The engineer lane verified and
  concluded the *opposite* (D-E9). The combined plan repeats H4 in §3.3 as if still open.

Three concrete consequences the combined plan does not address:

1. **Single-row outbox capacity.** `model_migration` is a single-row table
   (`MemorySchema.cs:372-382` — `id INTEGER PRIMARY KEY CHECK (id = 1)`); `ModelMigrationJob.HasWorkAsync`
   answers `finished_at IS NULL` on that one row (`ModelMigrationJob.cs:27-29`). Two
   outstanding migrations (memory re-embed owed + code re-embed owed) cannot coexist; the
   plan never specifies the interaction of `model set code` while a memory migration is
   open (clobber / refuse / queue). "One outbox with a corpus discriminator" (arch D-D,
   line 191) silently assumes capacity the table does not have.
2. **ToolGate semantics.** "Relay reused unchanged" (arch D-D) contradicts ops §3.6's
   binding contract "code re-embed … never blocks memory tools": ADR-0076's relay path is
   exactly what closes the ToolGate during a memory re-embed (`CliCommandTree.cs:144-150`).
   A code drain through the same relay must either close the gate (violating the contract)
   or the relay's gate behavior becomes corpus-dependent — which is a change, not a reuse.
3. **Stale-vector window.** In the outbox design, `model set code` commits settings + outbox
   row first; invalidation happens at drain time. Between those, queries embed with the NEW
   code engine while `vec_code` still holds OLD-engine vectors → cosine garbage for an
   unbounded window. Engineer D-E9's configure-transaction invalidation closes this
   immediately (the `vec_code_pending` trigger empties the table at commit). The combined
   plan's "drain while vectors pending → kind=code degrades to FTS5-only" (§3.3) is only
   true if invalidation happens in the configure transaction — which the outbox design does
   not specify.
4. **Drain rate.** D-E9's only drain is the maintenance poll at 4×32 rows/run (`moe-engineer.md`
   §7, line 300) — at the spike's 56 texts/s that is ~8.5 texts/s effective; a 100k-chunk
   repo re-embed takes ~3 hours in FTS5-only degradation. The plan never states the drain
   rate or a fast path for fingerprint-change re-embeds.

**Fix:** Record the disposition in §7 and pick ONE design. Recommended: adopt engineer D-E9
as the v1 mechanism — configure-transaction invalidation (`embed_state='pending'` + triggers
clear `vec_code` at commit), `code-reindex` drains, no ToolGate interaction, memory never
blocked, `kind=code` naturally FTS5-only while `vec_code` is empty — and DROP ladder v11b
(the `corpus` column becomes unnecessary; the separate-code-migration-table rejection in §4-2
then applies to v11b itself). If the outbox is kept instead, the plan must specify: (a)
invalidation inside the configure transaction (kill the stale-window), (b) relay branching
by corpus with gate-OPEN semantics for code rows, (c) single-row outbox contention policy
for two outstanding migrations, (d) a drain rate/fast-path contract. Either way, delete the
"relay reused unchanged" claim and the still-open H4 phrasing.

### 2. QA test contract pins a `memory` section key; the combined plan and arch lane pin `results` (§3.6 vs QA WP6-T02/G2)

The combined plan declares the QA catalog the authoritative test contract (§5: "Canonical WP
numbering = QA lane's (test contract)"), but the wire shape contradicts it:

- Combined plan §3.6: `kind=both` → `{ results: [...memory...], code: [...] }` — key is
  **`results`** (also arch D-I, `moe-architecture.md` line 311: "key 'results', same position
  as today", mirroring the existing `SearchResultList` at `MemoryTools.cs:346`).
- QA lane WP6-T02 (`moe-qa.md` line 480): "`kind=code` returns code hits only; **`memory`**
  key present and empty"; §6 G2 (line 676): "Always both keys (`memory: [], code: []`)"; §4
  BDD scenarios inherit it. The exploration also used `{memory, code}` (`exploration.md` §Q3,
  line 166) — the arch lane's `results` refinement was never propagated to the test catalog.

A test written to a `memory` key fails against an implementation per §3.6, and vice versa.
Since the plan commits to `kind=memory` byte-identity, `results` is the only coherent name
(the existing key is `results` today) — the QA catalog must be amended, not the design.

**Fix:** Amend QA WP6-T02, G2, and the §4 BDD scenario wording to
`{ results: [...], code: [...] }` (empty arrays when a section is empty), and add a WP6 test
asserting the wire key names exactly, so the drift cannot recur.

### 3. "Optional Code section serialized only for kind=code|both" has no serialization mechanism; kind=memory byte-identity is unachievable as specified (§7-2, WP6-T01/RG-01)

Disposition 2 (§7) resolves: optional `Code` section, serialized only for `kind=code|both`,
`kind=memory` byte-identical. The repo has **no custom `JsonSerializerOptions`/converters**
on the MCP surface (grep of `src/` for `JsonConverter|JsonSerializerOptions` — only
unrelated hits: Node observability, encryption sidecar, `WatchState.cs`). With SDK default
serialization, `ApiEnvelope<CombinedSearchResultList>` whose `Code` property is an empty
list serializes `"code": []` unconditionally — `kind=memory` would NOT be byte-identical, and
WP6-T01/RG-01 (which pin "no `code` key") fail against the declared record shape. The arch
lane's OQ1 fallback ("omit `code` when empty (serializer condition)") was "adopted as the
primary" (combined §7-2) without naming the mechanism that makes omission possible.

**Fix:** Specify the mechanism in §3.6/§7-2 — either (a) a custom `JsonConverter<CombinedSearchResultList>`
(or a `[JsonIgnore(Condition = WhenWritingNull)]` nullable `IReadOnlyList<CodeSearchResult>? Code`
property — null omitted, non-null always emitted) with a converter/unit test pinning the
byte-identical `kind=memory` payload against a golden string, or (b) two distinct return
types with the tool switching on `kind` (note: a single MCP tool declares one return type, so
(a) is the honest option). Whichever is chosen, name it in the plan and add the golden-serialization
test to WP6-T01 so the compat promise is mechanism-backed, not aspiration.

---

## SHOULD-FIX

### 4. Sync strip: "deletes code_% tables" vs the actual strip mechanism — the gate as written cannot pass (§3.7)

`StripNonSyncableAsync` today deletes **rows** (workspace entries + all settings), then
VACUUMs (`SyncService.cs:424-440`); it never drops tables. The plan says "`StripNonSyncableAsync`
deletes `code_%` tables from every pushed snapshot (mirroring the settings strip)" with gate
"pushed snapshot contains no `code_%` table" (§3.7, line 161-164; ops §3.3 line 206). If the
code strip mirrors the settings strip (row deletion), the tables survive empty in the
snapshot and the gate fails as written; if the strip drops tables, the mirror-claim is wrong.
Also note `DELETE FROM code_entries` on the snapshot fires `code_fts_ad`/`vec_code_ad`
(needs vec0 loaded — the strip already opens with vec0 loaded, `SyncService.cs:427-429`).

**Fix:** Pick one and align: (a) keep row-deletion and change the gate to "pushed snapshot
contains zero `code_%` rows" (mirroring the settings gate), or (b) `DROP TABLE` the code
tables in the strip and keep the table-absence gate. State which in §3.7 and pin it in
WP7-T04.

### 5. Ops lane acceptance checklist contradicts the resolved reject-narrower decision (§7-4 vs ops §2.4/§7 item 6)

The combined plan resolves narrower-inside-broader as **reject** with `WatchOverlapException`
(§7-4, from QA G18 + engineer D-E5). The ops lane still specifies the opposite in two
places: §2.4 (lines 146–161): "Registering a watch inside an existing directory watch is a
**no-op** that reports the covering watch", `absorbedBy = <covering watch path>`; and the
owner acceptance checklist item 6 (line 296): "A second `memory_watch_add` of a subdirectory
→ `absorbedBy: <repo>`". An implementer or owner running the ops checklist will witness a
refusal where the checklist promises a no-op.

**Fix:** Rewrite ops §2.4 (rule statement, decision table, `WatchAddResult` semantics —
`absorbedBy` only meaningful for equal-path re-add) and checklist item 6 to the reject
semantics; keep the wire fields `pruned`/`absorbedBy` additive but document `absorbedBy`
never set on a rejected add (the refusal names the covering watch).

### 6. Ignore-vs-explicit `memory_ingest_file` conflict is unrecorded in §7 (arch D-F vs ops §2.3)

Architecture D-F (lines 246–248): "explicit single-file ingest (`memory_ingest_file` of an
ignored path → 0 chunks, same as an unindexable extension)" — ignore wins. Ops §2.3 (line
136): "`memory_ingest_file` on an explicitly-named file is **never** ignored (explicit beats
ignore)" — and even flags "Decision D6 … owner's call". The combined plan's owner
requirement 1 only says "also honored by `memory_ingest_directory`" — the single-file case
is silently dropped, and §7 has no disposition. (Adjacent OQ4 covers code routing for
`memory_ingest_file`, not the ignore semantics.)

**Fix:** Add a §7 disposition (or an explicit owner OQ): pick ignore-wins (arch, consistent
with "ignored files are never fingerprinted, never chunked" and with the digest path) or
explicit-beats-ignore (ops), and pin the chosen behavior in WP4-T16/T17 (the catalog's
"ignored file" tests currently cover the digest, not `memory_ingest_file`).

### 7. Chunker A/B arm: lanes disagree on the arm, and `expectedHash` anchoring breaks across chunker arms (§3.9 vs QA WP8-T03)

Combined §3.9: "chunker arm (heuristic vs **plain token-window**)". QA WP8-T03 (line 608):
"baseline (**whole-file single chunk** and/or MiniLM-on-the-same-chunks)". Two different
baselines. More fundamentally: the eval-set anchors relevance by `expectedHash`
(content-derived) with `expectedSource` fallback (`scoring.py:119-127`, verified), and the
plan says hashes are "stable across re-embeds and across the A/B" (arch D-L, line 398) —
true for the *model* A/B (same chunks), **false for the chunker arm**: different chunk
boundaries → different chunk texts → different hashes → the graded `expectedHash` no longer
exists in the token-window corpus, and the `expectedSource` (path) fallback collapses
relevance to file-level ("any chunk of the right file is a hit"), which cannot measure
chunk-boundary quality — the very question H2 asks. The existing harness has no
span-overlap relevance mode ("Graded queries authored against answer spans (line ranges)",
arch D-L line 402, but scoring stays binary hash/path).

**Fix:** Settle the arm shape in §3.9 (recommend: token-window arm, per QA's corpus-validate
gate requiring "anchors resolve"), and add a per-arm re-anchoring step: for each chunker
arm, regenerate the graded spans' hashes against that arm's chunks (span-overlap relevance:
a chunk is relevant iff its line range intersects the graded span) — a small scoring
extension the plan must name as in-scope for WP8, or explicitly reword H2 to "file-level
recall" and say why that is accepted.

### 8. v11a "reported, not silent" has no logging channel in the ladder (§4-1, ops §3.4)

The v11a prune promises "one Information log line per pruned watch … + pruned count". The
ladder has no logger: `MemorySchema` is `internal static` with **no `Log` class and no
`ILogger` anywhere** (verified — grep of `MemorySchema.cs` for `Log|ILogger|LoggerMessage`
returns nothing; every `MigrateToVnAsync` takes only `(connection, ct)`). The plan-review
lesson applies: "warn-and-continue needs a channel — schema layers are usually logger-less;
specify the mechanism". Unspecified, the implementer either invents a logger seam in a
static schema class or silently drops the report — the owner-visible "reported, not silent"
contract dies.

**Fix:** Specify the channel: have `MigrateToV11Async` return the pruned `(path, coveredBy)`
list and the count, and log at the one caller that owns a logger
(`SqliteConnectionFactory.InitializeAsync` or a new `MemorySchema` Log class taking
`ILogger`), or accept an `ILogger?` parameter on the migration. Add the log assertion to
WP1's v11 gate (RED: migration runs, no log line).

### 9. `idx_code_entries_path` is required by the engineer lane but absent from the arch DDL (§3.1/D-B vs engineer §11, E-table)

Engineer hand-off notes: "The engineer's store legs (WP-E4) depend on `idx_code_entries_path`
existing" (`moe-engineer.md` line 385) and the E-file table lists
"`MemorySchema.cs` (dep — arch lane): … **`idx_code_entries_path`**" (line 339). The arch
lane's D-B DDL (lines 93-96) declares only `uq_code_chunk`,
`idx_code_entries_project`, `idx_code_entries_hash`, `idx_code_entries_embed_state` — no
path index. The delete legs (`DELETE FROM code_entries WHERE project_id=@p AND (path=@path
OR path LIKE @prefix)`, engineer §5.1 leg 4, mirroring `MemorySql.DeleteBySourcePath:196-200`)
run per digest event; without `(project_id, path)` the prefix scan filters a project's whole
chunk set — memory has the same weakness, but code chunk counts per project are an order of
magnitude larger (chunks, not notes).

**Fix:** Add `CREATE INDEX IF NOT EXISTS idx_code_entries_path ON code_entries(project_id, path);`
to D-B's index block, or explicitly delete the engineer's dependency note. Also worth
pinning in WP4: `EXPLAIN QUERY PLAN` on the delete leg shows the index (cheap gate, no
behavior change).

### 10. Hidden-*directory* policy is unresolved, and repo-watch-by-default will index dependency trees (node_modules/bin/obj) with no built-in exclusion (§3.5, WP4-D vs reality)

Verified in source: the watch catch-up enumeration has **no hidden filter at all**
(`WatchCatchUp.EnumerateFiles`, `WatchCatchUp.cs:38-58`), and the directory-ingest walk
filters only per-file leading-dot names (`FileIngestor.IngestDirectoryAsync:51-53` +
`IsHidden:298-302` — `.git/config`'s *file name* "config" is not hidden). Hidden
*directories* are not skipped. Consequence: a repo-root watch (owner requirement 3, always
prune + whole-repo catch-up) enumerates and the code ingestor indexes `node_modules/**/*.js`,
`bin/obj/**/*.cs`, `target/**`, `__pycache__/**` etc. unless the user happens to author an
`ai-raccoon.ignore` — the plan's "initial index of a large repo is the only real wait"
(§8) understates this by orders of magnitude (56 texts/s spike). QA WP3-T03's
"skips hidden files and directories" is marked ⚠ HYPOTHESIS and does not match the
current policy.

**Fix:** Decide and document the hidden-directory policy for walks (extend `IsHidden` to
path segments during enumeration, or keep dot-file-only and say so), and either (a) ship a
small built-in deny set for repo-root watches (`node_modules`, `bin`, `obj`, `.git`,
`.venv`, `__pycache__`, `dist`, `build`, `target`) as a documented v1 default with the
ignore file as the extension surface — owner sign-off required — or (b) state in §8/risks
and the how-to that repo-watch indexes dependency trees and the ignore file is mandatory
onboarding. Then pin the chosen behavior in WP4-T29/WP3-T03.

### 11. CodeIngestor dedup: post-conflict re-read missing from the engineer's step 6 (arch D-B note 3 vs engineer §4.2)

Arch D-B note 3 (line 156-158) requires "`INSERT … ON CONFLICT DO NOTHING` with the same
bucket-shaped dedup read (`SelectChunkIdByPathAndHash…` pattern)" — the memory precedent's
whole point: after a DO NOTHING conflict the loser must **re-read by bucket key** before
using the id, or it embeds/updates a non-existent/stale row (`MemorySql.cs:8-10` comment;
`FileIngestor.cs:167-184` re-read after insert). Engineer §4.2 step 6 (lines 131-133) shows
the SELECT-miss → INSERT → embed path with **no re-read** after the insert; the embed then
needs the id (`EmbedIfConfiguredAsync(connection, id, …)`) that only the re-read can supply
correctly under a concurrent same-file ingest (`memory_ingest_directory` racing a digest).

**Fix:** Add the re-read step to engineer §4.2 step 6 exactly as `FileIngestor.cs:167-184`
does (SELECT by `(project_id, path, hash)` after the DO NOTHING insert; skip the row if
still null), and extend WP3-T07's RED to cover a concurrent double-ingest (two writers, one
wins, loser re-reads and refreshes positions via D-E11).

---

## INFO

### 12. Feature-file path/name mismatch between lanes (QA WP8-T04 vs ops §6)

QA: `docs/work/features-native-memory/code-corpus.feature` + `BDD/CodeCorpusSteps.cs`
(`moe-qa.md` line 617). Ops: `docs/work/features-code-search/code-search.feature` +
`spec.json` (`moe-ops.md` line 280). Combined plan WP8 says "`code-corpus.feature`". Pick
ops's convention (`features-<name>/<name>.feature` + `spec.json`, per the agent-memory
precedent) and align QA + WP8. No behavior impact; purely a contract pointer.

### 13. Tool-count gate "28" must also update the stale feature-file assertion

`docs/reference/agent-memory-server.md:19` says "Tools (27)" ✓ (grep: 27 `McpServerTool`
attributes in `src/AiRaccoon/Tools/`), so 27→28 is right. But the feature file still
asserts a live tool listing with an older count ("All 17 tools are still listed",
`docs/work/features-agent-memory/agent-memory.feature:12-14` — ops §1.3 line 48); the
plan's "tool-count gate 28" (WP8) must name updating that feature file in the same PR or
the gate stays green against a stale assertion.

### 14. Prune-gap event drops are recovered by the broad watch's full catch-up — eventual consistency holds (engineer §5.5)

Verified: `FindContainingWatch` is runtime-only (`WatchPipeline.cs:262-280`) and events with
no containing watch are dropped (`WatchPipeline.cs:193-197`); after
`RemoveWatchAsync`/`UnregisterWatch` and before the new watch registers, events under the
pruned path vanish. Harmless because the new watch is born with `lastChangeTs = 0` (full
scan, `WatchService.cs:34-37`) and re-fingerprints everything; crash between prune and
register is repaired by the next `AddAsync` re-running the flow. The plan's "transient
overlap harmless" claim checks out — worth one sentence in the ADR so future readers don't
re-litigate it. Note `watch_files` has no FK to `watches` (`MemorySchema.cs:259-265`), so
an in-flight digest's fingerprint upsert after a prune recreates a *shared* `(project_id,
path)` row with no parent — harmless and idempotent with the broad watch's catch-up; QA
WP4-T26's "no-resurrect" should be scoped to the registration row + runtime state (which
`UnregisterWatch` + the `_runtime.ContainsKey` guards at `WatchPipeline.cs:237,254` already
enforce).

### 15. Engine generalization is assumed-implemented but still in flight — keep the dependency explicit

The combined plan treats the engine plan as "assumed fully implemented" (repeated in §1),
but the `embedding-model-support` worktree is at the plan commit and the engine's own
`embedding-wp1`/`embedding-wp3` worktrees exist — WP1–WP4 are not merged. Engineer A4
flags this ASSUMED. Fine for a plan, but WP5/E5 (code engine resolution, manifest-driven
generator, D3-reconcile parameterization) must not start until the engine WP3 lands; the
combined plan's WP ordering should say so explicitly (currently only the engineer lane's
"Cross-cutting" note does). Also: `EmbeddingService.EngineFingerprint` today is
`local:<path>` (`EmbeddingService.cs:101-109`) — the D7 manifest-hash fingerprint is part of
the assumed engine work; the code plan's "fingerprint change → re-embed" semantics stand or
fall with it.

### 16. Minor citation drift in the engineer lane

`MaintenanceJobs.cs:16-56` is cited for `PendingEmbedJob` (engineer §1.5) but that range
holds `VacuumJob`; `PendingEmbedJob` lives later in the file. The pattern claims all verify;
only the anchor is off. No action needed beyond re-anchoring at implementation time.

---

## Verified-correct claims (checked and cleared)

- Digest gate: `Ddl` runs only when the stored digest differs (`MemorySchema.cs:453-460`),
  so additive code DDL reaches legacy banks with no version bump — the metrics precedent
  (`MemorySchema.cs:335-353`) is real; ADR-0023/ADR-0075 reading correct.
- `vec_code` ctx = project_id with single-partition equality (`WHERE v.ctx = @ctx AND k =
  @limit`) matches the verified vec0 query shape (`MemorySql.cs:141-149`) and ADR-0068.
- `uq_code_chunk UNIQUE(project_id, path, hash)` in the unconditional Ddl is safe (new table
  → no legacy violating rows; the memory bucket indexes needed the ladder only because
  legacy banks had duplicates — `MemorySchema.cs:19-36`). Dedup predicate matches the index
  exactly.
- Trigger family mirrors `entries_fts_ai/ad/au` (`MemorySchema.cs:166-181`) and
  `vec_entries_au/pending/ad` (`MemorySchema.cs:186-201`); the D-E11 position-refresh UPDATE
  touches only `line_start/line_end/chunk_index/total_chunks/updated_at`, so no FTS/vec
  trigger churn — correct by construction.
- Watch channeling seam (delete-both + re-ingest-both in one `ReplaceCoreAsync` transaction;
  digest's existing fingerprint/hash-skip semantics) matches `WatchDigestExecutor.cs:20-61`
  and `MemorySql.cs:196-200,239-243`; each ingestor self-filtering is the right shape.
- Containment: `IngestPath.IsWithinScope` (`IngestPath.cs:48-60`) is separator-aware and
  symlink-resolving — `/repo2` ⊄ `/repo` holds; digest ownership already uses it
  (`WatchPipeline.cs:273`).
- Sync pull-side exclusion is by construction (merge names only
  `entries`/`sync_tombstones`/`memory_source`, `SyncService.cs:261-343`); the push-side
  strip correction (§3.7) is a genuine lane-found bug fix.
- Sweep/promotion/TTL cannot reach code rows: sweep operates through `IMemoryStore`
  (`SweepService.cs:16-19`), no TTL column, table separation.
- Search surface: per-section `minRelativeScore`/`limit`, no cross-corpus fusion, `kind`
  fail-fast mirroring the `scope` validation (`MemoryTools.cs:146-152`), `code_get` scoped
  by projectId (hash is not globally unique — the tool signature gets this right),
  QueryGuard/QueryLengthGuard parity — all coherent and MCP-thin.
- Eval: harness reuse (`evaluate.py`/`scoring.py` binary relevance verified), floor pinning
  + RED witness matches the ADR-0079/0081 precedent, "never flip defaults" matches engine
  plan G0; jina parity probe before the arm is required and specified.
- Tool count 27 → 28 ✓; ADR numbering next-free 0084 ✓; `WatchAddResult` additive fields
  (`WatchTools.cs:64`) ✓.

---

*Reviewer: architect lane (independent). No code changes were made; this file is the only
output.*
