# Error Patterns When Calling AiRaccoon MCP from Scripts

## 1. `resp.json()` on SSE transport → empty/parse error

**Symptom:** `json.decoder.JSONDecodeError: Expecting value: line 1 column 1 (char 0)`

**Root cause:** AiRaccoon MCP Streamable HTTP returns SSE (`event: message\ndata: {...}`), not
plain JSON. Calling `resp.json()` on an SSE body fails.

**Fix:** Parse SSE format first (see SKILL.md Transport section).

## 2. `memory_stats` returns `entries: 0` despite successful writes

**Symptom:** Writes return successfully, `memory_stats` shows `entries: 0, pending: 0`, but
the SQLite DB has rows.

**Root cause:** Passing `context` to `memory_write` sets `scope='custom'`. `memory_stats` only
counts `WHERE scope = 'project'`.

**Fix:** Don't pass `context` parameter. Embed metadata in content string instead.

## 3. `isExpectedSource` always false despite correct content

**Symptom:** Hash map contains expected hash, search results contain matching entries,
but `isExpectedSource` detection fails.

**Root cause:** Hash computation uses raw `chunk.content`, but actual written content
includes an embedded `[context] path\n\n` prefix. The hashes differ.

**Fix:** Compute hashes on the exact same string that gets passed to `memory_write(content=...)`.

## 4. `memory_embed_pending` returns `processed: 0`

**Symptom:** After writing many entries with `memory_configure(provider="local")`,
`memory_embed_pending` processes 0 entries, leaving all entries in pending state.

**Root cause:** ONNX model file (`model_qint8_arm64.onnx`) not found in the build output
directory. The .csproj doesn't auto-copy it.

**Fix:** Copy the model: `cp src/AiRaccoon/Models/model_qint8_arm64.onnx src/AiRaccoon/bin/Debug/net10.0/Models/`

## 5. `memory_delete_context` returns `access-denied: requires mode full (current rw)`

**Symptom:** All delete operations return isError with access-denied.

**Root cause:** Default access mode is `rw` (read+write, no destructive). `memory_delete_context`
requires `full` mode.

**Fix:** Set access mode to `full` before delete ops, or stop server and delete `memory.db` directly.
