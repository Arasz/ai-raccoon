# File watcher — part 2 of native-memory (behavioral contract)

@bdd
Feature: File watcher

    As a coding agent
    I want to register a path and never think about syncing
    So that all changes under that path land in my memory by default

    # Benefit (ability header, confirmed 2026-08-04):
    # 1. Memory seed — a useful memory bank exists before doing anything.
    # 2. Sync is persisted and independent of agent decisions — eliminates the
    #    agent "forgetting" to update memory when a file is added or changed.
    # 3. Memory always in sync with docs — agent can ask about a new file right
    #    after it was added.

    # Baseline (confirmed in restatement, 2026-08-04):
    # - Three MCP tools: memory_watch_add / memory_watch_status / memory_watch_remove.
    # - Backed by a persisted watches table; registrations survive restart
    #   (a restart re-watches what was registered).
    # - Re-digest calls the existing ingest path.
    # - Gap analysis (2026-08-04): watcher fires OnSourceChanged -> ingest pipeline.

    Rule: Watching is opt-in and bounded by a scope allowlist
        # 2026-08-04: watching stays disabled until explicitly enabled; watch
        # paths must be inside an allowed scope — an absolute path covers the
        # directory and all subdirectories (no "*" needed).
        Scenario: Watching is disabled until explicitly enabled
            Given the server with watching disabled
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the tool errors with watching-disabled

        Scenario: A watch inside an allowed scope is created
            Given a scope allowlist containing "/repo"
            And watching enabled
            When I call memory_watch_add for "proj-a" on path "/repo/docs"
            Then a watch exists for "proj-a" at "/repo/docs"

        Scenario: A watch outside the allowed scopes is rejected
            Given a scope allowlist containing "/repo"
            And watching enabled
            When I call memory_watch_add for "proj-a" on path "/other"
            Then the tool errors with path-outside-scope

    Rule: Watch configuration is CLI-only and user-facing
        # 2026-08-04 (f): the CLI is the ONLY channel for watch config — no MCP
        # tools, no env/args, no config-file edits; commands run by the user so
        # no access-tier checks apply. Format: settings watch enable|disable {project-id|*}
        # {true|false} and settings ingest scope add|remove|list {project-id|*} {path};
        # "*" matches all projects and the more specific entry wins.
        Scenario: settings watch enable returns a message to add a scope
            When the user runs settings watch enable * true
            Then the command returns a message to add at least one scope

        Scenario: The scope allowlist is managed add/remove/list
            Given a scope allowlist containing "/repo"
            When the user runs settings ingest scope add * /docs
            Then the allowlist contains "/repo" and "/docs"

        Scenario: A more specific project setting overrides the wildcard
            Given settings watch enable * true
            When the user runs settings watch disable proj-a false
            Then watching stays disabled for "proj-a"
            And watching stays enabled for other projects

        Scenario: settings watch concurrency sets the digest limit per project
            When the user runs settings watch concurrency proj-a 8
            Then the concurrency limit for "proj-a" is 8

        Scenario: settings watch concurrency rejects values outside 1-16
            When the user runs settings watch concurrency proj-a 20
            Then the command errors with invalid-value

        Scenario: Watch configuration survives a restart
            Given watching enabled with scope "/repo"
            When the server restarts
            Then watching is still enabled with scope "/repo"

        Scenario: watch registered lists the registrations made by memory_watch_add
            Given a watch for "proj-a" on path "/repo"
            When the user runs watch registered
            Then the CLI output lists the registered watch for "proj-a" at "/repo"

    Rule: Watches are project-scoped
        # 2026-08-04 (f): a watch belongs to exactly one project; created only in
        # project scope, digests land in that project's bank; all three tools take
        # projectId like every memory tool; no global/workspace-scoped watches.
        Scenario: A watch created for a project digests into that project's bank
            Given a project "proj-a" and a project "proj-b"
            And a file "notes.md" under the watched path
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then a watch exists for "proj-a" at "/repo"
            And memory_search for "proj-a" returns the file's content
            And memory_search for "proj-b" returns nothing for it

        Scenario: Two projects watch the same path without interference
            Given a watch for "proj-a" on path "/repo"
            When I call memory_watch_add for "proj-b" on path "/repo"
            Then both watches exist
            And a change to "/repo/file.md" is searchable in both projects' banks

        Scenario: A watch cannot be created without a project
            When I call memory_watch_add without a project on path "/repo"
            Then the tool errors with missing-project

    Rule: Watches support both files and directories
        # 2026-08-04: memory_watch_add accepts a single file or a directory.
        Scenario: A single file path registers a watch
            Given a file "/repo/readme.md"
            When I call memory_watch_add for "proj-a" on path "/repo/readme.md"
            Then a watch exists for "proj-a" at "/repo/readme.md"
            And editing the file makes the new content searchable

        Scenario: A directory path registers a watch covering its subtree
            Given a directory "/repo" containing "a/b.md"
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then a watch exists for "proj-a" at "/repo"
            And editing "/repo/a/b.md" makes the new content searchable

        Scenario: A non-existent path is rejected with an error
            When I call memory_watch_add for "proj-a" on path "/missing"
            Then the tool errors with path-not-found

    Rule: The initial scan is asynchronous
        # 2026-08-04: add returns immediately; the seed digest runs in the
        # background and status reports scanning until it completes.
        Scenario: add returns before the seed digest completes
            Given a directory "/repo" with files
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the call returns with the watch registered
            And memory_watch_status for "proj-a" shows the watch scanning

        Scenario: A large directory still returns add immediately
            Given a directory "/repo" with many files
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the call returns without waiting for the scan to finish

        Scenario: A scan error surfaces in status after add already returned
            Given a directory "/repo" containing an unreadable file
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the call still returns
            And memory_watch_status for "proj-a" shows the scan error

    Rule: All four filesystem event types trigger processing
        # 2026-08-04: created, changed, renamed, deleted all enter the pipeline.
        Scenario: A created file is digested
            Given a watch for "proj-a" on path "/repo"
            When a new file "/repo/new.md" is created
            Then memory_search for "proj-a" returns its content

        Scenario: A burst of mixed events collapses into one tick of processing
            Given a watch for "proj-a" on path "/repo"
            When a file receives create, change and rename events within one tick
            Then the final content is searchable for "proj-a"

        Scenario: An event outside the watch scope is ignored
            Given a watch for "proj-a" on path "/repo"
            When a file outside "/repo" changes
            Then memory_search for "proj-a" returns nothing for it

    Rule: A watch mirrors the filesystem into memory 1:1
        # 2026-08-04: watch reflects ALL filesystem changes to memory — a delete
        # removes the file's chunks, a rename moves its identity. If mirroring is
        # not wanted, use the standard ingestion tools (memory_ingest_file /
        # memory_ingest_directory) instead of a watch.
        Scenario: A new file is searchable after the next tick
            Given a watch for "proj-a" on path "/repo"
            When a file is created in "/repo"
            Then it is searchable for "proj-a" within one second

        Scenario: A file modified during its own digest is picked up by the next tick
            Given a watch for "proj-a" on path "/repo"
            And a file whose digest is in flight
            When the file changes during the digest
            Then the newer content is searchable after the following tick

        Scenario: A change while the server is down is ingested on restart
            Given a watch for "proj-a" on path "/repo" with "file.md" ingested
            And "file.md" changed while the server was stopped
            When the server restarts and re-watches
            Then "file.md" is re-digested and its new content is searchable
    # 2026-08-04 (card 1): missed events only occur while the server is
    # down; each watch keeps a last-change timestamp; on restart, targets
    # changed since it are re-ingested (catch-up).

    Rule: A delete event removes the deleted file's chunks from memory
        # 2026-08-04: file deleted on disk -> its memory entries are deleted too
        # (mirror semantics).
        Scenario: Deleting a file removes its chunks from search
            Given a watch for "proj-a" on path "/repo" with "file.md" ingested
            When "file.md" is deleted
            Then memory_search for "proj-a" no longer returns its content

        Scenario: Deleting a file while its digest is in flight resolves deterministically
            Given a watch for "proj-a" on path "/repo"
            And "file.md" changed and queued for digest
            When "file.md" is deleted before the digest runs
            Then the file is not present in memory after the next tick

        Scenario: Deleting a never-ingested file is a silent no-op
            Given a watch for "proj-a" on path "/repo"
            When a file that was never ingested is deleted
            Then memory_watch_status for "proj-a" stays healthy

    Rule: A rename event moves the file's memory to the new path
        # 2026-08-04: old path's chunks are removed, content is re-digested under
        # the new path (mirror semantics; D8 path-scoped identity).
        Scenario: Renaming a file moves its memory to the new path
            Given a watch for "proj-a" on path "/repo" with "old.md" ingested
            When "old.md" is renamed to "new.md"
            Then memory_search for "proj-a" returns the content under "new.md"
            And nothing is returned under "old.md"

        Scenario: Renaming before the initial scan finishes resolves deterministically
            Given a watch for "proj-a" on path "/repo" that is still scanning
            When a file is renamed during the scan
            Then the content lands under the final path

        Scenario: Renaming onto an existing path resolves deterministically
            Given a watch for "proj-a" on path "/repo" with "a.md" and "b.md" ingested
            When "a.md" is renamed onto "b.md"
            Then exactly one memory entry holds that path
    # 2026-08-04 (card 2): overwrite — the incoming content wins; the
    # target's previous chunks are replaced.

    Rule: Processing ticks every 1 second
        # 2026-08-04: debounce window; pending paths are processed on a 1s interval.
        Scenario: A change is processed within one second of the tick
            Given a watch for "proj-a" on path "/repo"
            When a file changes under the watch
            Then the new content is searchable within one second

        Scenario: A change at the tick boundary is processed by the next tick
            Given a watch for "proj-a" on path "/repo"
            And a change that lands just before a tick
            When the next tick runs
            Then the new content is searchable after that tick

        Scenario: An idle tick processes nothing
            Given a watch for "proj-a" on path "/repo" with no pending changes
            When a tick runs
            Then memory_search for "proj-a" returns the same results as before

    Rule: Per-path digests run concurrently with a configured limit, 4 by default, round-robin across watches
        # 2026-08-04: concurrent per path; global concurrency limit (default 4);
        # round-robin scheduling across watches so one watch cannot starve others.
        Scenario: Ten pending paths across three watches process four at a time
            Given watches for "proj-a", "proj-b" and "proj-c" on three directories
            And ten files changed across the three watches
            When the tick processes them
            Then all ten files are searchable in their projects

        Scenario: Exactly four pending paths saturate the limit
            Given a watch for "proj-a" on path "/repo" with four pending files
            When the tick processes them
            Then all four files are searchable

        Scenario: One watch's flood does not starve other watches
            Given a watch on a directory with many changes
            And a watch on a directory with one change
            When the tick processes
            Then the small watch's change is searchable promptly
            And the large watch's changes are searchable too

    Rule: Watching follows the design
        # Mechanics: debounce, hash-skip, initial scan, retry.
        Scenario: A metadata-only touch is skipped by hash-skip
            Given a watch for "proj-a" on path "/repo" with "file.md" ingested
            When "file.md" is touched without a content change
            Then the memory entry is unchanged

        Scenario: Repeated saves of one file produce one digest
            Given a watch for "proj-a" on path "/repo"
            When a file is saved repeatedly within one tick
            Then the final content is searchable
            And no intermediate save's content is ever searchable

        Scenario: A real content change is never skipped
            Given a watch for "proj-a" on path "/repo" with "file.md" ingested
            When "file.md" content changes
            Then the new content is searchable

    Rule: Watch operation failures never fail the MCP server
        # 2026-08-04: watcher errors are contained; a failing watch must not take
        # down the MCP server or its other tools.
        Scenario: An ingest failure for one file leaves other tools serving
            Given a watch for "proj-a" on path "/repo" where one file's digest fails
            When the tick processes it
            Then memory_search for "proj-a" still answers
            And memory_watch_add for "proj-b" on another path still works

        Scenario: A watcher loop exception is contained and logged
            Given a watch for "proj-a" on path "/repo"
            When the watcher loop hits an exception
            Then the MCP server keeps serving
            And memory_watch_status for "proj-a" still answers

        Scenario: A watch on an inaccessible path errors without crashing the server
            Given a watch for "proj-a" on a path that becomes unreadable
            When the next tick runs
            Then the MCP server keeps serving
            And memory_watch_status for "proj-a" shows the error

    Rule: Watch errors are reported through memory_watch_status
        # 2026-08-04: when something fails, status returns the error.
        # Status states (card 4, confirmed; f: "stale" removed — a missed event
        # goes straight to catch-up processing on restart): scanning / healthy /
        # retrying / stopped.
        Scenario: A failed digest shows the error in status
            Given a watch for "proj-a" whose file fails to ingest
            When the failure occurs
            Then memory_watch_status for "proj-a" shows the error for that watch

        Scenario: A transient error clears when the next digest succeeds
            Given a watch for "proj-a" with a failed digest
            When the next digest succeeds
            Then memory_watch_status for "proj-a" no longer shows the error

        Scenario: Status still answers while a watch is erroring
            Given a watch for "proj-a" with an error
            When memory_watch_status for "proj-a" is called
            Then it returns the watch states including the error

    Rule: A failing watch retries, then stops checking but keeps its status
        # 2026-08-04: retry with exponential backoff; after 5 retries the watch is
        # removed from checking but its registration/status is kept. (f: backpropagation = exponential backoff)
        Scenario: Five consecutive failures stop checking but keep status
            Given a watch for "proj-a" whose digests fail
            When five consecutive digests fail
            Then memory_watch_status for "proj-a" reports the watch as stopped
            And the watch remains registered

        Scenario: A fourth failure is still retried with backoff
            Given a watch for "proj-a" with four failed digests
            When a change arrives
            Then the digest is attempted again
            And memory_watch_status for "proj-a" reports the watch as retrying

        Scenario: A success on retry resets the counter and checking continues
            Given a watch for "proj-a" with a failed digest
            When the next digest succeeds
            Then memory_watch_status for "proj-a" reports the watch healthy
            And later failures do not stop it prematurely

    Rule: memory_watch_remove on a non-existent watch is a no-op
        # 2026-08-04: removing a watch that is not registered does nothing (no error).
        Scenario: Removing a never-registered path returns success without effect
            When I call memory_watch_remove for "proj-a" on path "/never-registered"
            Then the tool returns success
            And memory_watch_status for "proj-a" shows no watch for it

        Scenario: Removing an already-removed path is still a no-op
            Given a watch for "proj-a" that was already removed
            When I call memory_watch_remove for "proj-a" on the same path
            Then the tool returns success
            And no watch exists for the path

        Scenario: A pending digest for a removed watch does not resurrect the watch
            Given a removed watch with a queued change
            When the tick runs
            Then no digest happens for that path
            And the watch stays removed

    Rule: memory_watch_add on an already-watched path is a no-op
        # 2026-08-04: adding a watch for a path already registered does nothing (no
        # error, no duplicate watch). Scope: per (projectId, path).
        Scenario: Adding the same path twice in one project yields one watch
            When I call memory_watch_add for "proj-a" on path "/repo" twice
            Then exactly one watch exists for "proj-a" at "/repo"

        Scenario: Re-adding with different path normalization is still a no-op
            Given a watch for "proj-a" on path "/repo"
            When I call memory_watch_add for "proj-a" on path "/repo/"
            Then exactly one watch exists for "proj-a" at "/repo"
        # 2026-08-04 (card 3): OS-agnostic normalization — Path.GetFullPath
        # (absolute; separators and ".." resolved; trailing separator stripped);
        # case comparison follows the host OS filesystem semantics.

        Scenario: The same path in a second project creates a separate watch
            Given a watch for "proj-a" on path "/repo"
            When I call memory_watch_add for "proj-b" on path "/repo"
            Then a second watch exists for "proj-b" at "/repo"
            And each project digests into its own bank

    Rule: The watcher mirrors regardless of access tier
        # 2026-08-04: access tiers gate DIRECT agent operations only; the watcher's
        # background mirroring (incl. delete/rename removal) always runs, whatever
        # the project's tier.
        Scenario: A delete mirror-removes chunks in a ro project
            Given a project "proj-a" with access tier ro
            And a watch for "proj-a" on path "/repo" with "file.md" ingested
            When "file.md" is deleted
            Then memory_search for "proj-a" no longer returns its content

        Scenario: A delete mirror-removes chunks in a rw project
            Given a project "proj-a" with access tier rw
            And a watch for "proj-a" on path "/repo" with "file.md" ingested
            When "file.md" is deleted
            Then memory_search for "proj-a" no longer returns its content

        Scenario: No tier denies or blocks the background mirror
            Given a project "proj-a" with access tier ro
            When the mirror removes chunks for a deleted file
            Then the removal succeeds without tier denial

    Rule: memory_watch_add and memory_watch_remove require read-write access
        # 2026-08-04: ro cannot add or remove watches; rw and full may. (f: correction
        # — watches are not in the "settings that affect other agents" category.)
        Scenario: A rw agent adds and removes watches
            Given a project "proj-a" with access tier rw
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the watch is created
            And memory_watch_remove for "proj-a" on path "/repo" removes it

        Scenario: A ro agent's add is rejected with an access error
            Given a project "proj-a" with access tier ro
            When I call memory_watch_add for "proj-a" on path "/repo"
            Then the tool errors with access-denied

    Rule: memory_watch_status is available to every access tier
        # 2026-08-04: status is a read-only inspection; ro may call it. (Confirmed.)
        Scenario: A ro agent reads watch status
            Given a project "proj-a" with access tier ro
            And a watch for "proj-a" on path "/repo"
            When memory_watch_status for "proj-a" is called
            Then the watch states are returned

        Scenario: Status during an in-flight scan reports scanning
            Given a watch for "proj-a" on path "/repo" that is scanning
            When memory_watch_status for "proj-a" is called
            Then the watch state is scanning

        Scenario: Status with no watches returns an empty list without error
            Given a project "proj-a" with no watches
            When memory_watch_status for "proj-a" is called
            Then an empty list is returned without error

    Rule: A project-ids repair folds watch registrations without resurrecting the loser
        # air-merge P3+P4: the repair renames watches/watch_files/watch_digest_claims as UPDATEs
        # preserving scan_owner/lease (never DELETE+INSERT); a digest completing under a winner
        # afterwards leaves zero loser rows. The transient blank-id Ambiguous window — watches
        # already moved while a scope row still names the loser — is fail-closed by design
        # (CwdProjectIdResolverTests.AliasPair_StaysAmbiguous_NeverFolds).
        Scenario: Renaming watched projects keeps scan leases and drops the loser keys
            Given a watch for "job-search-ai-assistant" on path "/repo" with "a.md" and "b.md" ingested
            And a digest claim for "job-search-ai-assistant" at "/repo" with scan owner "scanner-1"
            And a watch for "AI-RACCOON" on path "/repo" with "c.md" ingested
            And a guid watch for "01a062f4-0000-7000-8000-000000000001" named "job-search-ai-assistant" on path "/repo/g" with "g.md" ingested
            And loser scope and config rows for "job-search-ai-assistant" and "AI-RACCOON"
            And labeled project rows for the loser ids
            When the project-ids repair folds loser ids into their winners
            Then no loser rows remain except byte-identical file mirrors
            And the scope and config rows moved to the winner keys
            And the "jsaa" watch keeps scan owner "scanner-1" with its lease
            And the "ai-raccoon" watch still owns "c.md"
            When "a.md" and "b.md" change
            Then memory_search for "jsaa" returns both new contents
            # g.md is asserted searchable only AFTER a post-repair change: its untouched rows
            # stay byte-identical under the guid partition (review S2), so an unchanged g.md
            # can never be searchable for the winner — same treatment a.md/b.md get above.
            When "/repo/g/g.md" content changes
            Then "g.md" is searchable for "jsaa"
            And still no loser rows remain and mirrors are unchanged
