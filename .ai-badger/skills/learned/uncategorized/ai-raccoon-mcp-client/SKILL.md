---
name: mcp-client
description: Use when writing tools that call AiRaccoon's MCP server.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [macos]
---

# AiRaccoon MCP Client

Operational reference for scripts and tools that call AiRaccoon's MCP server over its HTTP
transport. Read this before writing any Python/Node/curl code that hits the MCP endpoint.

## Prerequisites

- AiRaccoon server built and running: `MCP_TRANSPORT=http dotnet run --project src/AiRaccoon`
- Server listens on `http://localhost:5000/mcp` by default (not 8080)
- ONNX model must be in build output: copy `src/AiRaccoon/Models/model_qint8_arm64.onnx` into
  `src/AiRaccoon/bin/Debug/net10.0/Models/` after `dotnet build` (the .csproj doesn't auto-copy it)

## Transport: SSE (Server-Sent Events)

AiRaccoon's Streamable HTTP transport returns MCP responses as SSE:

```
event: message
data: {"result":{"content":[{"type":"text","text":"{...}"}]},"id":1,"jsonrpc":"2.0"}
```

**Do NOT call `resp.json()` directly.** Parse with:

```python
text = resp.text
if "event:" in text and "data:" in text:
    data_line = next((line.removeprefix("data: ").strip()
                      for line in text.splitlines()
                      if line.startswith("data: ")), "{}")
    outer = json.loads(data_line)
else:
    outer = resp.json()
```

The actual tool result is nested: `outer["result"]["content"][0]["text"]` is a JSON string that
needs a second `json.loads()`. Use a helper:

```python
def _unwrap(rpc_response: dict) -> dict:
    content = rpc_response.get("result", {}).get("content", [])
    text = content[0].get("text", "{}") if content else "{}"
    return json.loads(text)
```

## memory_write: no `path` parameter, context → scope='custom'

`memory_write` signature: `projectId, content, workspaceId?, agentId?, context?`. There is **no
`path` parameter.** AiRaccoon auto-generates the path as `SHA256(content).hex + ".md"`.

**Critical pitfall:** passing `context` to `memory_write` sets `scope='custom'` in the entries
table, making those entries **invisible to `memory_search(scope="project")`** and
**uncountable by `memory_stats`** (which filters `WHERE scope = 'project'`). Do NOT pass
`context` unless you intend custom-scoped entries.

**Workaround for provenance:** embed structured-path and context-label metadata in the
`content` string itself (e.g., `[docs:adr] path/to/file\n\n<actual content>`), and pass
structured-path as `agentId` for traceability.

## content hash computation

AiRaccoon computes identity via two formulas:

1. **Auto-generated path** (`WritePathFor`): `path = SHA256(content_bytes).hex() + ".md"`
2. **Content hash** (`ContentHash.Of`): `hash = SHA256(path_bytes + content_bytes).hex()`

To pre-compute the hash that `MemorySearchResult.hash` will carry for a given chunk:

```python
content_hash = hashlib.sha256(content.encode()).hexdigest()
assigned_path = content_hash + ".md"
expected_hash = hashlib.sha256(assigned_path.encode() + content.encode()).hexdigest()
```

The hash depends on the **exact bytes written** — if you embed metadata in content (see above),
the hash computation must use that same augmented content, not the raw chunk.

## memory_delete_context requires full access mode

`memory_delete_context` requires `AccessRequirement.Destructive`. The default `rw` mode denies
it. For reset/re-ingestion operations, either:
- Set the project to `full` mode before calling delete, or
- Delete the memory.db file directly (during development, when server is stopped)

## JSON-RPC parameter naming

MCP tools use **camelCase**: `projectId` (not `project_id`), `minScore`, `workspaceId`, etc.
Match the schema exactly.

## memory_embed_pending: limit handling

`memory_embed_pending(projectId, limit?)` — omit `limit` to process all pending entries. Passing
`limit=0` would process zero entries (contrary to some docs that suggest 0 = "all").

## Complete Python client pattern

See `scripts/ingest-jsaa-docs.py` for a working `AiRaccoonClient` class with:
- Async HTTP via `httpx`
- SSE response parsing
- Tool methods wrapping JSON-RPC `tools/call`
- Error handling and timing

## Pitfalls

1. **Server port**: `dotnet run` uses launchSettings.json which defaults to port 5000, not 8080.
   Run the built binary directly to control the port.
2. **ONNX model**: the bundled ONNX model is packed at build time but may not appear in the bin
   output. Copy it manually or set `AIRACCOON_EMBEDDING_MODEL` env var to point at the source.
3. **Duplicate entries**: re-ingestion without clearing the DB creates duplicate entries with
   different hashes (because content may differ), doubling stats and polluting search results.
4. **isError responses**: the RPC response may have `"isError": true` even with HTTP 200. Always
   check `content[0]["text"]` for error messages.

## Verification

- [ ] Server responds to `curl -X POST http://localhost:5000/mcp` with SSE
- [ ] `memory_stats` returns non-zero entries with `pending: 0`
- [ ] `memory_search` with `scope="project"` returns results
- [ ] Pre-computed hashes match `result.hash` in search output
