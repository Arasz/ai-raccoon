## Parallel waves sharing a committed binary artifact (corpus db, vector store)

When two parallel waves both regenerate the same committed binary (SQLite db, vector store) plus a coupled generated map (hash map / index), the plan's "independent" dependency graph is a lie — the binary is a physical shared file. Design around it:

- Make the LATER-MERGED wave content-preserving: a **backfill** that adds columns/embeddings without changing existing row content, instead of re-ingesting from source. Content hashes stay byte-identical, so the coupled generated map never conflicts; a content-rewriting wave forces a map conflict on top of the binary conflict.
- Backfills must be **idempotent and re-runnable**: the later wave's db is stale the moment the earlier wave merges (content changed underneath), so the orchestrator re-runs the backfill on the merged corpus at integration. Tell the agent this explicitly in the dispatch context ("your db commit will conflict; the orchestrator re-runs your backfill on the merged corpus — make it safe to re-run").
- **Merge order**: content-changing wave first, backfill wave second (rebased on merged main), then re-run the backfill before the final gate. Do not let the backfill wave merge before the content wave.
- Commit the binary + its generated map in ONE commit — tests match against the map; a half-updated pair breaks expected-source matching and looks like a random test failure.
- Capture the measurement reference (baseline run) on the pre-wave HEAD BEFORE dispatch — post-merge comparisons need fresh numbers from the same commands; a reference captured mid-wave is contaminated.
