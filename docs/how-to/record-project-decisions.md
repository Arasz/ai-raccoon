# Record project decisions

AiRaccoon keeps a project's decisions as typed records in the bank (2.4.0, [ADR-0142](../adr/0142-native-project-decision-graph.md)).
Each decision has versions, a status and links to other decisions, and each belongs to the task that
recorded it. Agents use the MCP decision tools. People use `ai-raccoon decisions`, which goes
through the running server and never opens the bank itself. Both reach the same records under the
same project gate.

Every parameter is in the [tool reference](../reference/agent-memory-server.md) and every flag in the
[CLI reference](../reference/cli-reference.md#decisions). This page is the order to call them in.

## Before you start

The project id must be registered. Find or register it with `get_project_id`, or from a terminal:

```bash
ai-raccoon project id check <project-id>
ai-raccoon project id register <project-id>
```
Decision tools never guess the project from the working directory, so pass `projectId` on every call.

## 1. Register the task

Register the task at the start of the work, before its first decision. Export and preview select by
task, and "latest" means the task registered last on the server, not the one edited last.

```bash
ai-raccoon decisions task register --project-id <project-id> --task-id fix-sync --title "Fix sync retries"
```
Over MCP, call `register_decision_task` with `projectId`, `taskId`, `sessionId`, `requestId` and `title`.

Every write carries a `sessionId` and a `requestId`. Sending the same pair again replays the first
result, so a retry after a timeout never registers or records twice. The CLI generates the pair
when you leave it out and prints it to stderr; pass it back to retry.

## 2. Record decisions

Write the decision fields to a JSON file and record it under the registered task:

```json
{"title":"Retry sync with backoff","category":"architecture","scenario":"Sync fails on a flaky network",
 "outcome":"Retry three times with exponential backoff","reasoning":"Transient failures dominate the logs"}
```
```bash
ai-raccoon decisions record --project-id <project-id> --task-id fix-sync --input decision.json
```
Over MCP, call `record_decision` with the same fields. A new decision is `proposed` unless you pass
`status=accepted`. Change it later with `amend_decision` (new content, same status) or
`transition_decision` (proposed to accepted or rejected; accepted to deprecated). Both take the
`expectedVersion` you last read, and a stale one is refused as a conflict. Replace an accepted
decision with `supersede_decision`; rejected, superseded and deprecated decisions never change again.

## 3. Link decisions

`link_decisions` adds a `depends_on`, `supports` or `contradicts` edge from `sourceId` to `targetId`.
It takes `expectedProjectRevision`, the `projectRevision` of your last read; any write since then
makes it stale and the link is refused, so read again and resend. `unlink_decisions` removes an edge
and keeps it in history. Supersession edges cannot be removed. The CLI has no link verb; links are
an agent operation.

## 4. Read them back

| Need | MCP tool | CLI |
|---|---|---|
| One decision, optionally every version | `get_decision` | `decisions get <id> [--history]` |
| Current decisions by text or category | `query_decisions` | `decisions query [--query <text>] [--category <c>]` |
| Earlier decisions for a similar scenario | `find_decision_precedents` | none |
| The decisions a decision depends on or that depend on it | `get_decision_graph` | none |
| A task's complete snapshot | `export_decisions` (up to 1 MiB) | `decisions export` (no cap) |
| A compact, budgeted summary | `preview_decisions` | `decisions preview` |

Export and preview take the latest task by default. `--tasks <n>` takes the last `n` tasks, `--all`
every task, and `--task-id` names tasks (repeatable). Over MCP pass `selector` with `last`, `all` or
`taskIds`. The same selection returns the same records over HTTP, MCP and the CLI.

```bash
ai-raccoon decisions preview --project-id <project-id>
ai-raccoon decisions export --project-id <project-id> --tasks 3 --output decisions.json
```
The export is plaintext even for an encrypted bank. `--output` writes an owner-only file and
publishes it only once it is complete.

## Access modes

The project's access mode applies to the decision tools and the CLI the same way it applies to
memory. Reads work in every mode. Writes (register, record, amend, transition, supersede, link and
unlink) need `rw` or `full`.

```bash
ai-raccoon settings access set <project-id> ro
```
In `ro` mode HTTP answers 422 with kind `access-denied`, MCP returns an `access-denied:` tool error,
and the CLI exits 17 with the same message, and nothing is written. The mode is stored in the bank,
so it holds across a server restart.

## Limits

- Graph traversal depth is 0 to 10 and `maxNodes` 1 to 500.
- Preview shows 1 to 100 decisions, 0 to 200 edges and 1000 to 50000 characters (defaults 20, 30, 12000). `--all` still applies these limits; only export is complete.
- Query text is literal, 1 to 2000 characters, matched by full-text search. Precedent search is full-text too, not semantic similarity.
- Decisions are committed project records. They do not go into workspaces, are not promoted to `shared`, and are not part of cloud sync. Back up the bank to keep them.
- There is no project deletion for a project that owns decisions, and `repair project-ids` holds such an id back instead of folding or retiring it.

## Semantica

The native graph does not replace a Semantica setup yet. Keep the Semantica MCP server entry and the
agent instructions that point to it. Importing existing Semantica exports is not in 2.4.0 (it is
tracked in #924), and switching the agent framework's instructions to the native tools is a
separate change made after this release is accepted.

## Upgrade and roll back

2.4.0 migrates the bank to schema v23. Older binaries refuse to open it with
`bank schema v23 is newer than this binary supports (v22); update ai-raccoon`
([ADR-0019](../adr/0019-forward-version-write-guard.md)), and there is no downgrade migration.

Before upgrading, stop every `ai-raccoon` process (close the agent clients, then check `ps`) and copy
the whole data root, `~/.ai-raccoon` by default. The copy holds `memory.db`, its `-wal` and `-shm`
files and the key sidecars, so it opens with the same key.

To roll back, stop every `ai-raccoon` process, put the copy back in place of the data root, and
install the previous tool:

```bash
dotnet tool update -g ai-raccoon --version 2.3.0 --allow-downgrade
```
Memory written after the upgrade, and every native decision, is lost by a rollback. Export the
decisions first with `ai-raccoon decisions export --project-id <project-id> --all --output <file>` if you need them. The
Semantica configuration you kept still works with 2.3.0.
