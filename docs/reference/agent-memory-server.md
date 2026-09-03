# Agent memory server — reference

The ai-raccoon MCP server's complete agent-facing contract: tools, prompts,
environment variables, contexts, and error shapes. Consult this mid-task when
integrating or debugging; see `docs/work/features-agent-memory/spec-issue-1.md` for the
design rationale and `docs/work/features-native-memory/spec.json` for the native-store
scope.

The server runs a single SQLite bank (`memory.db`) with a native .NET store:
no sqlite-memory/sqlite-vector/sqlite-sync extensions, no download-on-first-run
provisioning, and no `raccoon_meta.db`. All tables — entries, workspaces, settings,
watches, watch_files, FTS5, vec0, sync_meta, and sync_tombstones — live in
`memory.db` (FR-NM-1).

**Fresh-start note (P11):** this release drops existing-bank migration — the bank
starts clean with the new native schema. A re-hash + re-embed migration path is
deferred to a deployment that needs it (D11).

## Tools (29)

Every tool takes `projectId` (camelCase — all parameters are camelCase), and it is
**optional on every tool**: an omitted or blank id defaults to the registered project
whose ingest-scope or watch surface contains the server process's working directory —
one distinct project resolves (guid spellings canonicalize at the gate), several refuse
as ambiguous with the sorted candidate list, none refuses with `projectId is required`
naming the probed directory. An explicit id always wins and never consults the resolver.
The exceptions: `memory_promotion_list`, whose omitted id means all-projects (its
cross-project feature) and never cwd-defaults, and `project_id_token_get`, which mints
one and so takes none. Writes land in `project:<id>` by default; naming a `workspaceId`
routes them into that workspace's isolated context.

