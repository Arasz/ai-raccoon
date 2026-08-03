# language: en
Feature: Native memory store (ai-raccoon MCP server)
    As the ai-raccoon maintainers
    I want the agent memory layer implemented natively in .NET on SQLite + FTS5 + sqlite-vec, with pluggable embeddings and our own single-file sync
    So that the embedding provider is no longer hardcoded, the bank is one self-describing file, and no sqliteai native extension bounds the server

    Background:
        Given the ai-raccoon MCP server is running
        And the memory bank is a single file memory.db
        And a project with id "acme-web" exists

    @FR-NM-1 @AC-1
    Rule: The bank is one self-describing SQLite file; all metadata lives inside it
        Scenario: The bank opens exactly one database file
            When I inspect the bank directory
            Then it contains memory.db
            And it does not contain raccoon_meta.db
        Scenario: Entry metadata is stored on the entry row
            Given an entry exists in project "acme-web"
            When I query the entries table
            Then access_count, last_accessed_at, rating, ttl_days and agent_id are columns of that row
        Scenario: Workspace lifecycle rows live in the bank
            When I call memory_workspace_begin for project "acme-web"
            Then a workspaces row with status "Active" exists in memory.db

    @FR-NM-2 @AC-2
    Rule: The memory surface is trust-first — read-only by default, rw and destructive access gated by configuration
        Scenario: A project without rw permission can read but cannot write
            Given rw is not enabled for project "acme-web"
            When I call memory_write for project "acme-web"
            Then the tool errors with access-denied
            And memory_search for project "acme-web" still returns results
        Scenario: rw is enabled per project
            Given rw is enabled for project "acme-web" in configuration
            When I call memory_write for project "acme-web"
            Then the entry is stored
        Scenario: rw is enabled globally
            Given rw is enabled globally in configuration
            And a project with id "other-app" exists
            When I call memory_write for project "other-app"
            Then the entry is stored
        Scenario: Destructive tools are gated the same way
            Given rw is not enabled for project "acme-web"
            When I call memory_delete with a known hash
            Then the tool errors with access-denied
            And the entry is not deleted
        @deferred
        Scenario: The memory inspects itself through memory_inspect
        # Part 2: introspection tool exposing schema, engine, provider, counts, pending, workspaces, watches, sync state.
        @deferred
        Scenario: The store emits metrics and tracing for its own operations
    # Part 2: metrics and tracing so the knowledge about the memory is complete.

    @FR-NM-3 @AC-3
    Rule: Embeddings are pluggable; the default engine is the small downloaded in-process model
        Scenario: The default engine embeds locally without a sidecar
            Given the small model is downloaded to the data root
            When I call memory_configure with provider "local"
            Then writes are embedded with the local engine
            And no external server process is required
        Scenario: Any OpenAI-compatible provider replaces the default
            When I call memory_configure with provider "openai", baseUrl "http://localhost:11434" and model "nomic-embed-text"
            Then writes are embedded through that endpoint
        Scenario: Without any model configured, writes are deferred
            Given project "acme-web" has no embedding model configured
            When I write "pending note" to project "acme-web"
            Then the entry is stored but not indexed
            And memory_stats reports one pending entry
        Scenario: Deferred entries are embedded after configuration
            Given project "acme-web" has one pending entry
            When I call memory_configure with provider "local"
            And I call memory_embed_pending
            Then memory_stats reports zero pending entries
            And the entry is searchable
        Scenario: Changing the engine re-embeds the bank
            Given project "acme-web" has embedded entries
            When I call memory_configure with a different provider
            Then every embedded entry is re-embedded with the new engine

    @FR-NM-4 @AC-4
    Rule: Hybrid search fuses FTS5 and vectors with reciprocal rank fusion
        Scenario: Search returns ranked results with the preserved contract
            When I search for "project knowledge" in project "acme-web"
            Then results carry hash, seq, ranking, path and snippet
            And ranking is normalized into 0..1
        Scenario: A keyword-only query with no vector hits still returns keyword results
            Given content that only matches the keyword query exists in project "acme-web"
            When I search for that exact keyword phrase
            Then keyword results are returned above the minimum score
            And no error is raised
        Scenario: Fusion parameters are configurable
            When I call memory_search with rrf_k 30 and weights 2:1
            Then the ranking reflects the configured parameters
        Scenario: Scope selection is unchanged
            Given a project-only fact exists in project "acme-web"
            When I search for that fact with scope "shared"
            Then no results are returned

    @FR-NM-5 @AC-5
    Rule: The swap is gated by a golden-retrieval harness
        Scenario: The harness passes before the pinned extension is removed
            Given a fixed corpus and a graded query set exist
            When I run the retrieval parity harness
            Then nDCG parity with the reference extension is within 0.02
            And degenerate queries show no regression
            And p95 latency stays within budget

    @FR-NM-6 @AC-6
    Rule: Workspaces are first-class entities with structural isolation
        Scenario: A new workspace is an Active row in the bank
            When I call memory_workspace_begin for project "acme-web"
            Then a workspace id is returned
            And its workspaces row has status "Active"
        Scenario: A write is either committed or in exactly one workspace
            Given workspace "ws-1" exists for project "acme-web"
            When I write "draft finding" to project "acme-web" with workspace "ws-1"
            Then the entry row has workspace_id "ws-1"
            And the schema forbids a row that has both a workspace_id and a committed scope
        Scenario: Consolidation promotes, discards and closes in one transaction
            Given workspace "ws-1" contains entries "h1" and "h2"
            When I call memory_workspace_consolidate with keep=["h1"]
            Then "h1" is committed to project "acme-web"
            And "h2" is deleted
            And the workspace row has status "Closed"
        Scenario: Sync and sweep structurally exclude workspace rows
            Given workspace "ws-1" contains an entry
            When I call memory_sync
            And I call memory_sweep with dry_run false
            Then the workspace entry was never part of a sync payload
            And it was never a sweep candidate

    @FR-NM-7 @AC-7
    Rule: Content identity is a path-scoped SHA-256 with global content dedup
        Scenario: Identical content is written once
            When I write the same content twice to project "acme-web"
            Then memory_stats reports one entry
        Scenario: Sharing creates a real row under a distinct path
            Given an entry with hash "h1" exists in project "acme-web"
            When I call memory_share with hash "h1"
            Then a row with path "shared/<path>" exists in the shared scope
            And its hash differs from "h1"
        Scenario: Consolidation preserves the logical path
            Given workspace "ws-1" contains an entry with path "docs/note.md"
            When I call memory_workspace_consolidate with keep=["all"]
            Then the committed entry keeps path "docs/note.md"

    @FR-NM-8 @AC-8
    Rule: Sync transports one snapshot file to S3-compatible storage and merges rows
        Scenario: Sync without credentials errors cleanly
            When I call memory_sync without sync credentials
            Then the tool errors with sync-not-configured
        Scenario: A sync pushes a consistent snapshot with a conditional write
            Given sync credentials are configured
            When I call memory_sync for project "acme-web"
            Then a snapshot object is uploaded with If-Match
            And the snapshot passed integrity check before upload
        Scenario: A concurrent remote change triggers a merge, never a silent drop
            Given the remote snapshot changed since the last pull
            When I call memory_sync
            Then the remote rows are merged row-wise
            And no local write since the last sync is lost
        Scenario: Deletes propagate through tombstones
            Given an entry was deleted locally since the last sync
            When I call memory_sync
            Then the deletion reaches the remote
            And the entry is not resurrected by the merge
        Scenario: Workspace rows never leave the bank
            Given workspace "ws-1" contains "private scratch"
            When I call memory_sync
            Then "private scratch" is not part of any synced payload
        Scenario: Merged rows are reindexed
            Given the remote contributed new rows
            When I call memory_sync
            Then the new rows are embedded and searchable

    @FR-NM-9 @AC-9
    Rule: The MCP surface keeps tool parity, gains provider configuration, and stays free of the replaced dependencies
        Scenario: All 17 tools are still listed
            When I list available tools
            Then memory_write, memory_search, memory_list, memory_stats, memory_share, memory_delete, memory_delete_context, memory_ingest_file, memory_ingest_directory, memory_configure, memory_embed_pending, memory_workspace_begin, memory_workspace_status, memory_workspace_consolidate, memory_workspace_discard, memory_sweep and memory_sync are present
        Scenario: memory_configure accepts a base URL
            When I call memory_configure with provider "openai" and baseUrl "http://localhost:1234/v1"
            Then the provider is configured with that endpoint
        Scenario: No Semantic Kernel dependency is introduced
            When I scan project package references
            Then no Microsoft.SemanticKernel* package is present
        Scenario: No sqliteai extension is provisioned at runtime
            When I scan the bank and extension directories
            Then no sqlite-memory, sqlite-vector or sqlite-sync binary is downloaded at first run
            And FTS5 comes from the bundled SQLite

    @FR-NM-10 @AC-10
    Rule: Chunking is deterministic, token-accurate and fence-aware
        Scenario: A markdown note is split with token bounds and overlay
            When I ingest a markdown note longer than max_tokens
            Then chunks respect max_tokens with the configured overlay
            And chunking is deterministic for identical input
        Scenario: Code fences are not split
            Given a note containing a fenced code block
            When I ingest it
            Then no chunk boundary falls inside the fence

    @deferred
    Rule: File watching is a separate part-2 feature
        @deferred
        Scenario: Watcher tools exist
# Part 2: memory_watch_add / memory_watch_status / memory_watch_remove with a persisted watches table.
