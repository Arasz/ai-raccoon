# Plan: memory-usage audit fixes (issue #44)

**Date:** 2026-08-06
**Source:** `docs/work/2026-08-06-memory-usage-audit.md` (11 measured findings) + issue #44. Owner directive: fix the findings; clean the jsaa corpus data that is broken ("until 1.0.7 we had critical bugs"); verify; regenerate the audit report as v2.

## Scope

| # | Fix | Kind | Gate |
|---|---|---|---|
| P1 | File-path watches work (mcp-tools.json bug) | Code, TDD | Unit tests + full suite |
| P2 | Watch digest embeds after ingest (pending rows) | Code, TDD | Unit tests + full suite |
| P3 | Ingest script: drop deleted `memory_configure`, fix reset to project scope, drop `agent_id` provenance, re-pin jsaa commit | Script | `--dry-run` + real run, hash contract |
| P4 | Ops cleanup: embed backfill, ghost config, tool-test residue | Ops | memory_stats / settings / watch queries |
| P5 | jsaa re-ingest with fixed script | Ops | source_file populated, 0 pending, spot-checks |
| P6 | Manual check + v2 report | Ops | Live probes + re-rendered HTML |

Code changes ship as ONE PR (P1+P2+P3, same task); P4–P6 are data operations on the live bank.

## P1 — WatchEventSource supports file paths [code]

**Problem (audit F7):** `WatchEventSource.Start` does `new FileSystemWatcher(normalized)` on the registered path (`src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs:45`). `FileSystemWatcher` requires a directory; a file registration (e.g. `ai-raccoon/.ai-badger/mcp-tools.json`) throws `ArgumentException: The directory name '...' does not exist` → recurring "Watch event source error" in mcp-stderr, yet the registration reports Healthy.

**Design:** for a path that is a FILE (exists, not a directory):
- watch `Path.GetDirectoryName(path)` with `Filter = Path.GetFileName(path)`, `IncludeSubdirectories = false`;
- translate only events whose normalized `FullPath` (or `OldFullPath` on rename) equals the registered file path;
- **rename-away edge (review F3):** when `OldFullPath == registered` and `FullPath != registered`, translate as a `Deleted` event for the registered path (not Renamed) — otherwise `DigestAsync` ingests the file the name moved to, outside the registration; rename-in (temp → registered) stays `Renamed`;
- keep the dictionary key `(projectId, registeredPath)` and `IsWatching`/`Stop` semantics unchanged; missing file at Start → watch the parent anyway (file may be created later);
- no Start-time validation error event for file-mode registrations (missing file included); OS-level `Error` events (buffer overflow) still surface via `HandleError` (review F7);
- Directory behavior unchanged.

**Tests (extend `tests/AiRaccoon.Tests/Unit/Watch/WatchEventSourceTests.cs`, real temp dirs):** Start on a file → created/changed/deleted/renamed events fire for that file; sibling files in the same dir produce no events; Start on a file produces no `WatchEventError`; rename-away produces `Deleted` for the registered path, rename-in produces `Renamed`; directory watch regression tests still pass. Live-watcher assertions need a bounded wait helper (TCS/SpinWait, ~2 s timeout — FileSystemWatcher events are async; review F4), keeping `[Trait(Speed, Fast)]`.

## P2 — Watch digest embeds after ingest [code]

**Problem (audit F6):** the only 2 pending rows in the bank are watcher ingests (ai-badger docs, 2026-08-06 10:13). Mechanism (review F2): `InsertChunksAsync` embeds per chunk via `EmbedIfConfiguredAsync` when a provider is configured — watch ingests DO embed normally; a row is left pending only when no provider is configured at ingest time or the inline embed throws after row insert. Nothing retries a row left pending. The digest then fails/retries, and the row stays FTS-only until a manual `memory_embed_pending`.

**Design:** after a successful `IngestFileAsync` in `WatchDigestExecutor.DigestAsync`, call `store.EmbedPendingAsync(projectId, null, ct)` **best-effort** — the retry net for rows left pending (embed failure is logged, never breaks the digest; no provider configured → `EmbedPendingResult(0, n)` is a no-op). Note: `EmbedPendingAsync` processes ALL project-pending rows, not just the ingested file — harmless, intended. This mirrors the ingest script's per-batch embed and makes the background mirror self-sufficient.

**Tests (extend `WatchDigestExecutorTests.cs`):** digest of a changed file triggers `EmbedPendingAsync` for the project; embed failure does not throw out of `DigestAsync`; hash-skip path does NOT embed; file-gone path does NOT embed (review F2). **`FakeMemoryStore.EmbedPendingAsync` currently throws `NotImplementedException` (`WatchTestFakes.cs:251-253`) — extend the fake first: record calls + settable failure.**

## P3 — ingest-jsaa-docs.py fixes [script]

