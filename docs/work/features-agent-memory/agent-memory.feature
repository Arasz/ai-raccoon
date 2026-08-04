# language: en
Feature: Agent memory management (ai-raccoon MCP server)
    As an AI agent working across projects
    I want a persistent memory bank shared across my projects
    So that I can recall durable knowledge, sandbox worktree notes, and promote what matters

    Background:
        Given the ai-raccoon MCP server is running
        And a project with id "acme-web" exists

@FR-MEM-1.1 @AC-1
    Rule: The MCP surface exposes the memory tools and guides on both transports
        @ignore
        Scenario: Tools are listed over the stdio transport
            Given the server runs with the default stdio transport
            When I list available tools
            Then memory_write, memory_search, memory_list, memory_stats, memory_share are present
            And memory_workspace_begin, memory_workspace_status, memory_workspace_consolidate, memory_workspace_discard are present
            And memory_sweep and memory_sync are present
        Scenario: The usage prompts are listed
            When I list available prompts
            Then memory-usage-guide is present
            And workspace-consolidation-guide is present

    @FR-MEM-1.2 @FR-MEM-1.3 @FR-MEM-1.4 @FR-MEM-1.21 @AC-2
    Rule: One memory bank per install scope; projects partition it, and a shared context spans them
        Scenario: Every tool requires a project id
            When I call memory_write without a project_id
            Then the tool errors with invalid-params
            And no memory is written
        Scenario: Two projects keep separate partitions in one bank
            Given a second project with id "other-app" exists
            When I write "acme secret" to project "acme-web"
            And I search for "acme secret" in project "other-app" with scope "project"
            Then no results are returned
        Scenario: Shared memory is promoted, then visible from any project
            When I write "ci convention" to project "acme-web"
            And I promote it to the shared scope
            And I search for "ci convention" in project "other-app" with scope "all"
            Then one result is returned
            And when I search for "ci convention" in project "other-app" with scope "project"
            Then the result is not returned
        Scenario: A project write is not shared until promoted
            When I write "acme secret" to project "acme-web"
            And I search for "acme secret" in project "other-app" with scope "all"
            Then no results are returned

    @FR-MEM-1.8 @FR-MEM-1.9 @FR-MEM-1.10 @AC-4
    Rule: Memory is written, searched and deleted through the hybrid index
        Scenario: Text written to the project is searchable
            When I write "SQLite memory stores project knowledge" to project "acme-web"
            And I search for "project knowledge" in project "acme-web"
            Then one result is returned with ranking above the minimum score
        Scenario: Duplicate content is written once
            When I write the same content twice to project "acme-web"
            Then memory_stats reports one entry
        @ignore
        Scenario: A context restricts search
            Given content "docs note" is written with context "docs:api"
            When I search for "docs note" restricted to context "docs:api"
            Then one result is returned
            And when I search for "docs note" restricted to context "project:acme-web"
            Then the result is not returned
        Scenario: Deleting a hash removes the entry
            Given a memory entry with a known hash exists
            When I delete that hash
            Then memory_stats reports the entry is gone

    @FR-MEM-1.5 @FR-MEM-1.6 @FR-MEM-1.7 @AC-3
    Rule: A workspace sandbox keeps worktree writes out of committed memory
        @ignore
        Scenario: A new workspace returns a workspace id
            When I call memory_workspace_begin for project "acme-web" with agent "agent-a"
            Then a workspace id is returned
            And its context is "workspace:<workspace-id>"
        Scenario: Writes with a workspace id stay in the workspace
            Given a workspace "ws-1" exists for project "acme-web"
            When I write "draft finding" to project "acme-web" with workspace "ws-1"
            Then memory_stats for project "acme-web" without workspace shows zero draft entries
            And the entry is listed by memory_workspace_status for "ws-1"
        @ignore
        Scenario: Search spans project and workspace when the workspace is named
            Given project "acme-web" contains "committed fact"
            And workspace "ws-1" contains "draft finding"
            When I search for "finding" in project "acme-web" with workspace "ws-1"
            Then both the committed fact and the draft finding are returned
        @ignore
        Scenario: Consolidation promotes the kept hashes and deletes the rest
            Given workspace "ws-1" for project "acme-web" contains entries with hashes "h1" and "h2"
            When I call memory_workspace_consolidate with keep=["h1"]
            Then "h1" is searchable in the project context
            And workspace "ws-1" no longer lists "h1" or "h2"
            And memory_stats for project "acme-web" without workspace reports exactly one new entry
        Scenario: A workspace can be discarded without promoting anything
            Given workspace "ws-2" for project "acme-web" contains an entry
            When I call memory_workspace_discard for "ws-2"
            Then memory_workspace_status for "ws-2" returns zero entries
            And memory_stats for project "acme-web" is unchanged

    @FR-MEM-1.11 @FR-MEM-1.12 @AC-5 @ignore
    Rule: Embedding configuration is per memory bank, local-first, remotely optional
        Scenario: The local GGUF model is configured once and reused
            When I call memory_configure with provider "local" and model "/models/nomic.gguf" for project "acme-web"
            Then writes to project "acme-web" are embedded with the local engine
        Scenario: Without a model, writes are deferred until embeddings are configured
            Given project "acme-web" has no embedding model configured
            When I write "pending note" to project "acme-web"
            Then the entry is stored but indexed=false
            And memory_stats reports one pending entry
        Scenario: Deferred entries are embedded after configuration
            Given project "acme-web" has one pending entry
            When I call memory_configure with provider "local" and a model path
            And I call memory_embed_pending
            Then memory_stats reports zero pending entries
            And the entry is searchable

    @FR-MEM-1.13 @FR-MEM-1.14 @AC-6 @ignore
    Rule: Extensions observe memory operations through a hook pipeline
        Scenario: Registered extensions run their hooks in order
            Given two extensions are registered
            When I write an entry
            Then the first extension's OnWrite ran before the second's
        Scenario: Search hits raise a memory's access count and rating
            Given an entry that has been searched twice
            Then its access count in the meta store is two
            And its rating is higher than an entry never searched

    @FR-MEM-1.15 @AC-7
    Rule: Degradation removes only low-rated, aged memories
        @ignore
        Scenario: A dry run lists candidates without deleting
            Given an entry rated below threshold and older than the TTL exists
            When I call memory_sweep with dry_run=true
            Then the entry is listed as a candidate
            And memory_stats still reports the entry
        @ignore
        Scenario: A real sweep deletes exactly the candidates
            Given an entry rated below threshold and older than the TTL exists
            And an entry rated above threshold exists
            When I call memory_sweep with dry_run=false
            Then the low-rated aged entry is deleted
            And the highly-rated entry survives
        @ignore
        Scenario: Shared entries are protected from the sweep
            Given a shared entry rated below threshold and older than the TTL exists
            When I call memory_sweep with dry_run=false
            Then the shared entry is not deleted

    @FR-MEM-1.16 @FR-MEM-1.22 @AC-8 @ignore
    Rule: Cloud sync is opt-in, carries committed bank contexts only, and is the correlation point between installs
        Scenario: Sync without credentials errors cleanly
            When I call memory_sync for project "acme-web" without cloud credentials
            Then the tool errors with sync-not-configured
        Scenario: Sync exchanges committed contexts, never workspace scratch
            Given cloud credentials are configured
            And workspace "ws-1" contains "private scratch"
            When I call memory_sync for project "acme-web"
            Then committed project and shared entries are sent and received
            And "private scratch" is never part of the synced payload
        Scenario: A local-only install syncs its bank to the cloud database
            Given the tool is installed in project scope with no user-scope instance
            And a shared entry exists in the local bank
            When I call memory_sync for project "acme-web"
            Then the local bank is synced to the configured cloud database
            And the shared entry is included in the synced payload
            And any user-scope instance correlates with it only through that cloud database

    @FR-MEM-1.20 @AC-10
    Rule: Credentials never appear in the repository
        Scenario: No secrets are tracked
            When I scan tracked files for cloud or embedding keys
            Then only environment-variable references are found

    @OQ-4
    Rule: Consolidation could later become a multi-round-trip request
        @ignore
        Scenario: The server asks the agent which hashes to keep via MRTR
    # Fallback for V1: plain tool call with an explicit keep list, as specified above.

    @OQ-5
    Rule: A single cloud memory bank could serve all agents
        @ignore
        Scenario: Project isolation is enforced on the cloud side via row-level security
# Fallback for V1: the cloud database is the correlation point; committed contexts
# (shared + project:<id>) sync into it, and global/local installs merge through it.
