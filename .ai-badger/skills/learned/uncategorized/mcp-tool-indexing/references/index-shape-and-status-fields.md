# Verified index-shape facts & status-preservation recipe (2026-08-06)

Session-verified details for the mcp-tool-indexing rebuild workflow, measured on
`.ai-badger/mcp-tools.json` (ai-raccoon repo, 18 sources, 189 tools).

## Index file shape

- Top level: `{version, generated_at, sources[]}`.
- Each source is `{name, status, tools}` where **`tools` is a DICT keyed by tool name**: `tools: { "<tool-name>": {"tags": [...], "intent": "...", "origin": "catalog|manual"} }`. NOT a list — a name-diff script that iterates `tools` as a
  list dies with
  `TypeError: string indices must be integers`. Diff against `set(tools.keys())`.
- Tool counts per server in the index equal the live session's `mcp__<server>__<tool>`
  catalog counts — a count-only diff is NOT enough; diff names too (counts can match while names drifted, and vice versa).

## Status preservation — the `--from-json` document fields that keep non-ok statuses

`server_status()` (tool_descriptions.py) resolves status in order:
`host_status` phrase → `enabled: false` → `!tools_known` → ok/empty. A `tools_known: false` entry WITHOUT the extra fields restates as `unknown`, which silently LOSES an existing `disabled` or `unreachable` status. To preserve:

- **`disabled`** (e.g. llmstudio, superpowers): pass `"enabled": false` alongside
  `"tools_known": false` → status stays `disabled`.
- **`unreachable`** (e.g. a claude-only plugin the hermes session cannot see):
  pass `"host_status": "failed to connect"` → status stays `unreachable`. This is the legitimate case for carrying `host_status`: the source is invisible to this host, so the phrase is the only truth. The "do NOT carry host_status" rule
  applies to sources whose ok status would be wrongly downgraded by another host's phrase.

Verified end state: 7 live servers `ok` with exact session tool counts; hermes bridge + rider `unknown` with tools untouched; plugin:github:github `unreachable`; llmstudio/superpowers `disabled`.

## Working-tree reconciliation

If `.ai-badger/mcp-tools.json` is already modified when the rebuild starts (parallel session ran `update` earlier), diff BEFORE running: the pending change may BE the bug (a status downgrade to `unknown` from a tool-less listing). The
rebuild then reconciles it; the final `git diff` vs HEAD should shrink to `generated_at` only. Check `git log --oneline -1 -- .ai-badger/mcp-tools.json` after the run — if a parallel session committed mid-run, re-verify counts against
`git show HEAD:<file>`.
