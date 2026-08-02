# language: en
Feature: Agent memory management (ai-raccon MCP server)
  As an AI agent working across projects
  I want a persistent, project-scoped memory exposed through MCP tools
  So that I can recall durable knowledge, sandbox worktree notes, and promote what matters

  Background:
    Given the ai-raccon MCP server is running
    And a project with id "acme-web" exists

  @FR-MEM-1.1 @AC-1
  Rule: The MCP surface exposes the memory tools and guides on both transports
    Scenario: Tools are listed over the stdio transport
      Given the server runs with the default stdio transport
      When I list available tools
      Then memory_write, memory_search, memory_list, memory_stats are present
      And memory_workspace_begin, memory_workspace_status, memory_workspace_consolidate, memory_workspace_discard are present
      And memory_sweep and memory_sync are present
    Scenario: The usage prompts are listed
      When I list available prompts
      Then memory-usage-guide is present
      And workspace-consolidation-guide is present

  @FR-MEM-1.2 @FR-MEM-1.3 @FR-MEM-1.4 @AC-2
  Rule: Memory is scoped to a project, and projects never share data
    Scenario: Every tool requires a project id
      When I call memory_write without a project_id
      Then the tool errors with invalid-params
      And no memory is written
    Scenario: Two projects keep separate databases
      Given a second project with id "other-app" exists
      When I write "acme secret" to project "acme-web"
      And I search for "acme secret" in project "other-app"
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
    Scenario: A new workspace returns a workspace id
      When I call memory_workspace_begin for project "acme-web" with agent "agent-a"
      Then a workspace id is returned
      And its context is "workspace:<workspace-id>"
    Scenario: Isolated writes land only in the workspace
      Given a workspace "ws-1" exists for project "acme-web"
      When I write "draft finding" to project "acme-web" with workspace "ws-1" and isolated=true
      Then memory_stats for project "acme-web" without workspace shows zero draft entries
      And the entry is listed by memory_workspace_status for "ws-1"
    Scenario: Search spans project and workspace when the workspace is named
      Given project "acme-web" contains "committed fact"
      And workspace "ws-1" contains "draft finding"
      When I search for "finding" in project "acme-web" with workspace "ws-1"
      Then both the committed fact and the draft finding are returned
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

  @FR-MEM-1.11 @FR-MEM-1.12 @AC-5
  Rule: Embedding configuration is per project, local-first, remotely optional
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

  @FR-MEM-1.13 @FR-MEM-1.14 @AC-6
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
    Scenario: A dry run lists candidates without deleting
      Given an entry rated below threshold and older than the TTL exists
      When I call memory_sweep with dry_run=true
      Then the entry is listed as a candidate
      And memory_stats still reports the entry
    Scenario: A real sweep deletes exactly the candidates
      Given an entry rated below threshold and older than the TTL exists
      And an entry rated above threshold exists
      When I call memory_sweep with dry_run=false
      Then the low-rated aged entry is deleted
      And the highly-rated entry survives

  @FR-MEM-1.16 @AC-8
  Rule: Cloud sync is opt-in and never carries workspace scratch memory
    Scenario: Sync without credentials errors cleanly
      When I call memory_sync for project "acme-web" without cloud credentials
      Then the tool errors with sync-not-configured
    Scenario: Sync exchanges committed project memory only
      Given cloud credentials are configured
      And workspace "ws-1" contains "private scratch"
      When I call memory_sync for project "acme-web"
      Then committed project entries are sent and received
      And "private scratch" is never part of the synced payload

  @FR-MEM-1.20 @AC-10
  Rule: Credentials never appear in the repository
    Scenario: No secrets are tracked
      When I scan tracked files for cloud or embedding keys
      Then only environment-variable references are found

  @OQ-4
  Rule: Consolidation could later become a multi-round-trip request
    @deferred
    Scenario: The server asks the agent which hashes to keep via MRTR
      # Fallback for V1: plain tool call with an explicit keep list, as specified above.

  @OQ-5
  Rule: A single cloud memory bank could serve all agents
    @deferred
    Scenario: Project isolation is enforced on the cloud side via row-level security
      # Fallback for V1: per-project cloud databases via sqlite-sync, as specified above.
