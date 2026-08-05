# explanation/

Understanding-oriented background: why the architecture is shaped the way it is, how the
layers relate. Filenames are noun phrases, optionally `why-` prefixed.

## Contents

- [`architecture.md`](architecture.md) — the full architecture: data model, layers, write
  and search flows, sync cycle, workspace lifecycle, access modes, and all algorithms
  (RRF, content hashing, rating, degradation, chunking, FTS5 normalisation).
- [`agent-memory-architecture.md`](agent-memory-architecture.md) — why the memory bank is
  per install scope, why writes default to the project, why the workspace is a context
  rather than a flag, why sync goes through one cloud object, and how the extension
  pipeline keeps the server open.
