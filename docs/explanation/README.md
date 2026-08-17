# explanation/

Understanding-oriented background: why the architecture is shaped the way it is, how the layers relate. Filenames are noun phrases, optionally `why-` prefixed.

## Contents

- [`agent-memory-architecture.md`](agent-memory-architecture.md) — why the memory bank is per install scope, why writes default to the project, why proposals wait in a propose tier, why the workspace is a context rather than a flag, why sync goes through one cloud object, and what the shape costs.
- [`agent-memory-capabilities.md`](agent-memory-capabilities.md) — in-depth explanation of memory capabilities: scope partitioning, hybrid FTS5+vec0 RRF search pipeline, workspace sandboxes, propose/shared promotion tier, rating degradation, and JSON filetype handling.
- [`architecture.md`](architecture.md) — the full architecture: data model, layers, write and search flows, sync cycle, workspace lifecycle, access modes, and all algorithms (RRF, content hashing, rating, degradation, chunking, FTS5 normalisation).
- [`model-migration-flow.md`](model-migration-flow.md) — how a `model set` becomes a transactional outbox drained by an on-demand relay, why every tool call checks the bank for an open migration, what happens at each of the three crash points, and the two things that surprise people: setting the engine you are already on does nothing, and a real migration blocks the bank for minutes.