**Problems (audit F8 + post-merge audit):** (a) line 1066 calls the DELETED `memory_configure` tool → fresh runs die at Phase 4; (b) `CONTEXTS_TO_DELETE` (line 281) lists custom-label contexts but the corpus is written WITHOUT `context` (all rows `scope='project'`) → `reset_contexts` deletes nothing; (c) `agent_id=chunk.structured_path` (line 875) pollutes the agent column with provenance that now belongs in `sourceFile`/`section`; (d) pin `JSAA_PINNED_COMMIT` (0bb8ff8) ≠ jsaa HEAD (9397bbef) → script aborts.

**Design:**
- delete the `memory_configure` helper + its call; replace with a preflight: after the first batch, if `embed_pending` returns `processed: 0` and `pending > 0`, log a WARNING naming the CLI fix (`ai-raccoon model set local`);
- `CONTEXTS_TO_DELETE = ["project:job-search-ai-assistant"]` (project-scoped delete; `FilterFor` for `project:` contexts is `scope = 'project' AND project_id = @projectId` — shared rows are NOT matched, the reset is jsaa-project-only by construction; the shared tier is empty anyway. The `project:` prefix is required — without it the string falls to the custom-label branch and deletes nothing);
- drop `agent_id=chunk.structured_path` (keep the `memory_write` helper param, pass None);
- re-pin `JSAA_PINNED_COMMIT` = current jsaa HEAD, verify `git -C <JSAA_ROOT> rev-parse HEAD` matches before the run (built into `verify_jsaa_pin`);
- **out of scope, stated as a decision (review F8):** `heading_path`/`structure_embedding` cannot be populated — the tool surface (`MemoryWriteRequest`) carries no heading field; a follow-up task.

**Gates:** `--dry-run` passes (note: dry-run returns before Phase 4 — it cannot catch the `memory_configure` regression, so add a static gate: `grep -rn "memory_configure" scripts/` must be zero hits, review F5); a fresh-data-root rehearsal run (scratch server) then the real run (P5) yield rows with `source_file` = structured relative path, `section` set, `agent_id` NULL, all `embedded`, hash-map 0 mismatches, spot-checks green.

## P4 — Ops cleanup [ops]

- `memory_embed_pending(ai-badger)` via MCP → the 2 pending rows embed; verify `memory_stats(ai-badger)` pending 0.
- Ghost config: `ai-raccoon watch remove CLAUDE.md` and `ai-raccoon watch remove tool-test-20260806` (removes enabled/scope/concurrency rows per the CLI contract; `CLAUDE.md` and `tool-test-20260806` have no registrations to lose).
- Tool-test residue: delete 3 `watch_files` rows for `tool-test-20260806` and the 2 closed tool-test workspaces (SQL, read-verified first; server holds the bank — coordinate with the live server; WAL-safe).
- Verify: settings table has no `watch.enabled.CLAUDE.md`, no `watch.enabled.tool-test-20260806`; `watch_files` clean; `workspaces` table keeps only `acme` test rows (feature-test artifacts, leave unless owner says otherwise — they predate this audit).

## P5 — jsaa re-ingest [ops]

Run the fixed script against the live bank (after a fresh-data-root rehearsal). Sequence: pin check → reset (`project:job-search-ai-assistant` delete) → write batches (sourceFile/section, no agent_id) → per-batch embed → final embed → stats → spot-checks. Verify post-run: `SELECT count(*), count(source_file), count(section), count(agent_id), embed_state counts` for the project; hash-map contract: `git diff --exit-code scripts/chunk-hash-map.json` (the script rewrites it at ingest-jsaa-docs.py:1048) plus `python3 scripts/run-baseline-queries.py` against the bank (review F1 — `references/baseline-hash-contract.md` and `scripts/verify-baseline-pipeline.py` do NOT exist in the repo; they are skill artifacts).

## P6 — Manual check + v2 report [ops]

Live probes through the Hermes bridge: `memory_stats` (all three projects, pending 0 everywhere), `memory_search` on a jsaa source (expect `sourceFile` populated, rank 1), `memory_watch_status` (Healthy; mcp-tools.json watch now event-source-able — verify no new "directory name does not exist" errors in mcp-stderr after the P1 build is live), shared-tier search (still empty — promotion remains owner-curated). Then update `docs/work/2026-08-06-memory-usage-audit.md` with a before/after section, re-render the HTML, re-verify the issue checklist.

## Risks & notes

- Concurrent sessions share the jsaa and ai-raccoon checkouts; jsaa HEAD can move between pin check and run — abort and re-pin if `rev-parse` mismatches at run time.
- The live server holds the bank in WAL mode; SQL cleanup must be read-verified and minimal, and the embed/delete ops go through the MCP/CLI channels where they exist.
- `memory_delete_context(projectId, "shared")` is globally destructive — never invoked; the jsaa reset uses the `project:` scope only.
- P1's file-watch change touches the watch event source used by all watches; directory-watch regression tests are the safety net.
