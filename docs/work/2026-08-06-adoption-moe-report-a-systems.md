I have everything I need. All evidence is gathered: full tool surface read (19 MCP tools + 2 prompts), existing validation patterns, share/store internals, the skill, the prompts, the grade telemetry (12 lines), the audit findings, and repo invariants from the development skill. Producing the report now.

---

# MoE Report A — systems/architecture: maximizing AiRaccoon adoption (search + shared tier)

Evidence base: read `docs/work/2026-08-06-memory-usage-audit.md` (F1–F16, adoption levers), `src/AiRaccoon/Tools/MemoryTools.cs` + `WatchTools.cs` (full 19-tool surface), `SearchQuery.cs` + `MemoryWriteRequest.cs` validators, `SqliteMemoryStore.ShareAsync` (shared-row mechanics), `ContextNaming.cs`, `MemoryPrompts.cs`, the ai-raccoon-memory skill (v0.1.0), and `~/.ai-badger/memory-grade/memory-quality.jsonl` (12 lines). Ground state: 2,696 entries / 3 projects / 0 shared rows / 5 organic writes / 2 graded organic searches scoring 4/5 and 5/5 / watcher healthy / all sweep-sync-encryption at defaults.

Core systems judgment up front: the retrieval quality is proven when used (graded 4/5 and 5/5), so the adoption gap is trigger + empty-tier, not quality. The two highest-leverage server changes are (a) a shared-extraction surface that converts the 5 organic facts + watch corpus into a seeded shared tier, and (b) a hardened input contract that makes agent calls fail with actionable typed errors instead of silent misbehavior or untyped 500s. Both are designed below; everything else is orchestration.

## 1. Recommendations to maximize adoption

R1. Seed the shared tier operationally — today, before any code lands. Promote the genuinely cross-project organic facts that exist in the bank: the "1.0.4 global tool ships the fixed multi-RID shell" fact (entry id 986, ai-badger) and the "REGRESSION: watch tools not on MCP surface" note (id 2577, ai-raccoon) via memory_share. Impact: HIGH — the shared tier is a network-effect feature; zero rows means every scope=shared search returns nothing and agents learn the tier is worthless. Effort: S (two tool calls). Evidence: MEASURED (audit F2, F4).

R2. Ship the server-side shared-extraction task (`memory_share_extract`, owner requirement — full design in section 2). Impact: HIGH — turns the measured 5 organic writes + 2,644 ingest rows into a reviewable promotion pipeline and gives agents a concrete cold-start ritual ("shared tier empty → run memory_share_extract"). Effort: M (one PR). Evidence: MEASURED (F2 cold start) + INFERRED demand.

R3. Add a one-call memory brief surface (audit lever 1). A `memory_brief` tool (or prompt) returning top-recent + project-relevant entries in one call removes the 2–3-call cost of "search first" — the measured blocker. Impact: MED-HIGH. Effort: M. Evidence: INFERRED (audit lever 1; the 2–3 formulation ritual in the skill is the friction).

R4. Move memory into the default agent loop (audit lever 3): a Phase-0 "check project memory" step in the ai-badger task skill and a memory_search recommendation in the mcp-index per-turn hook, instead of optional .hermes.md prose. Impact: HIGH — the measured gap is habit/trigger (11 searches, mostly probes), not quality. Effort: S–M (ai-badger repo, not this one). Evidence: INFERRED + MEASURED (F3).

R5. Input-parameter validation hardening (owner requirement — section 3). Impact: MED — prevents the measured delete_context('shared') data-loss path, kills silent custom-context invisibility, and converts untyped store exceptions into typed McpException codes agents can act on. Effort: M (one PR). Evidence: READ (code audit) + MEASURED (F2 demonstrated the shared-wipe).

R6. Close the grade-loop gaps: raise grade rate (2/12 lines graded), verify whether Claude Code capture fires at all (no host=claude lines), and keep the ask-after-search loop as the quality feedback mechanism. Impact: MED. Effort: S. Evidence: MEASURED (F3).

R7. Settle access_count semantics (search-hit bump or not — audit "still open") and wire rating/access into the extraction signals and sweep. This directly feeds R2's criteria quality. Impact: MED. Effort: S. Evidence: MEASURED (F9, open item).

R8. KPI visibility: add sharedCount and search/share counts to memory_stats output and ship a tiny grade-log report script (searches/window, graded %, per-project). The audit's KPI is search+share per window; make it readable in one call. Impact: LOW–MED. Effort: S. Evidence: MEASURED (F3).

