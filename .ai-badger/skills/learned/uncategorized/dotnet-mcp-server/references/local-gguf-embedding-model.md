# Local GGUF embedding model for sqlite-memory (verified on ai-raccoon)

How to pick, download and verify a small local embedding model for the
sqlite-memory extension's llama.cpp local engine — and which remote options
are actually configurable. All facts verified against sqlite-memory 1.3.5
binaries and its source (2026-08).

## The model that works

`memory_set_model('local', '<gguf-path>')` accepts any llama.cpp-compatible
GGUF embedding model. Verified end-to-end (writes + `memory_embed_pending` +
`memory_search` round-trip) with:

| Model | File | Size | License | Notes |
|---|---|---|---|---|
| all-MiniLM-L6-v2 | `all-MiniLM-L6-v2.Q5_K_M.gguf` | ~21 MB | Apache-2.0 | **Smallest verified**; the recommended default |
| all-MiniLM-L6-v2 | `all-MiniLM-L6-v2.Q8_0.gguf` | ~24 MB | Apache-2.0 | Slightly better fidelity, same size class |
| nomic-embed-text-v1.5 | `nomic-embed-text-v1.5.Q8_0.gguf` | ~139 MB | Apache-2.0 | The model sqlite-memory's README documents (reference choice) |
| nomic-embed-text-v1.5 | `nomic-embed-text-v1.5.Q4_K_M.gguf` | ~80 MB | Apache-2.0 | Cheapest nomic quantization |

All Apache-2.0 → redistributable with the server. Sizes are the real byte
counts (Content-Length after following the HF Xet CDN redirect — HEAD on the
`/resolve/main/` URL returns a redirect with a stub length).

## Download + pin (verified recipe)

Official repos: `nomic-ai/nomic-embed-text-v1.5-GGUF` (Hugging Face) and
`leliuga/all-MiniLM-L6-v2-GGUF`. Direct URL shape:

```
https://huggingface.co/<org>/<repo>/resolve/main/<file>.gguf
```

Pinned SHA-256 of `all-MiniLM-L6-v2.Q5_K_M.gguf` (2026-08):
`908c82ac3849f9ca23158117cec614bd8ec404040d8794c35b4c81242bf315e3`

A reusable download script lives in the project repo:
`scripts/download-embedding-model.sh all-minilm|nomic [out-dir]` — downloads,
SHA-256 verifies, and is idempotent (re-run prints "already downloaded and
verified"). Install target convention: `<data-root>/models/`.

## Test-gating: AIRACCOON_TEST_GGUF

Embedding integration/E2E tests read `AIRACCOON_TEST_GGUF` (a GGUF path) and
`Assert.Skip(...)` (xunit.v3) when unset. With the model set, the full suite
runs at 0 skips:

```bash
export AIRACCOON_TEST_GGUF=<data-root>/models/all-MiniLM-L6-v2.Q5_K_M.gguf
dotnet test   # 185 pass, 0 skipped
```

## Embedding engine matrix — what is actually configurable

`memory_configure(provider, model)` accepts any provider string, but the
pinned sqlite-memory 1.3.5 resolves exactly TWO engines:

| Engine | provider | model | Key |
|---|---|---|---|
| Local (llama.cpp) | `local` | GGUF file path | none |
| Remote (vectors.space) | `openai` | e.g. `text-embedding-3-small` | `AIRACCOON_VECTORSSPACE_API_KEY` |

**LM Studio / Ollama / arbitrary OpenAI-compatible endpoints are NOT
configurable.** Verified from source: `src/dbmem-rembed.c` hardcodes
`#define API_URL "https://api.vectors.space/v1/embeddings"` — there is no
base-URL override. The extension's custom-provider hook (`dbmem_provider_t`
callbacks in `src/sqlite-memory.c`) is an in-process C API for embedding a
custom engine at build time, not a runtime setting. Documenting LM Studio as
a config option would be false — say so explicitly instead of implying it.

## Pitfall: Dapper record-ctor matching vs blob-affinity columns

The `memory_search` virtual table declares blob-affinity columns, so Dapper's
`GetFieldType` returns `byte[]` and record-ctor matching demands a
`(byte[], byte[], ...)` ctor → `InvalidOperationException: A parameterless
default constructor or one matching signature (System.Byte[] ...) is required`.
`QueryAsync<MemorySearchResult>` on a record crashes; the actual values are
strings/ints/doubles. Fix: materialize into a **settable DTO** (`SearchRow`
class with `{ get; set; }` props), then map to the record:

```csharp
var rows = await connection.QueryAsync<SearchRow>(sql, ...);
var results = rows.Select(r => new MemorySearchResult(r.Hash, r.Seq, r.Ranking, r.Path, r.Snippet)).ToList();
```

Same pattern as the `MetaRow` DTO elsewhere in the store. This bug only
surfaces when the embedding tests actually run with a real GGUF model — the
search path was never exercised while they were skipped.
