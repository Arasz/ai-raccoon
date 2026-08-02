# Why one memory bank per install scope

The ai-raccoon server stores an agent's durable knowledge in a single SQLite database per
install scope, partitioned by context. This page explains why that shape was chosen, and
what the pieces are for. For the mechanical contract (tool names, parameters, env vars)
see `docs/reference/agent-memory-server.md`; for the full design dossier see
`docs/features/agent-memory/spec-issue-1.md`.

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
durable facts into the project's committed memory and discard the rest. No separate
`isolated` flag exists: naming the workspace *is* the isolation declaration.

## Why sync goes through one cloud database

`memory_sync` pushes/pulls the bank's committed contexts (`shared` + every
`project:<id>`) into a configured SQLite Cloud database. A user-scope install and a
project-scope install on the same machine are independent local banks; they correlate
**only through the cloud database** they both sync into. That keeps the sync story simple
in V1 — no peer-to-peer, no merge topology — and leaves a single cloud memory bank for
all agents as a later step (see the dossier's open questions).

## How extensions keep the server open

The `IMemoryExtension` pipeline (`MemoryExtensionHost`) lets first-party and future
third-party logic observe every store operation. The first-party `RetrievalRatingExtension`
bumps an entry's access count and rating each time a search returns it; `SweepService`
uses those ratings with the degradation policy. Auto-promotion to `shared` based on
ai-badger's stack concept is a recorded open question (OQ-8) — the pipeline is the seam.

## What it costs

- One SQLite database per install scope means a user-scope install holds every project's
  rows; context filters keep reads/writes partitioned, and `memory_stats` counts only the
  caller's project context.
- sqlite-memory's content-hash dedup is global, so identical content in two contexts needs
  `preserve_duplicate_paths=1` (set at bank open) plus distinct logical paths — this is
  how `memory_share` creates a real `shared` row without duplicating the source row.
- Deferred embeddings (`defer_embeddings=1` by default) mean writes work before any model
  is configured; search only returns embedded content, so a fresh bank needs
  `memory_configure` + `memory_embed_pending` to become searchable.