R9. Do NOT chase structure vectors (heading_path/structure_embedding, 0 bank-wide) until section-targeted query demand shows up — an explicit out-of-scope decision already exists (audit F8 updated). Impact: LOW. Effort: L. Evidence: MEASURED.

R10. Doc-level note only: the tool default minScore=0.7 is measured-inert at k=60 (ADR 0006 sweep); do not change defaults without a gate sweep. Impact: LOW. Effort: S. Evidence: MEASURED (ADR 0006).

## 2. Design: shared-extraction integration task (`memory_share_extract`)

Contract (tool layer, thin; logic in Core):

- Name: `memory_share_extract`. Params: `projectIds` (string[], required, 1..8 ids), `mode` ("propose" default | "promote"), `limit` (1..50, default 20), `includeTtlRows` (bool, default false), `sourceTypes` ("project" default | "project+custom"). CancellationToken standard.
- Returns `ShareExtractResult { Candidates: ShareCandidate[], Promoted: ShareResult[] }`. `ShareCandidate(Hash, Path, ValuePreview, Context, Rating, AccessCount, CreatedAt, Reasons[])`. In propose mode `Promoted` is empty; in promote mode, candidates that were already shared (dedup) are omitted from `Promoted` and their reason carries "already-shared".
- Access tiers: propose = AccessRequirement.Read; promote = AccessRequirement.Write (parity with memory_share, which is Write). Each projectId in the array is individually access-guarded via the existing RequireAsync. Promote writes only to the shared tier — it never mutates the source project's rows.
- Observability: standard ToolExecutionActivity wrapper + RecordInvocation/RecordError; tool name const TnMemoryShareExtract; inventory tests updated (MemoryTools count 16→17, name-parity const test).

Candidate selection (Core service `SharedExtractionService`, SQL in Infrastructure store):

