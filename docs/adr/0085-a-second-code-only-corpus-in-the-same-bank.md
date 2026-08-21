# 0085. A second, code-only corpus in the same bank

Date: 2026-08-21

Status: Accepted

Plan: `docs/work/2026-08-21-code-search-implementation-plan.md` (rev 3, §1, §3.1, §3.2, §3.8).

## Context

Agents already ask "how does X work" about their own repo, and the only answer today is the
prose memory bank — nothing indexes source line-by-line, embedded for semantic retrieval. The
obvious shapes were a separate bank file, or folding code chunks into `entries` with a
discriminator column. Both were rejected: a separate bank duplicates every piece of connection,
encryption, and settings machinery `MemorySchema.EnsureAsync` already owns for one `memory.db`;
folding into `entries` means every existing memory query (search, sweep, promotion, sync) now
has to filter a `kind` column it never needed before, and `entries` carries columns (`rating`,
`ttl_days`, `access_count`, `scope`) that mean nothing for a re-derivable code index.

## Decision

**A second, code-only corpus in the same `memory.db`, added by the existing digest-gated
additive `Ddl` block** — the metrics-table precedent (`MemorySchema.cs:335-342`): purely
additive `CREATE … IF NOT EXISTS`, no ladder step, no `CurrentVersion` bump, re-run once per
bank on every digest change.

1. **`code_entries(id, hash, path, value, source_file, line_start, line_end, project_id,
   created_at, updated_at, embed_state, embedding BLOB, chunk_index, total_chunks)`** +
   `uq_code_chunk UNIQUE(project_id, path, hash)` + indexes on `(project_id)`, `(hash)`,
   `(embed_state, project_id)`. Deliberately absent, with reasons: `scope`/`workspace_id`/
   `agent_id` (code is project-scoped only, never shared or workspace-isolated),
   `rating`/`ttl_days`/`access_count` (no degradation — see below), `heading_path`/`section`/
   `source_id` (no structure modality in v1 — code has no headings).
2. **`code_fts`** — FTS5 external-content over `(value, source_file)`, `content='code_entries'`,
   the same trigger family (`ai`/`ad`/`au`) that `entries_fts` already uses.
3. **`vec_code`** — vec0, `ctx TEXT, embedding float[768] distance_metric=cosine`, `ctx =
   project_id`. `ctx` is a plain metadata column, not a `partition key` (ADR-0068's finding
   applies here unchanged: partitioning trades size for KNN latency, and this corpus inherits
   the same reasoning rather than re-litigating it).
4. **Lifecycle boundaries — code never syncs, never sweeps, no TTL, no promotion, no
   workspaces.** Watch removal deletes from both corpora (entries stay — existing semantics
   unchanged). The corpus is an explicit **re-derivable cache**: losing it costs a re-ingest
   from disk, never lost knowledge, which is the reason every degradation mechanism the memory
   bank has (rating decay, sweep, TTL, shared-tier promotion) is absent by design rather than
   by oversight.
5. **`memory_stats`/`memory_list`/`memory_performance` stay memory-only in v1.** Code counts
   surface through the `code-reindex` maintenance ledger and metrics instead of overloading
   tools whose contract is "the memory bank's shape" (ADR-0087 covers the drain job itself).
6. Encryption-at-rest is inherited wholesale — it is a bank-level property, not a per-corpus
   one, so no new encryption surface exists for code.

## Consequences

- **Positive**: every connection/pooling/encryption/settings mechanism the memory bank already
  has is reused for free; no second bank file, no second set of CLI/MCP plumbing for
  connectivity.
- **Positive**: the absent-columns list is legible — a reviewer can see code has no `rating`
  column and know from this ADR why, rather than guessing it was forgotten.
- **Negative**: `code_entries` and `entries` are structurally similar but not unified — a future
  third corpus (if one ever exists) re-asks the same "which columns apply" question rather than
  inheriting an answer from a shared base.
- **Not addressed**: the drain/re-embed mechanism (ADR-0087), watch/ignore semantics
  (ADR-0086), and the search surface (ADR-0088) are separate decisions this ADR does not cover.

Extends ADR-0068 (`ctx` as vec0 metadata, not partition key) and the metrics-table digest-gated
`Ddl` precedent (`MemorySchema.cs:335-342`).
