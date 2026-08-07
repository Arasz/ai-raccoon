# Record format

On disk they are compact, one JSON object per line:

```json
{"t":"2026-07-27T09:22:56+00:00","c":"ai_badger_hooks/session_start","e":"skip","v":"0.30.0","p":"/repo"}
```

The single-letter keys are a budget, not cosmetics: a record must stay under `PIPE_BUF`
(4096 bytes) for concurrent appends to be atomic, and the fixed keys repeat on every line. Fields a caller adds usually keep their full names — they are the payload, and they do not repeat. The MCP-retrieval fields below are the exception:
that event can fire on every turn, so they are compacted the same way the fixed keys are.

## Record keys

| Key | Meaning                                                           |
|-----|-------------------------------------------------------------------|
| `t` | timestamp, UTC, seconds                                           |
| `c` | component — which hook or script                                  |
| `e` | event — `start`, `skip`, or a domain outcome                      |
| `v` | version of the copy of the code that ran                          |
| `p` | project directory, when determinable                              |
| `n` | project name from `.ai-badger/config.json`, read once per process |
| `s` | session id, when the host supplies one                            |

## Retrieval events

| Event               | Means                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
|---------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `hit`               | At least one candidate cleared the match threshold — something was recommended.                                                                                                                                                                                                                                                                                                                                                                                |
| `gate`              | Candidates were scored and **all** fell below the threshold. A correct, frequent outcome, not a failure — but previously indistinguishable from `absent`.                                                                                                                                                                                                                                                                                                      |
| `no_terms`          | The tokenizer read nothing scoreable from the query, so **no candidate was ever scored against the threshold**. `_score_all_tools` short-circuits to `[]` on the same empty-tokenize condition that fires this event, so `o` (top candidates) is always empty and `h` (threshold) is absent — there is no "suppressed top scorer" to look at, only the index's tool count (`d`), which is unaffected. Distinct from `gate`, where scoring did happen and lost. |
| `absent`            | No `.ai-badger/mcp-tools.json` **and** no `.ai-badger/mcp-tools.yaml` — there is nothing to migrate, nothing to search.                                                                                                                                                                                                                                                                                                                                        |
| `legacy`            | A `.ai-badger/mcp-tools.yaml` exists but hasn't been migrated to `.json` yet (issue #145) — the JSON-only hook reader can't read it, but it is not the same absence as `absent`: run `mcp-index migrate` (or any write command) to fix it.                                                                                                                                                                                                                     |
| `known` / `unknown` | A tool call was checked against the index after the fact — was it a tool the index knows about? `unknown` means the tool's server *is* indexed (including with `status: empty`/`unknown`, which is still present) and this tool is not in it: run `mcp-index update`.                                                                                                                                                                                          |
| `server_unindexed`  | The called tool's server is named by no source in the index at all (issue #170) — added after the index was built, or never indexed. The strongest "the index is stale" signal, and until 0.51.1 the one that emitted nothing. Remedy is indexing the server, not updating a source. Only server-qualified tool names (`server:tool`) are checked; a built-in like `write_file` is not an MCP tool and is not recorded.                                        |

A "no match" that reads identically to "no index" is a bug that hides itself; that is why these are separate events rather than one silent no-op. `legacy` exists for the same reason: without it, "how many projects are stuck on the legacy
format" collapses into "how many have no index at all", and the migration this event exists to help track becomes unmeasurable. `server_unindexed`
is the same rule applied to a silence that had no name: the check simply fell off the end of its loop, so "the index has never heard of this server" was indistinguishable from "the hook never ran".

## Retrieval keys

| Key | Meaning                                                                                                  |
|-----|----------------------------------------------------------------------------------------------------------|
| `q` | the query — the user's message that drove retrieval                                                      |
| `g` | terms/tags extracted from the query, comma-joined                                                        |
| `d` | how many tools in the index were considered                                                              |
| `o` | the top 3 scored candidates as `name:score`, comma-joined (empty on `no_terms`: nothing was ever scored) |
| `r` | what was actually returned (empty on `gate`; absent on `absent` and `legacy`)                            |
| `h` | the match threshold in force, so a later threshold change is attributable                                |
| `l` | the tool name, for the `known`/`unknown`/`server_unindexed` check                                        |