- Pool: rows with context = `project:<id>` (never shared, never `workspace:` outbox rows, never `label:` rows unless sourceTypes opts in), embed_state=embedded (skip pending), no ttl_days unless includeTtlRows (TTL rows are ephemeral by design; promoting makes them sweep-exempt forever — flag them in reasons, exclude by default).
- Cross-project relevance signals (mechanical only — no LLM dependency, the server has no in-process judge; embeddings are the bundled ONNX model): (a) organic write — source_file null/empty and agent_id set (the audit's 5 facts all match); (b) cross-project reference — value or source_file mentions another known project id or a path under another project root (e.g. `/RiderProjects/<other>/`); (c) usage signal — access_count > 0 or rating > 0.5 (the 268 once-accessed rows). Ranking score: organic-write +2, cross-project-reference +2, usage signal +1, recency (created_at within 30d) +0.5. Everything scores ≥1 reason or it is excluded.
- Dedup vs existing shared rows (three exact checks, in order): (1) value equality after whitespace normalization against any scope='shared' row; (2) path equality — ShareAsync materializes shared rows as `shared/<source.Path>`, so a candidate whose `shared/<path>` row exists is a duplicate; (3) the candidate itself is already shared (impossible by construction, asserted). Embedding-similarity dedup is explicitly v2 (would require vector-querying the shared tier per candidate; over-engineering for a tier that starts at 0 rows).
- Promote flow: reuses the existing `ShareAsync` path (SqliteMemoryStore.AddContentAsync is idempotent — re-promotion finds the existing shared row; this is the documented FR-NM-7 path-scoped-hash behavior). No new delete path is introduced anywhere in this feature.
- Batch/background: v1 is on-demand only (ask-if-simpler — the invocation is the caller's loop: propose → owner reviews → promote). A cross-project operator batch via CLI verb `ai-raccoon extract` (user-run, no access-tier checks, mirroring config verbs) is the documented stretch, NOT in v1. A hosted background service is explicitly rejected until telemetry shows demand.

Safety invariants (non-negotiable):

- The tool never calls, and cannot trigger, any delete path — in particular never DeleteContextAsync with context='shared' (measured F2: wipes the entire tier). Promote = insert-only, idempotent.
- "memory_share never automatic" guidance is preserved: propose is the default mode and the only mode the skill/prompt advertises; promote is a single-purpose explicit call made after reviewing a bounded, ranked list.
- Degradation interplay: promoted rows become sweep-exempt (shared tier contract). The candidate record therefore surfaces rating and access_count so the owner sees they are immortalizing a row that sweep would otherwise have removed; low-rated rows are not excluded, just visible.
- Cold-start: with an empty tier, propose returns the ~5 organic facts (measured) plus cross-project-referencing watch/ingest rows — the exact seed set from R1, now reproducible by any agent.

Tests (TDD, Unit/Share + Unit/Mcp + inventory):

- Selection: organic-write and cross-project-reference candidates found; label/workspace/shared rows excluded; pending rows excluded; TTL rows excluded by default and flagged when included; ranking order pinned.
- Dedup: value-duplicate and path-duplicate candidates excluded from promote; re-promote idempotent (promoted count 0 on second run); source row untouched after promote (assert count and content).
- Access: propose denied at rw tier? no — propose=Read allowed; promote denied below Write tier (AccessModeGuard test pattern); per-projectId guard with mixed tiers in the array.
- Cold-start: empty shared tier → propose returns candidates; empty bank → empty result, no throw.
- Tool tests: invalid projectIds (empty array, >8), mode enum, limit bounds, inventory 16→17, name const parity.
- Never-deletes assertion: after a full propose+promote cycle, bank row count delta = promoted only; delete_context never invoked (fake store records calls).

## 3. Validation-hardening matrix (19-tool surface + prompts)

Where validation lives (no new abstraction): value-domain rules (ranges, lengths, formats) in the Core FluentValidation record validators (existing pattern: SearchQuery.Validator, MemoryWriteRequest.Validator); MCP-shape and safety rules (scope parse, reserved-context protection, absolute paths, store-exception mapping) in the tool layer as typed McpException codes following the existing 'watching-disabled'/'path-outside-scope' pattern (inner typed catch + activity.RecordError + outer `is not McpException` filter); one small internal static `ToolArgs` helper class in Tools/ for hash/path/context checks shared by MemoryTools and WatchTools (justified: two consumers, identical rules — not an abstraction before a caller).

Cross-cutting rules:

- project_id: currently required-only (RequireProjectId). Add: max 128 chars, no whitespace/control chars. Existing ids (ai-raccoon, job-search-ai-assistant, ai-badger, tool-test-20260806) all comply; charset stays permissive (alnum, dot, dash, underscore) to avoid breaking agents. Code: 'invalid-params'.
- hash (memory_share, memory_delete): currently ThrowIfNullOrWhiteSpace only. Add: format ^[0-9a-f]{64}$ → 'invalid-hash'; map the store's InvalidOperationException ("No entry with hash...") → 'hash-not-found' (typed catch in tool). Workspace consolidate keep[] accepts 'all' sentinel — format check must allow it.
- Absolute paths (memory_ingest_file, memory_ingest_directory, memory_watch_add, memory_watch_remove): currently empty-check only; WatchPath resolves relative paths against server CWD, which is wrong by construction. Add Path.IsPathFullyQualified + rooted check at tool layer → 'path-not-absolute' (watch_add keeps its existing 'watching-disabled'/'path-outside-scope'/'path-not-found' mapping after it).
- limit bounds: memory_search limit 1..100 (currently >0 only — a 10,000-limit call would dump the bank into agent context); rrfK 1..500; fts/vectorWeight 0..100; memory_embed_pending limit ≥1 when set; memory_share_extract limit 1..50.
- scope enum (memory_search): free string is fine for MCP ergonomics, but the current error has no code prefix (inconsistent with RequireProjectId's 'invalid-params:'). Add typed 'invalid-scope' with the same all/project/shared message.

Per-tool rules:

- memory_write: content length cap 64KB → 'content-too-large' (single-entry writes are not chunked); agentId ≤128; sourceFile ≤1024 (exists); section ≤256 (exists); NEW safety rule: reserved context names ('shared', 'project:', 'workspace:', 'label:') rejected in the context param → 'reserved-context'. Rationale: the skill documents that any non-default context silently becomes scope='custom' (invisible to project search), and the tool code shows context is a free string — whether 'shared' maps into the shared tier is an implement-time verification (pin the FilterFor mapping with a test), but the reserved-name block is cheap insurance against both the invisibility trap and a direct-write-to-shared bypass of curation.
- memory_search: contextLabel only valid with scope=project → 'context-label-requires-project-scope' (currently silently ignored in scope=all — the worst kind of failure for an agent that thinks it filtered); minScore 0..1 (exists); contextLabel ≤256 (exists).
- memory_share / memory_delete: hash rules above.
- memory_delete_context: BLOCK context='shared' → 'shared-context-protected' (measured F2: deletes every shared row bank-wide; this is the single most dangerous call in the surface and currently unprotected). Allow project:/workspace:/label: forms as today.
- memory_ingest_file / memory_ingest_directory: absolute-path rule; existence errors mapped from the store to 'path-not-found' (mirror watch_add's mapping).
- memory_workspace_begin: agentId ≤128, name ≤128.
- memory_workspace_status / consolidate / discard: workspaceId format (non-empty exists; add ≤128, no whitespace) + map store not-found to 'workspace-not-found' (mirror the watch mapping pattern — implement-time check of WorkspaceService's exception type); consolidate keep[] non-empty, hashes valid or exactly ['all'].
- memory_sweep / memory_sync / memory_list / memory_stats / memory_watch_status: no changes (sync already has the full typed family: sync-not-configured, sync-auth-failed, sync-conflict, sync-network, sync-corrupt-file).

Test strategy: parametrized xunit theories per rule in the existing Unit/Mcp/MemoryToolsTests + MemoryToolsAccessModeTests patterns (xunit v3, TestContext.Current.CancellationToken, TreatWarningsAsErrors); per-validator unit tests in Unit/Memory; store-mapping tests assert the McpException code string; one E2E scenario for delete_context('shared') protection and one for reserved-context write rejection; inventory tests unchanged (no new tools in this PR).

## 4. Prioritized work packages (TDD-ready, one PR per task)

WP1 — memory_share_extract (section 2). Files: new Core/Memory/ShareExtractRequest.cs + Core/Memory/SharedExtractionService.cs (criteria + ranking, pure/testable), Infrastructure/Sqlite candidate-selection SQL + reuse of ShareAsync/AddContentAsync, Tools/MemoryTools.cs (tool + const + result records), inventory tests 16→17 + name parity, Unit/Share + Unit/Mcp tests, docs/plans/ shared-extraction plan + dated docs/work/ record, MemoryPrompts.cs memory-usage-guide mention ("shared tier empty → memory_share_extract(propose)"). Gate: full suite + script gates. Est. M.

WP2 — Validation hardening (section 3). Files: Tools/ToolArgs.cs helper, MemoryTools.cs + WatchTools.cs rule additions, Core validator extensions (SearchQuery, MemoryWriteRequest), store exception mapping, Unit/Mcp parametrized tests + Unit/Memory validator tests + E2E protection scenarios. No tool-count change. Gate: full suite. Est. M.

WP3 — Operational seeding run (zero code): after WP1 lands, run memory_share_extract(propose) over all 3 projects, owner reviews, promote the seed set (or promote the two R1 facts directly with memory_share if WP1 is not yet merged). Record in docs/work/. Est. S.

WP4 — memory_brief one-call surface (R3): Core brief service (top-recent per project + top-rated + pending count), tool + prompt, skill update, inventory 17→18. Est. M.

WP5 — Adoption-loop changes in the ai-badger repo (separate PRs, different repo): task-skill Phase 0 memory-check step; mcp-index hook mention; ai-raccoon-memory skill v0.1.1 (extract ritual, brief, reserved-context pitfall). Est. S–M.

WP6 — Telemetry/KPI (R8): memory_stats gains sharedCount + recent-search/share counts; scripts/memory-grade-report.py (searches/window, graded %, per project/host). Est. S.

WP7 — Deferred: access_count semantics settlement (R7) then rating/access wiring into extraction signals (small follow-up to WP1); CLI verb `ai-raccoon extract` operator batch (stretch, only if WP1 usage shows need); structure-vector population (R9, explicitly deferred).

Sequencing note: WP1 and WP2 both touch MemoryTools.cs and the inventory tests — run them as separate sequential PRs (one PR per task invariant), WP1 first (new feature), WP2 second (hardening touches the new tool's params too). WP3 can run the moment WP1 lands and does not wait for WP2.

---

**What I did:** Read the audit + fixes-plan context, the complete 19-tool surface (MemoryTools.cs 16 tools, WatchTools.cs 3 tools; the 21-tool count = +2 standard prompt tools), the existing FluentValidation rules, ShareAsync/AddContentAsync shared-row mechanics, ContextNaming, both server prompts, the ai-raccoon-memory skill, and the 12-line grade telemetry; loaded the ai-raccoon-development skill for repo invariants (tool-inventory tests, ToolExecutionActivity pattern, single config channel, typed-exception conventions, TDD/worktree/one-PR rules).

**Files created/modified:** none (report-only task, per instructions).

**Issues encountered:** one grep timed out on a huge directory scan (re-scoped to src/); no blockers. Two implement-time verifications flagged in the design: (a) whether a memory_write context='shared' maps into the shared tier (reserved-context rule is safe either way), (b) WorkspaceService's not-found exception type for the workspaceId mapping.