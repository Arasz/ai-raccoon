# Code corpus — semantic code retrieval alongside the memory bank (behavioral contract)
# Plan: docs/work/2026-08-21-code-search-implementation-plan.md (rev 3)

@bdd
Feature: Code corpus

    As a coding agent
    I want to search this repo's source the same way I search my memory
    So that I can retrieve a function or a config line without leaving the MCP surface

    # Baseline (plan §1, §3.1): a second, code-only corpus in the SAME memory.db
    # (code_entries + code_fts + vec_code float[768]), fed by the existing watch/ingest
    # machinery, reachable through memory_search kind=memory|code|both and code_get.
    # Model: faxenoff/code-daemon-embed-v1, 768-dim, chunk budget 510 (min(510, ctx-2)).

    # Explicitly OUT OF SCOPE for this contract (plan §1, §12.4 OQ5):
    # - Flipping the shipped default embedding model or chunker arm — the owner decides
    #   from the WP8 eval report; this feature never changes a default.
    # - The full multi-repo evaluation (2-3 vendored repos incl. a mandatory Python repo)
    #   — deferred pending owner approval of the repos (OQ5); scripts/.../eval-sets/
    #   code-smoke.json is a small sanity fixture only, not that eval set.

    Rule: memory_search's kind parameter selects and shapes the response envelope
        # Plan §3.6: keys are "results" (existing) and "code" (new); kind=memory
        # serializes the exact legacy shape (no "code" key at all, via
        # [JsonIgnore(Condition = WhenWritingNull)]); kind=code/both always carry both
        # keys. Default kind is "memory" — memory behavior is unchanged by default.
        Scenario: The default kind searches memory only, with the legacy envelope
            Given a project with memory entries and code chunks
            When I call memory_search for the project without a kind
            Then the response contains a "results" key with the memory hits
            And the response contains no "code" key

        Scenario: kind=code returns only the code section
            Given a project with memory entries and code chunks
            When I call memory_search for the project with kind "code"
            Then the response contains an empty "results" key
            And the response contains a "code" key with the code hits

        Scenario: kind=both returns both sections
            Given a project with memory entries and code chunks
            When I call memory_search for the project with kind "both"
            Then the response contains a "results" key with the memory hits
            And the response contains a "code" key with the code hits

        Scenario: An invalid kind is refused
            When I call memory_search for the project with kind "docs"
            Then the tool errors with invalid-params naming the allowed kinds

        Scenario: Code hits carry line ranges instead of chunk position
            Given a project with a code chunk spanning lines 10 to 24
            When I call memory_search for the project with kind "code"
            Then the code hit carries lineStart 10 and lineEnd 24

        Scenario: kind=code with a shared or workspace scope returns an empty code section
            Given a project with code chunks
            When I call memory_search for the project with kind "code" and scope "shared"
            Then the code section is empty
            # code is always project-scoped (plan §3.6); this is documented, not an error.

    Rule: code_get reads one code chunk's full source by content hash
        # Mirrors memory_get (plan §3.6).
        Scenario: code_get returns the chunk addressed by its hash
            Given a code chunk ingested with a known content hash
            When I call code_get for the project on that hash
            Then the response contains the chunk's value, path, and line range

        Scenario: code_get on an unknown hash is refused
            When I call code_get for the project on a hash nothing was ingested under
            Then the tool errors with unknown-hash

    Rule: ai-raccoon.ignore filters both ingestion pipelines the same way
        # Owner requirement (plan §2.1): one file per watch/tree root, gitignore-subset
        # syntax, no "!" negation in v1, any-match-wins. Ignored files are never
        # fingerprinted, never chunked, in EITHER corpus. Applies identically to a
        # directory watch's catch-up scan and to memory_ingest_directory.
        Scenario: A watched directory skips files matched by ai-raccoon.ignore
            Given a directory "/repo" with an "ai-raccoon.ignore" listing "*.generated.cs"
            And a file "widget.generated.cs" under "/repo"
            When I call memory_watch_add for the project on path "/repo"
            Then "widget.generated.cs" is ingested into neither corpus

        Scenario: memory_ingest_directory skips files matched by ai-raccoon.ignore
            Given a directory "/repo" with an "ai-raccoon.ignore" listing "*.generated.cs"
            And a file "widget.generated.cs" under "/repo"
            When I call memory_ingest_directory for the project on path "/repo"
            Then "widget.generated.cs" is ingested into neither corpus

        Scenario: A trailing-slash pattern ignores an entire subtree
            Given a directory "/repo" with an "ai-raccoon.ignore" listing "bin/"
            And a file "bin/Debug/app.dll" under "/repo"
            When I call memory_watch_add for the project on path "/repo"
            Then every file under "bin/" is ingested into neither corpus

        Scenario: A file ingested before the ignore line was added is cleaned up
            Given a watch for the project on path "/repo" with "widget.generated.cs" already ingested into the code corpus
            When "ai-raccoon.ignore" is edited to list "*.generated.cs"
            Then the stale chunks for "widget.generated.cs" are deleted from the code corpus
            And its last-change timestamp is updated without a new fingerprint

        Scenario: Explicit memory_ingest_file on an ignored path returns zero chunks
            Given a directory "/repo" with an "ai-raccoon.ignore" listing "*.generated.cs"
            When I call memory_ingest_file for the project on path "/repo/widget.generated.cs"
            Then the call returns zero chunks
            # Ignore wins even for an explicit single-file ingest — consistent with
            # "never fingerprinted" (plan §2.1).

        Scenario: Editing ai-raccoon.ignore triggers a full re-scan of the watch
            Given a watch for the project on path "/repo"
            When "ai-raccoon.ignore" under "/repo" is edited
            Then the watch performs a full re-scan of "/repo"

    Rule: A broader watch wins; a narrower watch inside an existing one is rejected
        # Owner requirement (plan §2.2): containment = real-path prefix, separator-aware.
        Scenario: Adding a watch on a repo root prunes a watch already inside it
            Given a watch for the project on path "/repo/docs"
            When I call memory_watch_add for the project on path "/repo"
            Then the watch on "/repo/docs" is pruned
            And a single watch exists at "/repo" covering both corpora

        Scenario: Adding a watch inside an existing broader watch is rejected
            Given a watch for the project on path "/repo"
            When I call memory_watch_add for the project on path "/repo/docs"
            Then the tool errors naming "/repo" as the covering watch
            And no new watch is written

        Scenario: Re-adding the identical path is an idempotent no-op
            Given a watch for the project on path "/repo"
            When I call memory_watch_add for the project on path "/repo" again
            Then exactly one watch exists at "/repo"

        Scenario: A sibling path is never pruned
            Given a watch for the project on path "/repo"
            When I call memory_watch_add for the project on path "/repo2"
            Then both watches exist independently

    Rule: Code files are always ingested, even with no code engine configured
        # Owner requirement / F-11 (plan §3.3): code chunking never blocks on
        # configuration — the bundled counting tokenizer chunks and stores every code
        # file as "pending"; search degrades to FTS5-only until an engine is set.
        Scenario: A code file is chunked and stored pending with no code engine active
            Given a project with no code embedding engine configured
            When a code file is ingested
            Then its chunks are stored in the code corpus with embed_state "pending"

        Scenario: kind=code search degrades to FTS5-only with a warning when unconfigured
            Given a project with no code embedding engine configured
            And code chunks stored pending
            When I call memory_search for the project with kind "code"
            Then the code section is served from full-text search only
            And the response carries a warning that vector search is unavailable

        Scenario: A configured-but-unloadable code engine refuses code search only
            Given a project whose code embedding engine manifest cannot be loaded
            When I call memory_search for the project with kind "code"
            Then the tool errors with an actionable message naming the engine problem
            And memory_search with kind "memory" for the same project still succeeds

    Rule: The code corpus's vector index accepts only 768-dimension manifests
        # Plan §3.3: vec_code is a fixed float[768] index; unlike the memory bank's
        # "model set local", code has no dimension-reconcile phase, so this is the
        # only gate protecting it — refused at configure time, before anything commits.
        Scenario: model set code local refuses a non-768-dimension manifest
            Given a local model manifest declaring 1024 embedding dimensions
            When the user runs model set code local against that manifest's directory
            Then the command errors naming the required 768 dimensions
            And no code engine setting is changed

        Scenario: model set code local accepts a 768-dimension manifest
            Given a local model manifest declaring 768 embedding dimensions
            When the user runs model set code local against that manifest's directory
            Then the code embedding engine is activated

    Rule: The code corpus never leaves the machine
        # Plan §3.7 (review B1/B2, F-22): the code corpus is a re-derivable local cache;
        # pushing it off-machine would leak source identifiers/paths. StripNonSyncableAsync
        # DROPs code_entries/code_fts/vec_code (and their shadow tables) from every pushed
        # snapshot, on every push path.
        Scenario: A local sync push contains no code tables
            Given a project with code chunks ingested
            When the project pushes a sync snapshot
            Then the pushed snapshot contains no code_entries, code_fts, or vec_code table

        Scenario: A merged sync push contains no code tables
            Given a project with code chunks ingested and a pending merge
            When the merged snapshot is pushed
            Then the pushed snapshot contains no code_entries, code_fts, or vec_code table

        Scenario: Sync pull and merge are unaffected
            Given a remote snapshot carrying only memory entries
            When the project pulls and merges that snapshot
            Then the memory entries merge exactly as before
            # The code corpus is never a merge input — pull/merge name only
            # entries/sync_tombstones/memory_source (plan §3.7).

    Rule: A code engine change drains through the code-reindex job, not the memory outbox
        # Plan §3.3 (join disposition #2): the model_migration outbox is single-row,
        # its relay hard-codes the memory query, and its ToolGate closes ALL tools
        # during a migration — three reasons a second corpus cannot share it. Instead,
        # "model set code local" invalidates pending code rows in the SAME transaction
        # as the settings write; a separate code-reindex maintenance job drains them.
        Scenario: Activating a new code engine invalidates pending code rows in one transaction
            Given a project with code chunks already embedded under the old engine
            When the user runs model set code local against a new manifest
            Then every code row's embed_state becomes pending in the same transaction as the settings change

        Scenario: Memory tools are never blocked while code rows drain
            Given a code engine change with pending rows still draining
            When memory_search is called for the project with kind "memory"
            Then the call succeeds without a migration-in-progress refusal
            # No ToolGate interaction for the code drain — memory tools are never
            # closed the way a memory model migration closes them (plan §3.3).

        Scenario: The code-reindex maintenance job re-embeds pending code rows
            Given code rows left pending by a code engine change
            When the code-reindex maintenance job runs
            Then the pending rows are re-embedded under the new engine
            And kind=code search returns vector-ranked results for them again

    Rule: Code and code/both searches are excluded from search-quality recording
        # Review F-21 (plan §1, §3.6): search_quality rows sync off-machine; recording
        # a code query would leak identifiers/paths the same way a raw sync push would.
        # Memory searches are unaffected.
        Scenario: A kind=code search writes no search_quality row
            When I call memory_search for the project with kind "code"
            Then no search_quality row is written for that call

        Scenario: A kind=both search writes no search_quality row
            When I call memory_search for the project with kind "both"
            Then no search_quality row is written for that call

        Scenario: A kind=memory search still records exactly as today
            When I call memory_search for the project with kind "memory"
            Then a search_quality row is written for that call
