# Search the code corpus

Search your project's source code alongside (or instead of) your memory bank.

## Prerequisites

A code-corpus embedding engine must be active. If you have not set one up yet:

```bash
ai-raccoon model code set default
```

This activates the bundled `granite-embedding-small-english-r2` model (fp16, 384-dim, the
same one memory uses) for the code corpus. Nothing is downloaded. (Before 1.47.0
this command downloaded the separate `faxenoff/code-daemon-embed-v1` model; a corpus
still on that model keeps it until you run the command again.) See
[Configure embedding engines](configure-embedding-engines.md#recipe-5-activate-the-code-corpuss-embedding-engine)
for details.

Without a code engine, `kind=code` and `kind=both` searches degrade to FTS5-only
(keyword matching, no vector similarity) and the server emits a warning. The
default `kind=both` search behaves the same way: memory results come back
normally, code results are keyword-only, and the response carries the warning.

## What gets indexed

Any file watched via `memory_watch_add` whose extension is in the code registry is
automatically ingested into the code corpus. The 37 supported extensions:

`.cs` `.fs` `.fsx` `.py` `.ts` `.tsx` `.js` `.jsx` `.mjs` `.cjs` `.go` `.rs` `.java` `.kt` `.kts`
`.swift` `.rb` `.php` `.c` `.h` `.cc` `.cpp` `.hpp` `.m` `.mm` `.scala` `.lua` `.html` `.htm` `.css` `.scss` `.sql`
`.vue` `.sh` `.tf` `.hcl` `.feature`

A file whose first 8,000 characters contain a NUL byte is treated as binary and skipped, whatever
its extension. The check applies to memory files too. A file that turns binary loses the chunks it
had.

Memory-owned extensions (`.md`, `.txt`, `.json`, etc.) are never ingested into the
code corpus. The two corpora are disjoint by design.

Any manifest dimension works for the code engine: `model code set local <dir>` reconciles
`vec_code` to whatever dimension the manifest declares, in the same transaction as
activation. There is no configure-time dimension gate (`ADR-0093`); the chunk-budget gate
(window at least 510 content tokens) is the only refusal left.

## Searching

Keyword matching splits identifiers, not just words: a query for `overlap` also matches a
chunk defining `WatchOverlapResolver`, because `camelCase`/`PascalCase`/`snake_case`/
`kebab-case` identifiers are split into a derived keyword column at ingest time and searched
alongside the raw text ([ADR-0109](../adr/0109-code-fts-carries-a-derived-identifiers-column.md)).
This applies to every kind=code/both search; there is no separate flag to enable it.

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
| `code-engine-unloadable` warning | Manifest or model files missing/corrupt | `ai-raccoon model code set local <dir>` or `ai-raccoon settings model code reset` |
| No code results, warning about missing engine | No code engine configured | `ai-raccoon model code set default` |
| Code results are FTS5-only (no vector hits) | Engine configured but embedding pending | Wait for the maintenance embed-drain to finish, or check `memory_performance` |

## See also

- [Configure embedding engines](configure-embedding-engines.md) — switch or download models
- [Code corpus feature spec](../features/code-corpus/) — behavioral contract
- [ADR-0085: A second code-only corpus in the same bank](../adr/0085-a-second-code-only-corpus-in-the-same-bank.md)
- [Tool contract reference](../reference/agent-memory-server.md) — full `memory_search` and `code_get` parameter docs
