# Why one memory bank per install scope

The ai-raccoon server stores an agent's durable knowledge in a single SQLite database per
install scope, partitioned by context. The store is a managed .NET layer — our own
SQLite schema, Dapper queries, and C# ranking — replacing the pinned sqlite-memory
extension. This page explains why that shape was chosen, and what the pieces are for.
For the mechanical contract (tool names, parameters, env vars) see
`docs/reference/agent-memory-server.md`; for the full architecture with data flow
diagrams see `docs/explanation/architecture.md`.

## The mental model

An agent works across projects. Its knowledge has two kinds of durability:

- **Project knowledge** — facts about one project: its conventions, decisions, gotchas.
- **Cross-project knowledge** — the user's preferences, shared conventions, general
  lessons — things any project benefits from.

A memory bank per *project* would duplicate the cross-project tier in every project and
make it diverge. A memory bank per *install scope* (one for the whole user when the tool
is installed globally, one for the project when installed locally) keeps a single copy of
everything, and context labels partition it:

- `project:<project-id>` — committed project memory.
- `shared` — the curated cross-project tier.
- `workspace:<workspace-id>` — sandboxed scratch memory for an in-flight task.

## Why we built our own store layer

sqlite-memory (the pinned extension) was the original backing store. Writing SQLite
virtual tables and triggers ourselves gives us:

- **Full control over the schema.** The `entries` table carries on-row metadata
  (`rating`, `ttl_days`, `access_count`, `embed_state`) that the extension handled in
  a separate meta database. One table, one query plan.
- **Hybrid search in C#.** Reciprocal rank fusion (RRF) runs in-process, with weights
  per modality (`ftsWeight`, `vectorWeight`) and a configurable `k` parameter — the
  extension's built-in ranking was a black box.
- **S3-compatible sync** instead of SQLite Cloud. No managed database dependency — sync
  pushes VACUUM snapshots to any S3-compatible object store with If-Match conflict
  detection, 3-retry loop, and tombstones.
- **Engine-agnostic embeddings.** The bundled `all-MiniLM-L6-v2` (ONNX, int8, ~21 MB)
  runs in-process with zero network calls. An OpenAI-compatible provider routes through
  any endpoint — the extension hardcoded vectors.space.

## Why writes land in the project by default

The server's default is conservative: `memory_write` without a `workspace_id` writes into
`project:<id>`. Nothing reaches the `shared` tier except an explicit `memory_share`
promotion. That makes "shared" a *curated* tier — a fact that a project found durable
enough to promote — rather than a second write lane that fills with noise. Because shared
is curated, it is also **sweep-exempt**: degradation removes old, low-rated *project*
entries but never promoted knowledge.

## Why the workspace is a context, not a flag

A workspace is isolated *by design* — the presence of a `workspace_id` on a write routes
it into `workspace:<id>`, and nothing in that context is synced to the cloud. The agent
works in a worktree (ai-badger's `worktree-agent-isolation` style), writes notes into the
workspace outbox, and at the end calls `memory_workspace_consolidate` to promote the
durable facts into the project's committed memory and discard the rest. Workspace rows
carry `workspace_id IS NOT NULL AND scope IS NULL` — the CHECK constraint enforces mutual
exclusion with committed rows.

## Why sync goes through one S3 object

`memory_sync` takes a VACUUM snapshot of the bank, strips workspace rows, and pushes it
to an S3-compatible object store. It pulls the remote snapshot, merges entries (INSERT OR
IGNORE by hash), merges settings (last-writer-wins), applies tombstones, and pushes back
with If-Match for conflict detection. A user-scope install and a project-scope install on
the same machine are independent local banks; they correlate **only through the shared
cloud object**.

## How extensions keep the server open

The `IMemoryExtension` pipeline (`MemoryExtensionHost`) lets first-party and future
third-party logic observe every store operation. The first-party `RetrievalRatingExtension`
is currently a no-op — the rating pipeline has been rewired directly into
`SqliteMemoryStore.SearchAsync` via the on-row `rating`/`access_count` columns (P1
rewire). The extension host architecture stays registered so later waves can add hooks
without restructuring the core.

## What it costs

- One SQLite database per install scope means a user-scope install holds every project's
  rows; context filters keep reads/writes partitioned, and `memory_stats` counts only the
  caller's project context.
- Content-hash dedup is global within a project's committed set (`workspace_id IS NULL`,
  matched on `value`). Identical content in two projects produces two rows with different
  project IDs, so cross-project dedup is not enforced — this is deliberate, keeping
  projects independent.
- Deferred embeddings (`embed_state = 'pending'` by default) mean writes work before any
  model is configured; search only returns embedded content, so a fresh bank needs an
  engine configured via the CLI (`ai-raccoon model set local` or `model set openai …`)
  plus `memory_embed_pending` to become searchable. When an engine is
  already configured, writes embed synchronously.
- Embedding engine changes (`ai-raccoon model set …` with a different provider/model/base-url)
  re-embed the entire bank: previously embedded rows are re-processed with the new engine,
  and the pending queue is left alone.
