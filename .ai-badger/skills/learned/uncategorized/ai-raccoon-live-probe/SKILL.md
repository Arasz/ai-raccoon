---
name: ai-raccoon-live-probe
description: >-
  Use when probing a live AiRaccoon server.
---

# Probing a live AiRaccoon server

The mechanics of talking to a running ai-raccoon server and reading its bank, learned the hard way on the 2026-08-20 1.27.2 checklist + fusion-retest sessions. Complements ai-raccoon-manual-checklist (the run protocol; this skill is the
mechanics its items run on) — use it for manual checklist items, retrieval-quality investigations, settings-state questions, "does the running binary behave like X".

## Starting a scratch server (never touch the user's)

- `ai-raccoon --data-root <scratch-dir> serve --port 0 > serve.log 2>&1` (backgrounded). The user's live server usually owns 7721 — never bind it; `--restart` would cycle it.
- Read the bound port from the server's OWN output (`grep "Now listening" serve.log`), never assume one.
- Bearer token lives at `<data-root>/mcp-token`; send `Authorization: Bearer <token>`.

## The MCP streamable-HTTP wire protocol

- Endpoint: `POST http://127.0.0.1:<port>/mcp`; headers `Content-Type: application/json`,
  `Accept: application/json, text/event-stream`.
- **Every response is an SSE frame**, even non-streaming ones:
  `event: message\ndata: {"jsonrpc":"2.0","id":1,"result":{...}}\n\n`. Parse: collect lines starting `data:`, take the LAST, json.loads it. A parser that only handles a bare `data:` prefix throws JSONDecodeError on the first call. (Behind a
  proxy the body may be plain JSON — handle both.)
- **tools/call payloads are JSON strings inside `content[0].text`** — parse the text a second time. Refusals/errors carry `"isError": true` with the message in the same text slot (`invalid-params: ...`, `unknown-hash: ...`). Missing-param
  errors from the MCP layer are `invalid-argument: The arguments dictionary is missing a value for the
  required parameter 'X'.` — that is a client bug, not a product refusal.
- **The query-guard warn annotation lives at `data.warning` INSIDE the envelope's `data`
  object** (next to `results`), never top-level, never in `meta`. A probe that checks only top-level keys concludes the annotate tier is broken when it is working. The clean control's `data` has no `warning` key at all.
- tools/list and prompts/list return their payload directly (no text wrapper) — count from the parsed structure, not by grepping `"name"` (schema fields pollute the count).

## Reading the bank while the server runs

- `sqlite3 <data-root>/memory.db` — plain open works; the `-readonly` CLI flag can fail on macOS with `unable to open database file (14)` even when the file is readable. For someone else's bank use `PRAGMA query_only=1` after a plain open,
  or `?mode=ro` URI.
- **A fresh `--data-root` bank inherits NO settings** — `SELECT key, value FROM settings`
  has few/no rows, so code defaults govern (fusion reorder off, structural detector off, noise/query-guard on). A VACUUM-copy bank inherits the live settings. Before blaming or crediting any settings-gated feature, read the settings table
  and name which state you measured.
- **vec0 virtual tables are unreadable to system sqlite** (no vec0 module). Read the shadow table `vec_entries_vector_chunks00`: ONE row per 1024-vector block, `vectors`
  BLOB of float32 LE, 384 dims/vector, slot = entry rowid - 1 (0-based). Unused slots are all-zero — check the mapping before claiming an entry was never embedded (an off-by-one slice misreads a healthy bank as unembedded).

## CLI verbs route over the server

`ai-raccoon --data-root <root> --port <bound-port> <verb>` — settings/model/watch/noise/ repair/serve all route through the running server (the CLI never opens the bank itself). Read-only verbs are safe against any server; write verbs only
against your own scratch server. Port 0 is serve-only; the settings/model/etc. verbs need the actual bound port.

## Verifying settings-gated behaviour (A/B toggle)

To test whether a settings-gated component explains a behaviour: toggle it on the SAME bank, re-run the SAME queries, then confirm the flag ENGAGED via its own telemetry before trusting the comparison (e.g. fusion writes
`search.fusion.top1_changed` /
`top1_rank_delta` / `top5_moved` gauge rows only when enabled — nonzero rows = the enabled path ran, zero rows on the disabled side = the A/B is valid). Settings toggles:
`ai-raccoon --port <p> settings retrieval fusion enable|disable|show`, `settings
queryguard structural enable|disable`, `settings noise enable|disable`.

## Known behavioural facts (re-derive from source before relying)

- Noise policy (HermesBackgroundProcessLog) matches a prefix or an EXACT `received signal
  <digits>` body: `received signal 15` is rejected; `received signal 15, terminating`
  (trailing text) and mid-sentence mentions STORE. Test the exact body from the policy class; near-miss variants are the negative control.
- `fusion.noRegression.enabled.global` is a REORDER (promote each result to
  `min(fused rank, best-leg rank)`), default false, NOT telemetry-only. Enabling it rescues only queries where a single leg already ranked the target 1st — it neither causes nor fixes weak-model ranking (generic short texts near the
  embedding centroid dominate RRF; see software-development/hybrid-retrieval-fusion).
- Query-guard refuse shapes require the FULL notification shape (prefix + `completed
  normally` + `Command:`), not just the prefix.

## References

- ai-raccoon-manual-checklist (skill) — the run protocol this probe mechanics serves.
- software-development/hybrid-retrieval-fusion — fusion/ranking analysis, including the live-but-query-insensitive vector-leg reference (alien-token liveness probe, FTS-leg isolation, vector inspection).
