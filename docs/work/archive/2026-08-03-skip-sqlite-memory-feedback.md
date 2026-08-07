# Refinement feedback — Replacing the pinned sqlite-memory extension — 15 decisions (3 settled + 12 open)

<!-- refinement-form: refinement:skip-sqlite-memory:2026-08-03:v1 · saved 2026-08-03T15:50:39.126Z · answered 15/15 -->

Source document:
`Synthesis in session 2026-08-03 — round 1 (sqlite-vec/.NET, feature parity, SmartRAG) + round 2 (fusion, Semantic Kernel, own sync) + architect analysis; grounded in docs/reference/agent-memory-server.md and the agent-memory.feature contract`

## S1 — One database — memory.db — and all metadata lives inside it

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## S2 — The memory layer is self-describing and acts on its own knowledge (reflection)

**Verdict:** APPROVE

**Notes:**

> first, we want to extract details as a separate feature. But as default RO access only, controled by configuration per
> project destructive access (rw) and global. First, we want to give a memory we can trust. Also we should add metrics and
> tracing so the knowledge will be complete - but not now.

---

## S3 — Cloud sync is our own implementation: the synced artifact is one file (memory.db) over online file storage — no sqlite-sync/cloudsync native extension

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D1 — No Semantic Kernel — Microsoft.Extensions.AI (IEmbeddingGenerator) plus our own SQL and chunking

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D2 — Hybrid search fuses FTS5 + sqlite-vec with Reciprocal Rank Fusion — no fusion library, no invented algorithm

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D3 — A golden-retrieval harness gates the swap: nDCG parity vs the old extension + no degenerate-query regression

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D4 — Own sync = S3-compatible object storage, one snapshot per install scope, VACUUM INTO + If-Match CAS + row-level merge with tombstones

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D5 — Embedding engine is pluggable via IEmbeddingGenerator; local offline = OpenAI-compatible local server (Ollama/llama.cpp) in V1, in-process ONNX as a later option

**Verdict:** APPROVE

**Notes:**

> yes, but the default we will use is the small downloaded model, but can be repleaced by any OpenAI compatible provider

---

## D6 — Chunking is our own deterministic splitter (vendored TextChunker 363 LOC MIT or ~200 LOC own), token-accurate via Microsoft.ML.Tokenizers, markdown fence-aware; md4c-grade parity only if the harness shows a regression

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D7 — Workspaces are a first-class entity with structural isolation: entries.workspace_id FK + XOR CHECK — committed (shared/project/custom) or in exactly one workspace, never both

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D8 — Content identity = path-scoped SHA-256 (path + value); memory_write keeps global content dedup; share/consolidate create real rows via distinct paths

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D9 — Three watcher tools extend the surface — memory_watch_add / memory_watch_status / memory_watch_remove — backed by a persisted watches table; SmartRAG's watcher design is the pattern, SmartRAG itself is not used

**Verdict:** APPROVE

**Notes:**

> lets move that as a separate task, part 2, for now only core func

---

## D10 — Retrieval-mode bands are deferred: V1 always runs the same RRF hybrid search; strategy bands (keyword → FTS-first, semantic → vector-first) come later behind the same memory_search contract

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D11 — Existing banks start fresh — no hash/embedding migration in the swap; re-hash + re-embed migration is a separate later task if a deployment needs it

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## D12 — Unified multi-source question answering (SmartRAG's text-to-SQL across databases, OCR, transcripts) is explicitly out of scope — the agent is the integrator, the store stores outcomes

**Verdict:** APPROVE

**Notes:**

_(none)_

---

## Not answered

_(none — every item has a verdict)_

<!-- end refinement feedback -->
