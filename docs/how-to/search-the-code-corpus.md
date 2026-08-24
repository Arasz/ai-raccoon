# Search the code corpus

Search your project's source code alongside (or instead of) your memory bank.

## Prerequisites

A code-corpus embedding engine must be active. If you have not set one up yet:

```bash
ai-raccoon model set code default
```

This downloads and activates `faxenoff/code-daemon-embed-v1` (187 MB, 768-dim) into
`<data-root>/models/`. See [Configure embedding engines](configure-embedding-engines.md#recipe-5-activate-the-code-corpuss-embedding-engine)
for details.

Without a code engine, `kind=code` and `kind=both` searches degrade to FTS5-only
(keyword matching, no vector similarity) and the server emits a warning. The
default `kind=both` search behaves the same way: memory results come back
normally, code results are keyword-only, and the response carries the warning.

## What gets indexed

Any file watched via `memory_watch_add` whose extension is in the code registry is
automatically ingested into the code corpus. The 24 supported extensions:

`.cs` `.fs` `.fsx` `.py` `.ts` `.tsx` `.js` `.jsx` `.go` `.rs` `.java` `.kt` `.kts`
`.swift` `.rb` `.php` `.c` `.h` `.cc` `.cpp` `.hpp` `.m` `.mm` `.scala` `.lua`

Memory-owned extensions (`.md`, `.txt`, `.json`, etc.) are never ingested into the
code corpus. The two corpora are disjoint by design.

## Searching

### Code only

```json
{
  "projectId": "<your-project-id>",
  "query": "how does the RRF fusion work",
  "kind": "code"
}
```

Returns a `code` array with hits carrying `lineStart`/`lineEnd` instead of
`chunkIndex`/`totalChunks`. The `results` key is present but empty.

### Memory only (legacy envelope)

```json
{
  "projectId": "<your-project-id>",
  "query": "how does the RRF fusion work",
  "kind": "memory"
}
```

Passing `kind=memory` searches only the memory bank. No `code`
key appears in the response. This is the pre-1.34 behavior, still available
explicitly — but the default is now `both`, so omit `kind` only when you want
both corpora.

### Both corpora (default)

```json
{
  "projectId": "<your-project-id>",
  "query": "how does the RRF fusion work"
}
```

Omitting `kind` (or passing `kind=both`) runs both hybrids independently and
returns both sections. Useful when you do not know whether the answer lives in
memory or in source. With no code engine configured, the memory section is
normal and the code section is keyword-only (FTS5) with a warning.

## Reading a full code chunk

Search results return snippets. To read the full source of a code hit, call
`code_get` with the `hash` from the search result:

```json
{
  "projectId": "<your-project-id>",
  "hash": "<hash-from-search-result>"
}
```

Returns `{hash, value, path, lineStart, lineEnd}`.

## Scoping

Code search is always project-scoped. Passing `scope=shared` with `kind=code` or
`kind=both` returns an empty code section (shared has no code rows).

Workspace scope works normally: `kind=code` with a `workspaceId` searches that
workspace's project scope.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `code-engine-unloadable` warning | Manifest or model files missing/corrupt | `ai-raccoon model set code local <dir>` or `ai-raccoon settings model code reset` |
| No code results, warning about missing engine | No code engine configured | `ai-raccoon model set code default` |
| Code results are FTS5-only (no vector hits) | Engine configured but embedding pending | Wait for the maintenance embed-drain to finish, or check `memory_performance` |

## See also

- [Configure embedding engines](configure-embedding-engines.md) — switch or download models
- [Code corpus feature spec](../features/code-corpus/) — behavioral contract
- [ADR-0085: A second code-only corpus in the same bank](../adr/0085-a-second-code-only-corpus-in-the-same-bank.md)
- [Tool contract reference](../reference/agent-memory-server.md) — full `memory_search` and `code_get` parameter docs