10 memory tools (including `memory_get`, ADR-0035), 4 workspace tools, 3 watch tools,
2 promotion tools, 2 share tools, 2 sweep tools (`memory_sweep`, `memory_set_ttl`),
2 search-feedback tools (`memory_record_followthrough`, `memory_record_grade`),
1 sync tool, 1 performance tool (`memory_performance`), 1 code tool (`code_get`),
1 project tool (`project_id_token_get`, ADR-0089 — mints and registers a new project id).
`memory_configure` and `memory_set_structure_alpha` were removed by the CLI-config
refactor: configuration is no longer an MCP tool — the CLI verbs are the single
config channel (see [Command-line options](#command-line-options)).

| Tool                           | Parameters                                                                                                                                                  | Returns                                                                                            |
|--------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| `memory_write`                 | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?`, `sourceFile?`, `section?`                                                                   | `{hash, path, context, createdAt}`                                                                 |
| `memory_get`                   | `projectId`, `hash`                                                                                                                                         | `{hash, value, path, context, createdAt}`                                                          |
| `memory_search`                | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minRelativeScore=0`, `rrfK=60`, `ftsWeight=1`, `vectorWeight=1`, `contextLabel?`, `kind=memory\|code\|both` (default `both`) | `{results:[{hash, ranking, path, snippet, sourceFile?, chunkIndex, totalChunks}], code?:[{hash, ranking, path, snippet, lineStart, lineEnd}], warning?}` |
| `memory_record_followthrough`  | `projectId`, `correlationId`, `filePath`                                                                                                                    | `{recorded: true}`                                                                                 |
| `memory_record_grade`          | `projectId`, `correlationId`, `grade`, `note?`                                                                                                              | `{recorded: true}`                                                                                 |
| `memory_list`                  | `projectId`                                                                                                                                                 | `{files: <json tree>}`                                                                             |
| `memory_stats`                 | `projectId`                                                                                                                                                 | `{entries, pending, contexts}`                                                                     |
| `memory_share`                 | `projectId`, `hash`                                                                                                                                         | `{shared: true, context: "shared"}`                                                                |
| `memory_share_extract`         | `projectIds[]`, `mode=propose\|promote`, `limit=20`, `includeTtlRows=false`, `autoPromote=false`, `confirm=false`                                            | `{candidates: [...], promotedHashes: [...], absorbed, skippedDuplicates, failures: [...]}`         |
| `memory_delete`                | `projectId`, `hash`                                                                                                                                         | `{deleted: 0\|1}`                                                                                  |
| `memory_delete_context`        | `projectId`, `context`                                                                                                                                      | `{deleted: n}`                                                                                     |
| `memory_ingest_file`           | `projectId`, `path`, `context?`                                                                                                                             | `{indexed: 0\|1}`                                                                                  |
| `memory_ingest_directory`      | `projectId`, `path`, `context?`                                                                                                                             | `{scanned: n}`                                                                                     |
| `memory_embed_pending`         | `projectId`, `limit?`                                                                                                                                       | `{processed, pending}`                                                                             |
| `memory_watch_add`             | `projectId`, `path`                                                                                                                                         | `{projectId, path}`                                                                                |
| `memory_watch_status`          | `projectId`                                                                                                                                                 | `{watches: [{projectId, path, state, lastError?, lastSync?}]}`                                     |
| `memory_watch_remove`          | `projectId`, `path`                                                                                                                                         | `{projectId, path}`                                                                                |
| `memory_workspace_begin`       | `projectId`, `agentId?`, `name?`                                                                                                                            | `{workspaceId, context}`                                                                           |
| `memory_workspace_status`      | `projectId`, `workspaceId`                                                                                                                                  | `{entries, count, agentId, name}`                                                                  |
| `memory_workspace_consolidate` | `projectId`, `workspaceId`, `keep`                                                                                                                          | `{promoted, discarded}`                                                                            |
| `memory_workspace_discard`     | `projectId`, `workspaceId`                                                                                                                                  | `{discarded}`                                                                                      |
| `memory_sweep`                 | `projectId`, `dryRun=true`                                                                                                                                  | `{candidates, deleted}`                                                                            |
| `memory_set_ttl`               | `projectId`, `hash`, `ttlDays?`                                                                                                                             | `{hash, ttlDays, rating, threshold, canEverExpire}`                                                |
| `memory_sync`                  | `projectId`                                                                                                                                                 | `{sent, received, reindexed}`                                                                      |
| `memory_promotion_list`        | `projectId?`, `limit=50`, `includeFullValue=false`, `allProjects=false`                                                                                                             | `{rows: [PromotionQueueRow]}`                                                                       |
| `memory_promotion_discard`     | `projectId`, `hash?`                                                                                                                                        | `{discarded: n}`                                                                                   |
| `memory_performance`           | `projectId`, `windowMinutes?=180`, `bucketMinutes?=1`                                                                                                       | `{generatedAt, window, bucket, bucketCount, series: [{tool, count, p50, p95, p99, min, max, buckets: [{start, count, average}]}]}` |
| `code_get`                     | `projectId`, `hash`                                                                                                                                         | `{hash, value, path, lineStart, lineEnd}`                                                          |
| `project_id_token_get`         | `name?`                                                                                                                                                     | `{projectId, instructions}`                                                                        |

### Notes on the less obvious tools

- **`scope` values:** `scope=all` (default) searches `shared` + `project:<id>` (+ workspace
  when named); `scope=project` searches `project:<id>` only; `scope=shared` searches the
  `shared` promotion tier only. Workspace scratch is never included in `scope=all` — it is
  only visible to a search that names that `workspaceId`.
- **`memory_search` `kind` values:** `kind=both` (default since 1.34.0) runs both hybrids
  independently and returns both sections (no cross-corpus fusion — each section is ranked by
  its own FTS5+vec0 hybrid). `kind=memory` is the pre-1.34 default behavior, unchanged — no
  `code` key in the response at all. `kind=code` searches the code corpus only (`results`
  is present but empty). Code is always
  project-scoped: `scope=shared` with `kind=code`/`both` returns an empty `code` section.
  `codeLimit`/`codeMinRelativeScore` override `limit`/`minRelativeScore` for the code section
  only (omit them to use the same values as the memory section) — the only per-section knobs.
  Every other per-call tuning arg (`rrfK`/`ftsWeight`/`vectorWeight`/`candidateWindow`) applies
  to the code section too, at the same value passed for memory's (ADR-0088 decision 5; no
  separate `codeRrfK`/etc. namespace). Code search degrades by configuration state: with no
  `embedding.codeModel` configured, it is FTS5-only and carries a `warning`
  (`"code engine not configured — FTS5-only results"`); once a code engine is configured
  (`model code set local`, below) it runs the full vec0 hybrid, fused with the same weighted RRF
  as memory (`retrieval.rrfK`/`ftsWeight`/`vectorWeight`, or the per-call args above —
  `retrieval.structureAlpha` is read but never applied, since code has no structure modality); a
  query over the engine's
  510-token window is trimmed before embedding and carries a different `warning` naming that; a
  configured-but-**unloadable** engine (missing files, a dimension mismatch) refuses the search
  with `code-engine-unloadable` instead of degrading (see [Error shapes](#error-shapes)) — memory
  searches are unaffected, since the two engines are independent settings rows. A code hit's
  `relativeScore` is hybrid-score-relative like memory's (the top hit is always 1.0, others
  proportional to their fused score) — not the positional, rank-derived placeholder of an earlier
  wave. Code hits carry `lineStart`/`lineEnd` (1-based) instead of `chunkIndex`/`totalChunks`;
  read the full chunk with `code_get`. Every `memory_search` writes a `search_quality` row
  (ADR-0094): `kind=memory` records the memory hit count and files as always, `kind=both`
  records the memory leg the same way, and `kind=code` records the code hit count with an empty
  file list. Code paths never enter the table, since its rows travel in the sync snapshot and
  the code corpus never leaves the machine. `meta.correlationId` is present on all three kinds
  (the id `memory_record_grade`/`memory_record_followthrough` key off), since every search now
  has a row behind it.
- **`memory_ingest_file`/`memory_ingest_directory` feed the code corpus too:** a file is routed
  by extension — the memory-owned extensions (`.md`/`.markdown`/`.txt`/`.json`) always win on
  overlap; a recognized code extension (`.cs`, `.py`, `.ts`, `.go`, `.rs`, … — the v1 list is
  owner-adjustable) goes to the code corpus instead; anything else is skipped in both. A `.md`
  file inside a directory of otherwise-code files still routes to memory. `ai-raccoon.ignore`
  and the hidden-file/deny-set (`node_modules`, `bin`, `obj`, `.git`, `.venv`, `__pycache__`,
  `dist`, `build`, `target`) rules apply identically to both corpora. Code files are chunked
  (`CodeChunker`, line-range splitting) and stored on every ingest regardless of engine
  configuration; each row lands `embed_state = 'pending'` until a code embedding engine is
  configured (`model code set local`, below) — until then the rows are FTS5-searchable only
  (see `kind=code` above). Memory ingest is unaffected.
- **`memory_share`:** promotes the entry whose `hash` you pass (from a `memory_write`
  or `memory_search` result) into `shared`. It is additive — the source project row
  stays. There is no un-share; `memory_delete` on the shared row's hash removes it from
  `shared`.
- **`memory_promotion_list` / `memory_promotion_discard`:** the propose tier
  (ADR-0007) — `memory_share_extract` in `mode=propose` fills a persisted
  per-project queue (`promotion_queue`) ranked by score; `memory_promotion_list`
  reads it (omitting `projectId` requires `allProjects=true` — explicit consent
  to see every project's queue); `memory_promotion_discard`
  drops one row (`hash`) or, with `hash` omitted, the whole project's queue.
  **A discard is permanent** (ADR-0026): the rejected hash is recorded in
  `promotion_discards`, and propose will never re-queue it — not on the next pass, not
  after a watch re-ingest, not after a mode flip to promote (a discarded row is pruned
  before it could be promoted). Only the tool path writes discards: promote claims,
  capacity evictions and scorer-version clears are never recorded as rejections. There
  is no un-discard; changed content produces a new hash and is re-eligible. The propose
  upsert also refuses rows whose exact value is already in the shared tier, so the
  queue never holds shared content.
  `memory_share_extract` in `mode=promote` drains the top queued candidates into
  `shared`. Operators who never review the queue can set `settings extract
  auto-promote-threshold` to a score (0..4, e.g. 3.5; `off` by default). On a background
  pass in `promote` mode, the loop ranks fresh candidates, queues only rows at or above
  the threshold, and shares them in the same pass. Rows below it never enter the queue,
  so no backlog piles up waiting for a review that never happens. Propose mode ignores
  the threshold and keeps queuing the full ranked set, and the manual MCP promote path
  stays unfiltered: an explicit promote call shares what it names. Rows queued before
  the threshold was set stay queued; clear them once with `memory_promotion_discard`
  after enabling. Every response carries `waitingPromotionsCount`/`promotionsWaitTimeSeconds`
  in `meta`, scoped to the project the call named; once that project holds queued rows,
  `meta.capacity` also carries its `reserved`/`used`/`borrowing` share of the cap
  (ADR-0007's fair-share promise, made observable) — see [`capacity`
  semantics](#capacity-semantics) below for what `reserved` and `borrowing`
  actually mean. The two tools that do not name a single project — `memory_promotion_list`
  with `projectId` omitted, and `memory_share_extract` over several ids — report a
  bank-wide count with `capacity` absent. No response names another project.
- **`memory_share_extract(mode=promote)` result shape:** `candidates` is always `[]` in promote
  mode (it is only populated by `propose`). `promotedHashes` are the hashes whose share actually
  CREATED a shared row — never a claim for a row that already existed. Every promoted chunk gets
  its own shared row under a value-addressed path (`shared/<sha256(value)>.md`): one file may
  hold many shared rows. Identical chunk content from different sources (e.g. the same section
  mirrored in two repos) dedupes to one row by construction — the shared bucket key is
  (path, hash), and the value hash makes the path identical for identical values. `absorbed`
  counts queued chunks that were claimed but whose identical value was already shared (idempotent
  re-share, or an insert race lost to a concurrent caller) — they are dropped from the queue, not
  reported promoted. `skippedDuplicates` counts queued candidates whose value
  (whitespace-normalized) already exists in `shared` — one copy per value, even across different
  paths — dropped without an error. Invariant per call:
  claimed = `promotedHashes.length + absorbed + skippedDuplicates + failures.length`; `absorbed`
  is `0` in propose mode (same result record). `failures` is a list of `{projectId, hash, reason}`
  for candidates claimed off the queue but never shared, where `reason` is a bounded token —
  `stale-hash` (the queued hash no longer resolves in the entries table) or `share-failed` (any
  other per-candidate error) — see `PromoteFailure`/`ShareExtractResult`
  (`src/AiRaccoon.Core/Memory/PromotionQueue.cs`, `SharedExtraction.cs`). This exists so a caller
  can tell "everything queued was already shared" (`skippedDuplicates` > 0, `failures` empty) apart
  from "everything failed" (`failures` covers the whole batch), and can see partial success instead
  of a single pass/fail verdict for the batch.
- **Embedding engine (CLI, not a tool):** `ai-raccoon model embedding set local [path]` selects
  the bundled int8 ONNX all-MiniLM-L6-v2 (in-process, ~23 MB, Apache-2.0, SHA-256
  pinned); `ai-raccoon model embedding set openai {model-id} [base-url] [--api-key <key>]`
  selects any OpenAI-compatible `baseUrl` (default `https://api.openai.com/v1`).
  `model` is the model id for openai or a custom ONNX path for local; it defaults to
  the bundled model for local, is required for openai. A local **directory** must contain
  `ai-raccoon.manifest.json` describing its dimensions, tokenizer, pooling and files, and
  is refused without one; only a `.onnx` file path keeps the pre-manifest defaults.
  `--dims <n>` declares a remote engine's output dimension (required when it is not 384 —
  sqlite-vec infers none); `model embedding set openai` probes the endpoint first and refuses a
  contradicted or undeclared non-384 dimension before the outbox commits. The API key is
  persisted in the settings table. Changing the engine re-embeds the bank, rebuilding the
  vector index first when the dimension differs. The `engine` field in the
  result is the stable fingerprint (`local:bundled`, `openai:text-embedding-3-small@<baseUrl>`,
  etc.) — a change triggers the re-embed.
- **Downloading a local model (CLI, not a tool):** `ai-raccoon model download {repo-id}`
  resolves a Hugging Face repo, downloads the ONNX model + its external data + tokenizer with
  SHA-256 pins captured from the LFS oids BEFORE download (verify-or-delete, no half-installed
  model), runs an ONNX Runtime opset smoke test, and writes `ai-raccoon.manifest.json` into
  `<data-root>/models/<slug>/` (e.g. `BAAI__bge-m3`). Flags: `--revision`, `--file` (repeatable),
  `--dir`, `--dry-run` (resolve + print sizes/oids, download nothing), `--yes` (confirm
  downloads > 500 MB). It never activates the model — `model embedding set local <dir>` is the explicit
  next step (plan `docs/work/2026-08-21-arbitrary-embedding-models-plan.md`, D4/D8).
- **Code corpus embedding engine (CLI, not a tool):** `ai-raccoon model code set local <dir>`
  activates a manifest directory for the code corpus — independent of the memory engine above,
  its own `embedding.codeModel`/`embedding.codeEngine`/`embedding.codeDimensions` settings rows.
  `<dir>` must contain `ai-raccoon.manifest.json`; its declared dimension is accepted as-is and
  `vec_code` is reconciled to it in the same transaction (like the memory bank's D3 reconcile —
  fresh banks start at `float[768]`, the default-model dimension). A missing/invalid manifest is
  refused with the loader's own error. On success the write commits in one transaction with
  invalidating every already-embedded code row back to `pending` — `vec_code` empties at that
  same commit, no stale-vector window — and the `code-reindex` maintenance job signals the embed
  topic's single consumer (`EmbedDrainService`, ADR-0091) whenever it finds pending rows, on its
  own on-demand
>>>>>>> origin/main
  cadence, rather than re-embedding inline itself; there is no outbox, no relay wait, and memory
  tools are never blocked.
  `ai-raccoon model code set default` downloads `faxenoff/code-daemon-embed-v1` (187 MB, if not
  already present) and activates it in one command — the recommended path
  (`CodeEngineSetup.DefaultModelCommand`, the exact string the search warning, `doctor`, and the
  `memory_search` tool description all quote). For a non-default model, the manual two-step still
  applies: `ai-raccoon model download {repo-id}` then `ai-raccoon model code
  set local <dir>`. `ai-raccoon settings model
  show` includes the `codeModel`/`codeEngine` rows when set; `ai-raccoon settings model reset`
  never touches them; `ai-raccoon settings model code reset` deletes only them, leaving the
  memory engine untouched (`docs/work/2026-08-21-code-search-implementation-plan.md` §3.3).
- **Structure alpha (CLI, not a tool):** `ai-raccoon settings retrieval alpha set {0..1}`
  writes the dual-vector fusion alpha (`retrieval.structureAlpha`, 0..1; default 0.5)
  used by search as `score = alpha × content + (1 − alpha) × heading-path structure`.
  Applies to subsequent searches, no re-embedding.
- **`memory_search`:** hybrid fusion from two modalities: FTS5 (keyword) and vec0
  (semantic, when an embedding engine is configured). The two ranked lists are fused
  with Reciprocal Rank Fusion (RRF): each result's score = Σ weight / (k + rank) per
  modality, then normalized so the top result is 1.0 (range 0..1). `rrfK=60` (default),
  `ftsWeight=1`, `vectorWeight=1` (default 1:1). When no engine is configured, search
  degrades to FTS5-only — never crashes. The FTS5 MATCH expression is constructed per
  query (plan C Wave 1): stopwords are stripped and the remaining content tokens joined
  with AND when there are ≤4 (precision), with an OR fallback — all query tokens plus
  quoted adjacent-token bigram phrases — whenever the AND under-matches (zero rows,
  fewer rows than terms, or fewer than the requested limit); longer queries keep the
  plain OR join of all tokens. Punctuation never reaches the FTS5 grammar.
- **`memory_workspace_consolidate`:** `keep` is an array of hashes to promote, or
  `["all"]` to promote every entry in the workspace. It then deletes the workspace
  context entirely — entries not kept are gone.
- **Workspace lifecycle record:** `memory_workspace_begin` inserts an `Active` row into
  the `workspaces` table inside `memory.db` (no separate meta DB), carrying the `agentId`
  and `name` it was given as provenance; consolidate marks it `Closed` and discard marks
  it `Discarded`, both with `closed_at`, so the record says which trigger ended it. A
  workspace begun but never finished stays traceable after a crash.
- **`memory_sweep`:** `dryRun=true` (default) only lists candidates; pass `dryRun=false`
  to delete. An entry is a candidate only when it carries a per-entry TTL, its rating is
  below the sweep threshold (default 0.3) *and* its age exceeds that TTL. `shared` entries
  are never swept, and the delete stays inside the `project` scope it enumerated — a
  same-hash row in an active workspace or a custom context is not collateral.
  Counter-intuitive but worth knowing: the rating is only recomputed when a search returns
  the entry, so an entry nothing ever searches keeps its starting 0.5 and never becomes a
  candidate, while searching an entry older than ~26 days is what drops it under the
  threshold. `rating` does not affect search ranking.
- **Background reaper:** the same sweep also runs unattended on HTTP/S hosts, and it is
  **ON by default** — every 24 h it sweeps each project and *deletes* (never a dry run).
  It is the one background service that destroys data, so it has a kill switch:
  `ai-raccoon settings sweep disable`. `ai-raccoon settings sweep show` reports the whole policy —
  `enabled: True  interval: 24 h  threshold: 0.3` — and `settings sweep interval-hours {1..8760}`
  retunes the cadence live, no server restart needed. The kill switch fails safe: any
  casing of `false` in `sweep.enabled.global` disarms it, and only an explicit `false`
  does — an absent or unreadable row leaves the reaper armed. Nothing without a per-entry
  TTL is ever a candidate, so a bank that has never called `memory_set_ttl` has nothing
  to lose. It honours the same per-project access mode `memory_sweep` enforces at the
  tool boundary: a project not in `full` mode is skipped, not reaped, even while the
  reaper is armed and the pass is running against every other project (see
  [ADR-0025](../adr/0025-the-sweep-reaper.md)).
- **`memory_set_ttl`:** the only way to give an entry a TTL — without one it can never be
  swept. `ttlDays` is 1..36500, or `null` to clear it; `0` is rejected. A TTL is necessary
  but not sufficient: fresh entries start at rating 0.5 against a 0.3 threshold, so
  `ttlDays=7` does not expire an entry at age 30. The returned `canEverExpire` reports
  whether the rating gate is already met. An unknown hash — or one owned by another
  project — is refused as `unknown-hash`. Side effect: an entry carrying a TTL leaves
  `memory_share_extract`'s candidate set unless that call passes `includeTtlRows`.
- **File watching:** watching is enabled per project (or `*`) with
  `ai-raccoon settings watch enable|disable {project-id|*} {true|false}`, restricted to a scope
  allowlist (`settings ingest scope add|remove|list`) and a concurrency cap (`settings watch
  concurrency {project-id|*} {1..16}`, default 4) — all CLI-only, except `watch registered`,
  which stays top-level. Quote the `*` wildcard in the
  shell (`'*'`); an unquoted `*` expands into the current directory's files and the CLI
  reports each as an unrecognized argument. The `settings watch` family CONFIGURES watching —
  registrations are created by agents via `memory_watch_add`; `settings watch list` prints the
  config per target in block format (`target: <id>  enabled: ..  concurrency: ..  scope:`,
  one path per line, `(none)` when empty — `enabled: true` means watching is enabled for
  that target, not that a watch is registered), `watch registered [{project-id}]` lists
  the persisted registrations (project, path, registered, lastChange; live state stays on
  `memory_watch_status`), and `settings watch remove {project-id|*}` deletes a target's config rows
  (`'*'` clears only the global config; a file-name ghost row — written by an unquoted `*` —
  is removed individually, e.g. `settings watch remove CLAUDE.md`). `memory_watch_add` registers a
  file or directory and returns immediately (the initial scan runs in the background —
  status reports `scanning`); an exact re-add of an already-watched path is a no-op
  (`absorbedBy` in the result names it). **No overlapping watches**
  (docs/work/2026-08-21-code-search-implementation-plan.md §2.2/§5.5): a path already covered
  by an existing watch is refused (`watch-overlap:`, naming the covering watch — nothing is
  written); registering a broader watch instead prunes every watch it contains (registration
  row + fingerprints removed, listed in the result's `pruned`) — already-ingested entries are
  kept, and the broader watch's catch-up scan re-covers them (idempotent, hash-skip cheap).
  `memory_watch_status` lists every registered watch with live state
  (`scanning`/`healthy`/`retrying`/`stopped`), last error and last sync; it is available in
  every access tier. `memory_watch_remove` stops and unregisters; a non-existent watch is a
  no-op. Registration failures surface as `watching-disabled:` / `path-outside-scope:` /
  `path-not-found:` / `watch-overlap:` tool errors; watch failures never fail the server.
- **`ai-raccoon.ignore`:** an optional gitignore-subset exclude file at the root of a watched
  directory (or a `memory_ingest_directory` call's root) — `<root>/ai-raccoon.ignore`, one file
  per root, never discovered in subdirectories; a `memory_ingest_directory` root without its own
  file falls back to the same ancestor resolution as a single-file ingest. An explicit
  `memory_ingest_file` call has no walk root of its own, so it resolves one: the containing registered watch if the path falls under
  one, else the ingest-scope allowlist entry that admits it, else the file's own parent directory
  as a last resort — and the same rule applies whether the file routes to memory or to the code
  corpus. Syntax: `*` (one path segment), `**` (zero or
  more segments), a trailing `/` for a directory pattern (matches the directory and everything
  beneath it), a leading `/` (or any `/` elsewhere in the pattern) anchors it to the root — a
  pattern with no `/` at all matches at any depth; `#` comments and blank lines are inert; no
  `!` negation in v1; case sensitivity follows the host OS. A matched file is never
  fingerprinted or chunked; a file that was already indexed before a matching rule appeared has
  its stale chunks removed (and its fingerprint cleared) the next time its watch digests it or
  its watch rescans. Editing the ignore file itself triggers a full re-scan of that watch. The
  ignore file is never matched against its own rules.
- **Deferred writes:** until an engine is configured, writes are stored deferred
  (`memory_stats.pending > 0`) and only become searchable after `memory_embed_pending`.
- **`memory_performance`:** project-scoped, except the reserved `__self_metrics__` project id,
  which returns the bank-wide series instead (a per-tenant whole-bank scope is still deferred). The
  `series` list is derived from the server's tool inventory plus the nine
  `memory_search` phases (`search.open`, `search.embed`, `search.fts`, `search.vector`,
  `search.fusion`, `search.affinity`, `search.adjustment`, `search.snippets`,
  `search.bump`) — not from what happens to be
  in the metrics table — so a tool or phase that has never been recorded still appears,
  at `count: 0`, rather than being omitted. `bucketMinutes` wider than `windowMinutes`
  is never an error: it clamps to the window and the series returns one averaged point.
  A window with no measurements is an empty series (every `count: 0`), never an error.
  Maintenance-job series (`job.<name>.duration_ms` on every completed run, `job.<name>.rows` for a
  job that reports an outstanding-row count) are bank-wide, not project-scoped, so they only
  appear in the whole-bank self-metrics report — same surface as `metrics.dropped` (#477).
  Three more series recorded as measurements beside their log line (WP11, "log-values-as-metrics"):
  `drain.<memory|code>.rows` and `drain.<memory|code>.duration_ms` (an embed-drain pass, EventId
  1003) and `search.query.truncated_tokens` — how many tokens a search query ran OVER the
  embedding window, `tokens - maxTokens` (EventId 426) — are bank-wide like `job.*` — neither a
  drain pass nor the embedding engine's query-trim path has a project id to record under.
  `write.replace.wait_ms`, `write.replace.held_ms` and `write.replace.rows` (a replace-by-path
  transaction, EventId 899) are project-scoped, recorded under the writing project's own id, and
  so appear in an ordinary project's report the same way a tool series does. `wait_ms` is time
  spent waiting for `BEGIN IMMEDIATE` to return; `held_ms` is time from there to
  `COMMIT`/`ROLLBACK`. WP12 moved the chunker (file read, chunk, hash, insert) OUTSIDE the write
  lock — only a short claim transaction (decides which of two racing replaces on the same path
  chunks it) and the prune-and-fingerprint transaction still hold it, each a few statements long
  — so `held_ms` reflects the write itself, never the chunker; a slow chunker under contention now
  shows up as `wait_ms`, not `held_ms`. The claim transaction's own authoritative guard re-check
  can decline (a rare race between two replaces on the same path): that path records `rows = 0`
  with a real `held_ms`, the same `0` the EventId 899 log line reports there. The common no-race
  decline (fingerprint already matches) is checked in an unlocked read before the claim transaction
  and records nothing at all — there is no wait or held time to report.

### Unknown-id rule

An id that a tool cannot act on is handled one of two ways, and which way depends on what kind of
tool it is (see [ADR-0024](../adr/0024-unknown-id-contract.md)):

- **A removal verb is idempotent and reports a count.** An unknown id is a no-op, not an error —
  `memory_delete`, `memory_delete_context`, and `memory_promotion_discard` return `0` for the
  count they would otherwise report; `memory_watch_remove` treats a non-existent watch the same
  way. Calling a removal verb twice on the same id is safe by construction.
- **A state transition refuses an id it cannot act on.** `memory_share` and the workspace family
  (`memory_write` with `workspaceId`, `memory_workspace_status`, `memory_workspace_consolidate`,
  `memory_workspace_discard`) return a typed refusal (`unknown-hash` / `unknown-workspace`, see
  [Error shapes](#error-shapes)) instead of silently doing nothing — there is no well-defined
  "already done" state for promoting a hash that was never written or writing into a workspace
  that was never begun.
- **An unregistered `projectId` on a write/destructive call is refused, not silently founded.**
  ADR-0089 decision 3: before this, any string a caller named became a project the moment
  something was written under it — a typo could not fail, it founded a ghost project. Now a write
  is refused (`project-not-registered`, see [Error shapes](#error-shapes)) unless the id is
  registered (`project_id_token_get`) or the bank already holds rows for it (a legacy raw-text id,
  which keeps working with a one-time warning). Reads are never refused this way.

### `capacity` semantics

`reserved` is not a fixed entitlement — it's `cap ÷ (number of projects currently
holding at least one queued row)`, recomputed fresh on every meta read
(`PromotionQueueService.GetMetaAsync`, `PromotionCapacityPolicy.CapacityFor`). The
denominator moves: it shrinks as unrelated projects' rows drain out of the queue and
grows the moment another project proposes its first row, so `reserved` for a project
that hasn't changed its own usage can still go up or down between two calls.

`borrowing: true` means "using more than the current fair share," not "at risk of
eviction." Eviction is a wholly separate rule — `PromotionCapacityPolicy.NeedsEviction`
fires only when the queue's total row count exceeds the total cap, regardless of which
projects are borrowing. A project can sit at `borrowing: true` indefinitely as long as
nobody pushes the total over cap.

**Worked example** (cap = 1000):

1. One project (`p1`) has proposed 400 rows. It is the only occupant, so
   `projectCount = 1`, `reserved = 1000 / 1 = 1000`. `p1` shows `reserved: 1000, used:
   400, borrowing: false` (400 ≤ 1000).
2. Four unrelated projects each propose one row. The queue now has 5 occupying
   projects and 404 total rows (well under the 1000 cap, so no eviction fires).
   `projectCount = 5`, `reserved = 1000 / 5 = 200`. Without `p1` proposing or
   discarding anything, its next meta read shows `reserved: 200, used: 400,
   borrowing: true` (400 > 200) — the same 400 rows, a smaller fair share, because
   four other projects showed up.

> **Evidence:** `src/AiRaccoon.Core/Memory/PromotionCapacityPolicy.cs:12-35`
> (`ReservationFor`, `NeedsEviction`, `CapacityInfo`),
> `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:138-150`
> (`GetMetaAsync`, `projectCount` = occupying projects)

## Prompts (2)

| Prompt | Purpose |
|---|---|
| `memory-usage-guide` | Protocol: always pass `project_id`; **search memory first** (2-3 query formulations) and escalate to web/code search only by result, writing findings back; watch setup (`ai-raccoon settings ingest scope add` + `settings watch enable`, then `memory_watch_add`/`status`/`remove`); workspace isolation, promotion via `memory_share`, search scopes, degradation, bulk ingest. |
| `workspace-consolidation-guide` | Ritual: list the outbox, promote durable facts, drop noise. |

## Contexts

| Context | Meaning | Synced? | Swept? |
|---|---|---|---|
| `shared` | curated cross-project knowledge — only via `memory_share` | yes | exempt |
| `project:<project-id>` | committed, durable project memory | yes | yes |
| `workspace:<workspace-id>` | sandboxed workspace scratch (outbox) | never | no |
| custom | user-defined labels (`docs:api`, …) | yes | project sweep only |

## Access modes

Three-tier access control (FR-NM-2), enforced at the tool boundary:

| Mode | Reads | Writes | Destructive (delete, sweep, consolidate) |
|---|---|---|---|
| `ro` | ✓ | ✗ | ✗ |
| `rw` (default) | ✓ | ✓ | ✗ |
| `full` | ✓ | ✓ | ✓ |

- The **global default** is `rw`.
- The global default is set with `ai-raccoon settings access default set {ro|rw|full}`
  (row `access.mode.global` in the settings table; unset resolves to `rw`).
- A **per-project override** is stored in the settings table under
  `access.mode.project:<id>` — it takes precedence over the global setting.

## Environment variables

| Variable                  | Purpose                                                                                                                             |
|---------------------------|-------------------------------------------------------------------------------------------------------------------------------------|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (page-level via SQLite3MC, SQLite3MC.PCLRaw bundle, default cipher chacha20/sqleet; unset = plaintext) |

Beyond that, the only other environment variables read are the `OTEL_*` ones the
OpenTelemetry SDK itself reads for OTLP export (serve/HTTP mode only, opt-in) —
notably `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`,
`OTEL_METRIC_EXPORT_INTERVAL`, and `OTEL_METRIC_EXPORT_TIMEOUT` —
see [OTLP export](#serve-mode) below and [ADR 0009](../adr/0009-otlp-export.md) for the
current set and behavior rather than treating this list as exhaustive.
`OTEL_SERVICE_NAME` is read by the SDK but has no effect here: `service.name` is a
fixed product identity (`ai-raccoon`) that this codebase's own resource registration
always wins over.

All other configuration (access modes, embedding engine, retrieval alpha, sweep,
sync, watch) lives in the settings table and is changed with the CLI verbs below —
environment variables are not read for that runtime configuration (single-channel
ruling). Secrets (OpenAI API key, S3 access/secret keys or the Azure Blob connection
string) are stored in the settings table (encrypted at rest when a passphrase is set),
never in the environment and never in tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags are CLI-only; a verb runs a one-shot config command
against the bank (results to stdout), bare `ai-raccoon` (with optional launch flags)
runs the server.

| Option | Values | Default |
|---|---|---|
| `--transport` | `proxy`, `stdio`, `http`, `https` (https → warning) | `proxy` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |
| `--port <n>` | `1`-`65535`; `0` (random free port) is `serve`-only — the proxy has to dial a port it knows | `7721` |
| `--quiet` | flag | off |

`proxy` is the default and the zero-config path
([ADR 0020](../adr/0020-always-on-http-stdio-proxy.md)): bare `ai-raccoon`
opens no bank, resolves no encryption key, and loads no embedding model — it
probes `http://127.0.0.1:<port>/mcp`, spawns `ai-raccoon serve` when nothing
answers, and forwards every JSON-RPC message to it, restoring the client's
own request id on the response. No tool method is named in the proxy, so a
new tool needs no proxy change. If the backend can neither be reached nor
started within its budget, the process exits `ExitCode.ProxyBackendUnavailable`
(6) with one stderr line naming the URL, the `serve` exit code, and the
`--transport stdio` escape hatch — there is no in-process fallback.
`--transport stdio` is that escape hatch: a complete in-process server, no
proxy, no autostart — exactly how the server behaved before `proxy` became
the default.

`--quiet` sends every log level of a *server host* — the in-process `--transport stdio`
server, `--transport http`, and `serve` — to a file beside the bank instead of
stdout/stderr: `~/.ai-raccoon/quiet.log` at the default user scope, or
`<data-root>/.ai-raccoon/quiet.log` at project scope, the same directory `memory.db` lives
in (`HostLogging.Configure`, `QuietLogging.LogFilePath`,
`SqliteConnectionFactory.BankPathFor`). Nothing from those hosts, not even a warning,
reaches stdout or stderr in this mode, so a `--quiet` server that fails to start or
misbehaves (e.g. an invalid `OTEL_EXPORTER_OTLP_ENDPOINT`) leaves no trace on the
console — check `quiet.log` first. The file is append-only and never rotated; it
accumulates for the life of the installation.

The proxy is deliberately exempt. It builds its own logger factory
(`ProxyRunner.CreateLoggerFactory`) with no file destination, so under `--quiet` it still
logs to stderr at `Warning` and above; and the one line that says the backend could neither
be reached nor started is *written* to stderr rather than logged, so `--quiet` cannot
silence it. A proxy that cannot get a backend says so on the console either way. The
`serve` backend it spawns does inherit `--quiet`, so the backend's own logs land in
`quiet.log`.

### Serve mode

Since ADR-0020, `serve` is not only a manual verb — it is autostarted by the
default `proxy` transport, at proxy startup, whenever nothing already answers on
the port. A client that connects and never calls a tool still leaves a backend
running. This section describes `serve` itself, whether started by the proxy or
run by hand.

`ai-raccoon serve` is the HTTP mode as a first-class verb: it forces the http
transport, applies a 4h idle watchdog (`--idle-timeout 90s|30m|4h|1d`, `0`
disables), prints the bound URL to stdout, and stays in the foreground —
background it with `ai-raccoon serve > serve.log 2>&1 &` (POSIX). If the port
already hosts an ai-raccoon server, `serve` attaches to it and exits 0; the
owning process keeps the watchdog, and the attached run never touches the bank.
A busy port held by a foreign listener fails fast with exit code 3 and a
`--port 0` hint.

`serve --restart` cycles that server instead of attaching to it (ADR-0022).
Attaching is wrong on exactly one path — an update: `dotnet tool update`
replaces the binary while the always-on backend keeps the old assembly loaded,
so every later client attaches to the stale one. `--restart` asks the running
server to stop over `POST /shutdown` (token-guarded, POST-only), waits for the
port to free, then serves in its place; with nothing listening it is a plain
`serve`. The stop gets 10s in total — the host's stated `ShutdownTimeout`,
shared by in-flight calls and every background service, not a per-call
guarantee — after which what is left is aborted and the proxy's documented
at-least-once retry re-issues it against the new backend. The port is then
given 20s to free.

`--restart` kills no process and never falls back to attaching. Every way the
cycle can fail exits `8` with a line naming the port and the manual escape:
the server refuses our token (it serves another data root), it has no
`/shutdown` (too old to be cycled — the first update *onto* this version still
needs the old process stopped by hand), our data root holds no token to
present (nothing is asked to stop), the port is still held after the bound, or
another start won the port while this one was binding. A listener that does
not identify as an ai-raccoon over `/observability` is never sent a shutdown:
it is refused before the bind is attempted, with the unchanged exit code 3 and
a line saying the port is held by something that is not an ai-raccoon.

`/mcp` and `/shutdown` require `X-AiRaccoon-Token` or `Authorization: Bearer
<token>` (the Bearer envelope added 2026-08-09, see
[ADR 0020](../adr/0020-always-on-http-stdio-proxy.md) §"Amendment
2026-08-09"): before binding, `serve` mints a random token into
`<data-root>/mcp-token` (0600, exclusive create, reused across restarts), and
every request to either must present one of the two — the proxy reads the file
after a successful probe and sends `X-AiRaccoon-Token` automatically. An
unauthorised call gets one of two 401 bodies: one naming the headers when no
credential was sent at all, one saying the presented value does not match when
it was — the difference matters because an unexpanded `${AIRACCOON_MCP_TOKEN}`
placeholder is a *present* credential, and the first wording would tell you to
add the header you just added. Neither body names anything the other does not.
`/observability` stays unauthenticated by design (it returns a PID, the binary
version and OTLP on/off state, nothing that touches the bank). A direct
`ai-raccoon --transport http` launch (no `serve` verb) is **not** gated, and
gets no `/shutdown` at all — see [SECURITY.md](../../SECURITY.md) for the
reasoning and the known gaps.

`serve --mcp-entry [--format hermes|claude|all]` prints the client config
entry for the actually-bound URL, now with a `headers` map carrying
`X-AiRaccoon-Token: ${AIRACCOON_MCP_TOKEN}` alongside the URL — a
placeholder, never a live token. Keep stderr out of the entry file:
`ai-raccoon serve --mcp-entry > entry.json 2> serve.log &`. One long-lived
HTTP server avoids the ~5-minute stdio recycle of per-connection processes and
lets the background extraction and bank-maintenance hosted services actually
fire. Turning that entry, or `hermes mcp add`'s own auth prompt, into a
connection that actually authenticates is the advanced path below.

#### Direct HTTP access (advanced)

Bare `ai-raccoon` (the proxy) is the default, handles the token itself, and
needs none of what follows. Connecting a client straight to `serve`'s URL,
bypassing the proxy, is still genuinely useful for two narrower cases: a
client that cannot spawn a process at all (a containerised or remote client,
a gateway reaching in over a tunnel), and bisecting a **proxy** failure —
when bare `ai-raccoon` is what's broken, `--transport http` plus `curl` is
the diagnostic you reach for exactly when the default is down.

Every incantation below uses single quotes, or an unquoted `$(...)` meant to
expand immediately, around the token — `--header "X: ${VAR}"` in double
quotes is expanded by *your own shell* before the CLI ever sees it, silently
storing an empty or corrupted value. That footgun sits directly in the
copy-paste path, which is why it is worth avoiding by construction rather
than by care.

**1 — Hermes, via the CLI's own auth prompt.** The token lands in
`~/.hermes/.env`, never in `config.yaml`:

```bash
ai-raccoon serve > serve.log 2>&1 &
hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp
#   "Does this server require authentication?" -> y
#   "API key / Bearer token"                   -> paste the output of: cat ~/.ai-raccoon/mcp-token
```

`hermes mcp add` only prompts when the `.env` key for this server is empty —
a retry against a *different* `--data-root` (a different token) gets a
silent 401 with no prompt the second time round; edit the `.env` line by
hand or start from a fresh profile.

**2 — Hermes, via the printed entry.** Paste `entry.json` under
`mcp_servers` in `~/.hermes/config.yaml`, then add the one `.env` line the
placeholder resolves against:

```bash
ai-raccoon serve --mcp-entry > entry.json 2> serve.log &
echo "AIRACCOON_MCP_TOKEN=$(cat ~/.ai-raccoon/mcp-token)" >> ~/.hermes/.env
```

**3 — Claude Code.** Verified against the installed CLI: `${VAR}` expansion
in `.mcp.json` covers `command`, `args`, `env`, `url` and `headers`, and
`${VAR:-default}` works. An unset variable is not a hard failure —
`claude mcp list` prints a warning, but the connection still fires and sends
the literal `${AIRACCOON_MCP_TOKEN}` string, which the server's 401 then
names. Export the variable before adding the server, and single-quote the
header so *your* shell leaves the placeholder alone for Claude Code to
expand. Use `--scope local` (the default) or `--scope user` — **never
`--scope project`**, which writes `.mcp.json`, the one file a repo is likely
to commit; `local` and `user` both write `~/.claude.json`, which is per-user
and untracked:

```bash
export AIRACCOON_MCP_TOKEN=$(cat ~/.ai-raccoon/mcp-token)
claude mcp add --transport http --scope user ai-raccoon http://127.0.0.1:7721/mcp \
  --header 'X-AiRaccoon-Token: ${AIRACCOON_MCP_TOKEN}'
```

None of the three commands above has been run by this change — the gate
that makes `Authorization: Bearer` and the `${AIRACCOON_MCP_TOKEN}`
placeholder real ships in a parallel lane. Treat all three as
**untested-by-you**: the proof is
[the regression-fix plan](../plans/2026-08-09-http-token-clients.md)'s own
§E integration gate, run against a live `serve`, not this doc.

`serve observability <counters|trace|otlp|pid> [--port <n>]` prints a ready-to-run
diagnostic command for the **running** server, with its process id filled in. It
does not start or touch a server: it reads the PID from `GET /observability` on
the loopback port (default `7721`), so the value cannot go stale, and it returns
the owning process's PID even when the server it dials was itself started by an
attached `serve`. The verb never opens the bank, resolves the encryption key, or
loads the embedding engine.

| Kind | stdout |
|---|---|
| `counters` | `dotnet-counters monitor -p <pid>` — `System.Runtime` only (GC, CPU, working set, thread pool); append `--counters AiRaccoon.MemoryTools` for the tool metrics, broken out by `project_id` on the invocation counter |
| `trace` | `dotnet-trace collect -p <pid> --providers AiRaccoon.MemoryTools,AiRaccoon.Background,Microsoft.AspNetCore,System.Net.Http` |
| `otlp` | the OTLP endpoint the server exports to; the protocol goes to stderr |
| `pid` | the bare process id, for composing with other tools |

Exit codes: `0` success; `4` nothing listening on the port (or the server predates
the endpoint); `3` the port is held by a foreign listener; `5` `otlp` was asked for
but the server has no OTLP export configured. `--port 0` is a parse error — unlike
`serve --port 0`, there is no "any free port" to dial. Failures write nothing to
stdout, so command substitution yields an empty string rather than an error message.

OTLP export is **serve/HTTP mode only** — a stdio server is a per-connection
process on a ~5-minute recycle, too short-lived for a batch exporter to be worth
its schedule delay and shutdown grace. Since ADR-0020 that scope covers nearly
all traffic: the default `proxy` transport forwards every call to a `serve`
backend, so instrumentation now reaches whatever a client does, not only
callers who opt into `serve` directly. The proxy itself wires no exporter and
propagates no `traceparent` — it records nothing, so there is nothing of its
own to export, and the server it forwards to stays the trace root. It is
opt-in and configured only through the standard `OTEL_EXPORTER_OTLP_ENDPOINT` /
`OTEL_EXPORTER_OTLP_PROTOCOL` variables, read at host-build time; unset means
no exporter is constructed. Exported: the
`AiRaccoon.MemoryTools` meter and ActivitySource, the `AiRaccoon.PromotionQueue`
meter, and the built-in `System.Runtime` meter. See
[ADR 0008](../adr/0008-live-pid-discovery-for-monitoring.md),
[ADR 0009](../adr/0009-otlp-export.md), and
[ADR 0020](../adr/0020-always-on-http-stdio-proxy.md).

Config verbs (each writes settings rows in the bank's settings table; the running
server hot-reloads them):

Every family below lives under the top-level `settings` command
(`ai-raccoon settings <family> …`), with four exceptions that stay top-level because they
either read a table `settings` doesn't own or perform an operation that isn't a settings
write: `ai-raccoon watch registered` (reads the watches table), `ai-raccoon extract prune`
(deletes `promotion_queue` rows), `ai-raccoon model embedding set` (starts re-embedding the whole bank in the
background, ADR-0076), and `ai-raccoon encryption` / `ai-raccoon serve` (unaffected by this split).

```bash
# settings access: who may do what per project
ai-raccoon settings access default set {ro|rw|full}
ai-raccoon settings access default show
ai-raccoon settings access set {project-id|*} {ro|rw|full}
ai-raccoon settings access unset {project-id|*}
ai-raccoon settings access list

# model: embedding engine selection ('set' stays top-level — it starts re-embedding the whole bank in
# the background, ADR-0076; 'show'/'reset' move under settings)
ai-raccoon model embedding set local [path]
ai-raccoon model embedding set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon settings model embedding reset
ai-raccoon settings model embedding show
ai-raccoon settings model threads {n}       # ORT intra-op thread cap; 0 = ORT default, unset = max(1, logicalCores/2)

# model code set: the code corpus's own engine — independent settings rows, any manifest
# dimension accepted (vec_code is reconciled to it), no memory-bank re-embed
ai-raccoon model code set default   # downloads the default model if needed, then activates it
ai-raccoon model code set local <dir>
ai-raccoon settings model code reset
ai-raccoon settings model code show
ai-raccoon settings model threads {n}      # ORT intra-op thread cap; 0 = ORT default, unset = max(1, logicalCores/2)
ai-raccoon settings model code reset
ai-raccoon settings model code show
ai-raccoon settings model threads {n}      # ORT intra-op thread cap; 0 = ORT default, unset = max(1, logicalCores/2)

# model code set: the code corpus's own engine — independent settings rows, refuses non-768
# manifests before anything commits (§3.3 D-E9), no memory-bank re-embed
ai-raccoon model code set default   # downloads the default model if needed, then activates it
ai-raccoon model code set local <dir>

# settings retrieval: hybrid-search blend weight
ai-raccoon settings retrieval alpha set {0..1}
ai-raccoon settings retrieval alpha show

# settings sweep: the background reaper — ON by default, deletes expired entries on its
# cadence (default every 24 h). 'sweep disable' is the kill switch; 'sweep show'
# reports the whole policy (enabled, interval, threshold). Interval changes apply
# live, no server restart needed.
ai-raccoon settings sweep enable
ai-raccoon settings sweep disable
ai-raccoon settings sweep interval-hours {1..8760}
ai-raccoon settings sweep threshold set {0..1}
ai-raccoon settings sweep show

# settings sync: cloud snapshot sync
ai-raccoon settings sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]
ai-raccoon settings sync add azure {container} [--object-key {key}] [--cli --account {name}]
ai-raccoon settings sync remove
ai-raccoon settings sync show

# watch: file-watcher configuration (registers happen via memory_watch_add).
# 'watch registered' stays top-level — it reads the watches table, not settings.
ai-raccoon settings watch enable {project-id|*} {true|false}
ai-raccoon settings watch disable {project-id|*} {true|false}
ai-raccoon settings ingest scope add {project-id|*} {path}
ai-raccoon settings ingest scope remove {project-id|*} {path}
ai-raccoon settings ingest scope list {project-id|*}
ai-raccoon settings watch concurrency {project-id|*} {1..16}
ai-raccoon settings watch list
ai-raccoon watch registered [{project-id}]
ai-raccoon settings watch remove {project-id|*}

# encryption: bank key source (unaffected — the bootstrap path stays on the CLI)
ai-raccoon encryption bitwarden [-t <token>]
ai-raccoon encryption show
ai-raccoon encryption unset
ai-raccoon encryption migrate

# extract: background shared-extraction (HTTP/S hosts only — a stdio process is
# per-connection and recycled before the loop can fire; default interval 30 min;
# config changes apply live, no server restart needed; propose logs the ranked
# candidates — path, preview, reasons — to the server log; prune reports/removes
# promotion_queue rows orphaned by a deleted or re-chunked entries row (ADR-0023) —
# read-only by default, --apply removes, idempotent). 'extract prune' stays
# top-level — it deletes promotion_queue rows, not a settings write.
ai-raccoon settings extract enable {true|false}
ai-raccoon settings extract mode {propose|promote}
ai-raccoon settings extract interval {minutes}
ai-raccoon settings extract capacity {capacity}
ai-raccoon settings extract auto-promote-threshold {score|off}
ai-raccoon settings extract exclude add {prefix}
ai-raccoon settings extract exclude remove {prefix}
ai-raccoon settings extract exclude list
ai-raccoon settings extract list
ai-raccoon extract prune [--apply]

# settings maintenance: bank housekeeping (every process checkpoints the WAL at startup
# and shutdown — stdio included; the periodic timer runs on HTTP/S hosts,
# default 60 min — and VACUUM + ANALYZE on the vacuum cadence, default 7 days;
# embed-rows-per-run bounds the embed topic's single consumer's rows per drain
# pass, for both corpora, default 128; config changes apply live, no server restart needed)
ai-raccoon settings maintenance interval {minutes}
ai-raccoon settings maintenance vacuum-interval {days}
ai-raccoon settings maintenance embed-rows-per-run {rows}
ai-raccoon settings maintenance list
```

**Encryption key sources.** Default: `AIRACCOON_DB_PASSPHRASE` (env). Alternative:
`encryption bitwarden` fetches an unencrypted ed25519 SSH private key from a Bitwarden
Secrets Manager secret via the `bws` CLI and derives the raw SQLCipher key with
`HKDF-SHA-256` (`System.Security.Cryptography.HKDF`, seed as IKM, no salt,
`"ai-raccoon-db-key/v1"` as `info`) → `x'<64hex>'` — see
[ADR 0012](../adr/0012-ssh-key-derivation-hkdf-replacement.md). The command checks
`bws` presence (install guidance when missing), collects project id + secret id
(default: an obviously fake placeholder, unless `AIRACCOON_BITWARDEN_PROJECT_ID` /
`AIRACCOON_BITWARDEN_SECRET_ID` is set — no default may identify a real vault entry),
accepts a per-run-only `-t <token>`, warns that rotating the secret in the Bitwarden UI
without `PRAGMA rekey` bricks the bank, then rekeys + persists. Server startup refuses
loudly when the configured source cannot produce the key.

**`encryption migrate`** rekeys a bank still encrypted under the pre-ADR-0012
`SHA-256(label ‖ seed)` derivation to the current HKDF key. It affects only the
Bitwarden/SSH key source — the env-var passphrase path never went through
`SshKeyDerivation` and is unaffected. It needs exclusive access to the bank (run it
with the MCP server stopped); one of three outcomes follows: the bank is rekeyed, the
bank is already on the current derivation (no-op), or the command refuses (wrong
secret or a damaged bank) and leaves the file byte-identical, so it is safe to retry.
See [how to rekey an encrypted bank](../how-to/rekey-an-encrypted-bank.md).

The backend is selected by the `sync.provider` settings row (default `s3`): `sync add
s3` writes `provider=s3`; `sync add azure` writes `provider=azure`. Each clears the
other provider's rows, so at most one backend is configured at a time. Provider secrets
are **prompted interactively** — the S3 access/secret keys on `sync add s3`, the Azure
connection string on `sync add azure` (prompt on stderr, input read
from stdin; an empty answer aborts with exit 1 and persists nothing) — never accepted on the
command line.

**`--cli` credential modes** skip the prompts and use the machine's CLI login state:
`sync add azure <container> --cli --account <name>` (account required — `--cli` without
`--account` is an error) uses `DefaultAzureCredential`; `sync add s3 <url> --bucket
<name> --cli` uses the AWS default credential chain. Only non-secret rows are stored
(`sync.azureAccount`, `sync.s3Chain`); switching modes clears the other mode's rows.
Auth failures map to `sync-auth-failed:` with a "run `az login`" / "run `aws configure` |
`aws sso login`" hint.

**Sync authentication methods** — four ways to authenticate, two per backend. Secrets are
never accepted on the command line; the prompt-based methods read from stdin (an empty
answer aborts with exit 1 and persists nothing). Only one provider is active at a time
(`sync add` clears the other provider's rows), and switching modes clears the other
mode's rows — settings never leave the local machine (ADR 0014), but a stale secret row
left behind on this one is still a needless liability once its mode is no longer in use.

| Method | Configure with | Stored in the settings table | Auth at sync time | On failure |
|---|---|---|---|---|
| S3 access/secret keys | `sync add s3 {url} --bucket {name}` (keys prompted) | `endpoint`, `bucket`, `region`, `accessKey`, `secretKey`, `objectKey` | `BasicAWSCredentials` from the stored keys (long-lived; encrypted at rest when a passphrase is set) | 403 → `sync-auth-failed:` ("verify the keys with `sync show`"); network → `sync-network:` |
| S3 AWS chain | `sync add s3 {url} --bucket {name} --cli` | `endpoint`, `bucket`, `region`, `s3Chain`, `objectKey` (no secrets) | AWS default credential chain — env vars, `~/.aws/credentials`, SSO (`aws sso login`), container/IMDS — resolved lazily on the first call | no credentials → `sync-auth-failed:` ("run `aws configure` \| `aws sso login`"); 403 → `sync-auth-failed:`; network → `sync-network:` |
| Azure connection string | `sync add azure {container}` (string prompted) | `connectionString`, `container`, `objectKey` | `BlobServiceClient(connection string)` — account name + key in one string (long-lived; encrypted at rest when a passphrase is set) | malformed string → `sync-not-configured:`; 401/403 → `sync-auth-failed:`; missing container (404) → `sync-network:` — create the container first |
| Azure az CLI | `sync add azure {container} --cli --account {name}` | `azureAccount`, `container`, `objectKey` (no secrets) | `DefaultAzureCredential` chain — env (`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET`), workload identity, managed identity, VS/VS Code, az CLI login — endpoint built as `https://{account}.blob.core.windows.net` | no login → `sync-auth-failed:` ("run `az login`"); 401/403 → `sync-auth-failed:`; network → `sync-network:` |

**Which method when:**

- **`--cli` methods** suit developer machines that already log into az/aws — nothing
  long-lived is stored in the settings table, the tokens are short-lived and revocable,
  and auth failures are loud and fixable. Prefer SSO over static keys in
  `~/.aws/credentials`.
- **Prompted-secret methods** suit headless/CI environments (env-var credentials work
  through the same `--cli` chains) and non-AWS S3-compatible endpoints (MinIO, R2, …)
  where no CLI login exists. The secrets live in the settings table, encrypted at rest
  when a passphrase is set.
- If both modes' rows exist (manual settings edits), the stored secret wins the
  tie-break: connection string over az CLI, keys over chain.
- `sync show` prints the provider first, then the mode's fields, with secrets redacted
  (`set`/`unset`); `sync remove` deletes every `sync.*` row.

> `sync add azure` does **not** create the container — create it first (e.g. `az storage
> container create --account-name <account> --name <container>`), or the first sync
> fails with `sync-network:`.

**Azure (az CLI mode) setup — least privilege:**

```bash
az login                                        # sign in once (Azure CLI)
az storage account show -g <rg> -n <account> --query id   # find the storage account resource id
az role assignment create --assignee "you@domain.com" --role "Storage Blob Data Contributor" \
  --scope "<storage-account-resource-id>"       # least privilege: scope to account or container
```

`--cli` mode uses DefaultAzureCredential — az CLI login state, or the env vars
`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` for headless use. Nothing
long-lived is stored in the settings table; the token is short-lived and revocable.

**AWS (chain mode) setup — least privilege** (the sync only GETs and PUTs one object):

```bash
aws configure   # or: aws sso login (short-lived SSO tokens)
```

```json
{ "Version": "2012-10-17", "Statement": [ { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"],
  "Resource": "arn:aws:s3:::<bucket>/<object-key-prefix>*" } ] }
```

`--cli` mode uses the default credential chain (env, `~/.aws/credentials`, SSO, IMDS);
prefer SSO/short-lived credentials over static keys in `~/.aws/credentials`.

Secrets (OpenAI API key via `model embedding set openai --api-key`, S3 access/secret keys via
`sync add s3`, or the Azure connection string via `sync add azure`) are persisted in the settings table and are never launch flags — the
parser's unknown-option error is the defense. `--help`/`--version` and parse errors
print to **stderr** (exit 0 / exit 1). Generic host flags (`--environment`,
`--contentRoot`, `--applicationName`) are accepted hidden and ignored. A zero-config
`.mcp.json` entry is just `{"mcpServers": {"ai-raccoon": {"command": "ai-raccoon"}}}`;
registry installs (`.mcp/server.json`) pass no args (`packageArguments: []`).

When a client points `command` at the repo instead of the installed tool (e.g. VS Code's
`.vscode/mcp.json`):

```json
{
  "servers": {
    "AiRaccoon": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "<PATH TO PROJECT DIRECTORY>", "--no-launch-profile"]
    }
  }
}
```

`--no-launch-profile` matters: without it `dotnet run` prints its launch-settings
notice to stdout, which corrupts the newline-delimited JSON-RPC stream strict MCP
clients expect on stdio.

Encrypted-bank setups set `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped
config, never in a shared or tracked file:

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "env": {
        "AIRACCOON_DB_PASSPHRASE": "change-me"
      }
    }
  }
}
```

## Local embedding model

Local embeddings run in-process on ONNX Runtime over the small int8
all-MiniLM-L6-v2 model (dimension 384, mean-pool + L2-normalize) **bundled inside
the tool package** — `ai-raccoon model embedding set local` needs no sidecar, server
process or download. The binary is gitignored and fetched once by the pinned script
(SHA-256 verified); the tests FAIL (never skip) when it is missing:

```bash
scripts/download-embedding-model.py          # -> src/AiRaccoon/Models/model_qint8_arm64.onnx + vocab.txt
```

A custom ONNX model path overrides the bundled model via
`ai-raccoon model embedding set local /path/to/model.onnx`.

## Embedding configuration matrix

The embedding engine (configured via `ai-raccoon model …`) resolves exactly two engines:

| Engine | `provider` | `model` | `baseUrl` | Key | Notes |
|---|---|---|---|---|---|
| Local (bundled ONNX) | `local` | optional ONNX path (default: bundled model) | ignored | none | In-process, offline, no API cost |
| OpenAI-compatible | `openai` | model id (required), e.g. `nomic-embed-text` | optional endpoint (default `https://api.openai.com/v1`) | `--api-key` (persisted in settings table) | Any OpenAI-compatible `/embeddings` backend (LM Studio, Ollama, self-hosted, OpenAI) |

Changing the engine (provider, model or baseUrl) re-embeds the bank with the new
engine.

## Error shapes

A known, expected refusal — an invalid argument, a disabled feature, a path outside
scope — comes back as a normal MCP tool error (`CallToolResult.IsError = true`) rather
than an escaping exception, and is logged at `Information`, not `Error` (issue #151,
fixed by the `ToolRefusals` CallToolFilter in PR #163). Its message always starts with
one of the wire prefixes below — that mapping lives in
`ToolRefusals.RefusalPrefixes` (`src/AiRaccoon/Tools/ToolRefusals.cs`), which is the
source of truth; a test cross-checks this table against it.

| Prefix | Condition | Example message |
|---|---|---|
| `path-outside-scope` | Ingest/watch path falls outside the project's declared ingest scope | `path-outside-scope: Path '<path>' is outside the ingest scope.` |
| `path-not-found` | Ingest/watch path does not exist | `path-not-found: Path '<path>' does not exist.` |
| `unknown-workspace` | `workspaceId` does not exist, or is not active, for the project | `unknown-workspace: Workspace '<id>' does not exist for project '<project>'.` |
| `unknown-hash` | `hash` (e.g. passed to `memory_share`) does not exist in the project's scope | `unknown-hash: No entry with hash '<hash>' in project '<project>'.` |
| `schema-version-unsupported` | The bank's stored schema version is newer than this binary supports (issue #200) | `schema-version-unsupported: bank schema v<n> is newer than this binary supports (v<m>); update ai-raccoon` |
| `watching-disabled` | Watching is disabled for the project | `watching-disabled: Watching is disabled for project '<project>'.` |
| `watch-overlap` | `memory_watch_add`'s path is already covered by an existing watch (no overlapping watches — the broader watch wins; adding a broader watch instead prunes the narrower ones rather than refusing) | `watch-overlap: Path '<path>' is already covered by watch '<covering-path>'.` |
| `sync-not-configured` | No sync credentials configured | `sync-not-configured: Memory sync is not configured or its connection string is invalid. Run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted.` |
| `sync-auth-failed` | Sync credentials missing/invalid, or a 401/403 from the cloud provider | `sync-auth-failed: Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).` (Azure) / `sync-auth-failed: AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.` (S3) |
| `sync-conflict` | Remote snapshot kept changing mid-merge, past the 3 re-pull/re-merge/re-push retries | `sync-conflict: <detail>` |
| `sync-network` | Network-level failure during sync push/pull. A missing bucket/container (404) on **push** also lands here; on **pull** a 404 means "no remote snapshot yet" and returns null instead — it is not a refusal | `sync-network: <detail>` |
| `sync-corrupt-file` | `PRAGMA quick_check` failed on the pulled remote snapshot — the local DB is not replaced | `sync-corrupt-file: <detail>` |
| `sync-tampered-remote` | The pulled remote snapshot's embedded HMAC authenticity tag does not match its bytes, **or** the blob has no tag at all for an objectKey this bank has previously verified one for (checked before `PRAGMA quick_check` and before `ATTACH`) — the local DB is not replaced. An encrypted bank keys the tag from its own passphrase via `HKDF`; a headerless remote is accepted with a logged warning only the first time this objectKey is ever seen (trust-on-first-use). An encrypted bank synced by ≥1.31 cannot be pulled by <1.31 — upgrade both ends of an encrypted sync pair together | `sync-tampered-remote: <detail>` |
| `access-denied` | The resolved access mode (`ro`/`rw`/`full`) does not permit the attempted operation | `access-denied: <detail>` |
| `project-not-registered` | A write/destructive call named a `projectId` with no registry row and no existing rows either (ADR-0089) — reads are never refused. A legacy raw-text id the bank already holds rows for keeps working, with a one-time warning, instead of this refusal | `project-not-registered: Project '<id>' is not registered. Call project_id_token_get to mint and register a project id before writing.` |
| `context-outside-project` | A write's `context` names a project other than the request's `project_id` | `context-outside-project: Context '<context>' writes into a project other than '<project_id>'. A write may only target its own project.` |
| `invalid-params` | FluentValidation rejected the request (invalid `scope`, out-of-range `limit`, etc.), or the call named no `projectId` and cwd-default resolution found no candidate — the refusal names the probed working directory. When resolution finds two or more candidates the message is `invalid-params: projectId is ambiguous from cwd <cwd>: candidates <ids>`. The one exception is `memory_promotion_list`, whose omitted projectId means all-projects (that tool's cross-project feature) and never cwd-defaults. | `invalid-params: projectId is required (no registered project's scope contains cwd <cwd>; pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)` |
| `invalid-argument` | A call's JSON argument shape doesn't match the tool's declared parameter type (e.g. a scalar where an array is declared), a required parameter is missing, or a present-but-blank value fails a guard clause — caught at argument-binding time or by a guard clause at the top of the tool method, before its logic runs. Mapped from `JsonException`, `ArgumentException` and `ArgumentNullException`. `ArgumentOutOfRangeException` is deliberately **not** mapped: it is how .NET reports the server's own index arithmetic going wrong, so refusing it would mute Error-level alerting and tell the caller to retry an argument that was never at fault | `invalid-argument: The JSON value could not be converted to System.String[]. Path: $ \| LineNumber: 0 \| BytePositionInLine: 5.` |
| `confirm-required` | `memory_share_extract` called with `autoPromote=true` but `confirm` not set to `true` — an explicit enable gate for a promotion that shares data across all listed projects | `confirm-required: autoPromote shares candidates with ALL projects — pass confirm=true to enable` |
| `model-migration-in-progress` | Every bank operation is refused for the duration of an embedding-model migration (`model embedding set`, ADR-0076) — a bank whose rows are half old-model and half new-model vectors is not detectably broken, it just retrieves worse, so the migration locks the bank rather than serving through it | `model-migration-in-progress: ai-raccoon: a model migration is in progress; try again once it finishes (memory_write)` |
| `embedding-install-replaced` | The bundled embedding model/vocab could not be resolved because the install this server process started from (`AppContext.BaseDirectory`) no longer exists on disk — replaced or removed out from under a still-running server (e.g. `dotnet tool update` moving the outgoing version into `.store/.stage` and deleting it; already-mapped assemblies keep the process serving MCP calls even though its own install root is gone). A plain `InvalidOperationException` from the same lookup still means the asset is genuinely missing next to a live install and stays unmapped — only this replaced-install case is refused, because only a restart fixes it | `embedding-install-replaced: Bundled embedding model 'model_qint8_arm64.onnx' could not be resolved: the install this server started from ('<dir>') no longer exists, likely replaced by a tool update (e.g. 'dotnet tool update'). Restart the MCP server (or its host) to pick up the new install.` |
| `code-engine-unloadable` | A code engine IS configured (`embedding.codeModel`) but its manifest or model/tokenizer files fail to load at search time (missing files, a dimension mismatch, a corrupt asset) — distinct from "no engine configured" (which degrades to FTS5-only silently, no refusal). Affects `memory_search kind=code/both` only; `kind=memory` is unaffected, since the memory and code engines are independent settings rows | `code-engine-unloadable: The configured code engine at '<dir>' could not be loaded: <detail> Run 'ai-raccoon model code set local <dir>' to reconfigure it, or clear it with 'ai-raccoon settings model code reset'.` |

Anything `ToolRefusals` does not recognize — a remote embedding provider called without
a key, or any other unmapped exception — is a genuine failure, not a refusal, and its message
does **not** reach the caller. The MCP SDK's `CreateToolCallErrorResult` surfaces the exception
message only for `McpException`; for every other exception type it discards the message and
replaces it with the bare string `"An error occurred invoking '<tool>'."` (measured against the
live server; see `docs/adr/0019-forward-version-write-guard.md`). So a call that hits, say, the
embeddings service's plain `InvalidOperationException`
(`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` —
`"OpenAI-compatible embeddings require an API key: run 'ai-raccoon model embedding set openai <model>
--api-key <key>'."`, thrown from whichever tool needed the embedding engine — `memory_write`,
`memory_search`, `memory_ingest_file/directory`, `memory_embed_pending`) does not get that text on
the wire at all: the caller sees only `"An error occurred invoking '<tool>'."`, logged at `Error`,
with no indication of what to fix. This is precisely why the `ArgumentException` family is mapped
to `invalid-argument` above instead of being left unmapped — an unmapped exception type's message
is unrecoverable information loss, not just an untyped prefix.

## Managed store

All tables, indexes, FTS5 virtual table, vec0 virtual table, and triggers live in
`memory.db` with no native extension dependencies. `MemorySchema.EnsureAsync` creates
the schema on first open with `IF NOT EXISTS` on every DDL statement — idempotent,
safe to run on every bank open. No download-on-first-run provisioning, no per-RID
extension binaries, no external SQLite modules.

**Encryption at rest.** When `AIRACCOON_DB_PASSPHRASE` is set, the connection opens with `Password` in the connection string, enabling transparent page-level encryption via the bundled SQLite3MC engine (default cipher chacha20, sqleet
ChaCha20-Poly1305 scheme; the scheme is stored per-database and auto-detected on open). FTS5, vec0, and all SQL operations
work unchanged — encryption is at the page level, invisible to queries. Without
the passphrase the bank is plaintext (backward compatible).

## Deletion and sync semantics

- Deletes are permanent — there is no trash or recovery.
- `memory_delete` targets one hash wherever it lives, including a `shared` row;
  `memory_delete_context` deletes every entry under a context label. Nothing forbids
  targeting `shared` — use it deliberately.
- Deleting a synced context (`shared`, `project:<id>`, custom) removes rows locally;
  the deletion is pushed as a tombstone on the next `memory_sync`, so the removal
  propagates to the cloud copy.
- Workspace contexts are never synced, so `memory_workspace_discard` and consolidation's
  discard have no cloud counterpart.

## Known limitations

- There is no tool to list active workspaces: `memory_workspace_status` needs a
  `workspaceId` you must already hold (keep the value returned by `memory_workspace_begin`).
- No un-share tool exists; see `memory_share` notes above.
- No existing-bank migration (P11): a fresh bank is created; migrating an older
  sqlite-memory format bank is deferred (D11).
- **Ranking is not identical across CPU architectures.** The bundled model is u8s8-quantized, and
  ONNX Runtime evaluates that with different instructions on AVX512-VNNI hosts, on other x64 hosts,
  and on arm64 — different arithmetic, not different rounding. Query embeddings differ in the third
  decimal place, so `memory_search` can order results differently on different machines. A bank
  embedded and queried on one machine is self-consistent; the case to know about is a bank moved
  between machines by `memory_sync`, where stored vectors and query vectors come from two
  implementations of the same model. Content remains reachable — the keyword leg is unaffected and
  hybrid fusion usually rescues a reordering — but exact rank is not portable. Measured and
  accepted, with the rejected remedies costed, in
  [ADR-0049](../adr/0049-embeddings-depend-on-the-host-cpu.md).
