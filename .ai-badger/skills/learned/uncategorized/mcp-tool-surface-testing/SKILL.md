---
name: mcp-tool-surface-testing
description: Use when testing all tools exported by an MCP server.
---

# MCP tool-surface testing

Systematic black-box test of every tool an MCP server exports, with the expectations
written down before any call and a results doc as the deliverable. The user's explicit
method (verified on the AiRaccoon 21-tool surface, 2026-08-06): **expectations first,
then call, then compare** — never reverse the order, or the test degenerates into
exploratory guessing.

## When to use

- "test all tools exported by X", "full-test-all-tools", "does every MCP tool work"
- A server's surface changed (tools added/removed) and you want a live contract audit
- Before writing agent guidance that depends on tool response shapes

## Procedure

1. **Enumerate the surface.** Sources, in order: the project's mcp-index
   (`.ai-badger/mcp-tools.json` — per-server tool lists), the host's tool catalog
   (deferred tool list), `list_prompts` for prompt tools. Note the count (e.g. 19 memory
   tools + 2 prompts = 21).
2. **Gather the contract, then write expectations.** Three sources, all read BEFORE any
   call:
   - the docs tool table (often stale — flag drift as a finding, don't silently follow it)
   - live tool schemas (`tool_describe` per tool — exact param names, required flags)
   - mcp-index intents (semantic expectations)
   Write an expectations table into the results doc: tool | expected behaviour |
   expected response shape. This table is the test oracle.
3. **Safety first.** Use a DEDICATED test project id (never a real project's data).
   Destructive tools (delete, delete_context, sweep, discard, share) get positive AND
   negative controls (bogus hash → `{deleted: 0}`). Watch/ingest tests use temp dirs.
   Clean up afterwards and verify zero residue (`memory_stats` shows the test context
   gone). If the server needs per-project config (watch enable/scope), restore it after.
4. **Execute in dependency order, batching independents.** Write → search (verify hash
   round-trip at rank 1) → share → search shared; workspace begin → write → status →
   consolidate → verify → discard → verify. Batch independent calls in parallel; never
   race dependent ones (status before the write it should show).
5. **Compare + verdict.** PASS (matches expectation) / PARTIAL (works, differs from
   documented/ideal shape) / FAIL. When the live contract contradicts a prior assumption,
   mark it as an expectation CORRECTION — the live server outranks docs, index intents,
   and memory.
6. **Perfect-response table.** For each tool, the ideal response shape — what the docs
   promise or what would make the response unambiguous (e.g. "echo the new hash on
   share", "object instead of stringified JSON").
7. **Findings section.** Docs drift vs live contract, index-intent overstatements
   (e.g. a tool documented as "denied in rw" that actually succeeds on this deployment),
   response-key inconsistencies between sibling tools, error-message gaps (typed error
   that doesn't name its remedy).

## Deliverable + user preference

- Results doc in `docs/work/YYYY-MM-DD-<server>-tools-test.md` (or the repo's work-docs
  convention). Use `templates/results-doc.md` as the skeleton.
- **Work on main and commit + push the doc directly** — the user wants to see the result
  in the repo, not in a worktree or behind a PR (explicit correction 2026-08-06). Only
  commit the doc + task-state files; leave other sessions' uncommitted files untouched.
- If the repo runs the `task` skill: register the task with `--no-worktree` and follow
  the finish protocol (state.json entry, tracker finish).

## Pitfalls

- **Docs tables lag the live contract.** Verified on AiRaccoon: `memory_workspace_discard`
  documented as `{discarded}`, actual `{deleted}`; `memory_list` documented as a json
  tree, actual a stringified string; `memory_write` schema has params the docs table
  lacks. The live schema + response is the contract.
- **Access-tier claims are deployment-dependent.** An index intent saying "denied in rw"
  may be false on a full-access deployment — probe once, don't promise a denial.
- **Derived identity surprises:** hashes/paths are often derived (e.g. path =
  sha256(content).md, hash = sha256(path+content)) and promotion tiers may re-derive
  them (shared row gets a new hash). Verify round-trips instead of assuming equality.
- **Config-gated tools fail with typed errors until configured** (watching-disabled,
  path-outside-scope, sync-not-configured). These are valid test results — capture the
  error, apply the remedy (CLI config), retry, and record both phases.
- **Response envelope differences** across a host bridge: some tools return bare JSON,
  others `{"result": "<stringified JSON>"}`. Note it; parse defensively.
- **Cleanup must be verified, not assumed**: after delete_context + share-row delete +
  config restore, one final stats call proving the test project is gone.

## Auditing the mcp-index intents you use as expectations

`mcp-index validate` proves completeness (no `[general]`, no missing intents), not
quality — terse one-liners still fail disambiguation. Audit the index programmatically
before trusting it as an expectation source:

```python
import json
from collections import Counter
d = json.load(open('.ai-badger/mcp-tools.json'))
items = [(s['name'], tname, t) for s in d['sources'] for tname, t in s.get('tools', {}).items()]
print(Counter(t.get('origin') for _,_,t in items))          # manual/catalog/heuristic split
short = sorted([(s,n,t.get('intent','')) for s,n,t in items if len(t.get('intent','')) < 50],
               key=lambda x: len(x[2]))
for s,n,i in short: print(f"{s}:{n} -> {i!r}")
```

Heuristic-origin entries with short intents are the improvement queue; `mcp-index intent`
records `origin: manual` so updates preserve them (use the command, not direct JSON
edits — direct edits bypass origin bookkeeping).

## Worked example

AiRaccoon full surface test (21 tools, 35 calls, 33 PASS / 2 PARTIAL / 0 FAIL):
`ai-raccoon` repo → `docs/work/2026-08-06-ai-raccoon-tools-test.md`. The AiRaccoon
server-side quirks it uncovered live in the `ai-raccoon-pitfalls` skill — when a surface
test turns up behaviours, fold them into the server's pitfalls skill.
