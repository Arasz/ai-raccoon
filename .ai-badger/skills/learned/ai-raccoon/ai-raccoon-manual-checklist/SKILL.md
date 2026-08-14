---
name: ai-raccoon-manual-checklist
description: Use when running AiRaccoon's manual pre-flight/release checklist against a live build — derives the version and tool surface from the product rather than pinning them, records the command behind every answer, and writes the filled checklist to docs/work/checklist/.
---

# AiRaccoon manual checklist

The hand-run pass over a live build: the things `dotnet test` cannot answer because they
need a real install, a real server, and a real bank.

Replaces `1.9.1-manual-testing-checklist` and `ai-raccoon-state-checklist`, which were
deleted on 2026-08-14. Read "Why the old ones were deleted" before adding anything to this
skill — every failure listed there is easy to reintroduce.

## Never

- **Never write to the user's live bank** at `~/.ai-raccoon`. Read it only through
  `?mode=ro` / `PRAGMA query_only=1`. The checklist's own writes go to a scratch data root
  you create with `--data-root`.
- **Never bind the default port** (7721) for a checklist step that starts a server — the
  user's own server is usually on it. Use `--port 0` and read the bound port back.
- **Never mark an item `accepted` from a plan.** `accepted` means you ran the command and
  read its output. See `evidence` below.

## Process

1. Copy `templates/checklist-template.json` to
   `docs/work/checklist/<yyyy-mm-dd>-<what-you-are-checking>.json`. Create the directory if
   it does not exist. Results live under `docs/work/checklist/` — never the repo root and
   never `.ai-raccoon/`, which is a bank directory, not a reports directory.
2. **Derive the facts the checklist compares against, first, and write them into
   `derived`** — do not type them from memory or from a previous run:
   - version → `grep '<PackageVersion>' src/AiRaccoon/AiRaccoon.csproj`
   - MCP tool count → `grep -rc 'McpServerTool' src/AiRaccoon/Tools/*.cs | awk -F: '{s+=$2} END {print s}'`
   - MCP prompt count → `grep -rc 'McpServerPrompt' src/AiRaccoon/Prompts/*.cs`
   The checklist then asserts the running binary matches what the tree says. A count typed
   into the template is a second copy of a number that already exists, and it goes stale
   silently (`.ai-badger/invariants/derive-or-delete-the-list.md`).
3. Work each item. For every one, fill in:
   - `command` — the exact command or tool call you ran.
   - `evidence` — the output you read, verbatim and trimmed to the deciding lines.
   - `observed-result` — what it means.
   - `status` — `pass`, `fail`, or `skipped`. `skipped` needs a reason; it is not a pass.
   - `accepted` + `acceptation-reason` — whether the observed result is acceptable, and why.
     A `fail` may still be `accepted` (a known, tracked defect) as long as the reason names
     where it is tracked.
4. Items whose feature no longer exists are **deleted from the template**, not marked
   skipped. A step for a removed feature is worse than no step: it either fails forever or
   gets waved through.
5. Report the summary: counts by status, and every `fail` with its evidence line.

## Scope

Derived per run from the product, not listed here — see step 2. The stable shape is:

- **Build and install**: Release build, pack, force-update the global tool; `ai-raccoon --version`
  matches the derived version.
- **Server lifecycle**: `serve` starts, `serve --restart` cycles the server it finds, both on a
  non-default port.
- **Write path**: `memory_write` stores and returns a hash; a rejected write says so
  (`Stored: false` with a reason — ADR-0032) rather than returning a fabricated entry.
- **Read path**: `memory_search` returns the written entry; `memory_get` returns its content by
  hash (ADR-0035); a `file#section` anchor resolves its exact chunk.
- **Noise filtering**: the deterministic write-path policy rejects what it claims to reject and
  the rejected content is retrievable from the noise store (ADR-0039). Check which policies are
  registered before writing steps for them.
- **Read-path query guard**: refuse/annotate tiers behave as ADR-0040 describes; the structural
  detector (ADR-0041) is off unless armed via `queryguard structural enable`.
- **File watch**: `memory_watch_status` reflects live registrations.
- **Promotion queue**: `memory_promotion_list` reports candidates accurately.
- **Full MCP surface**: every derived tool and prompt is reachable.
- **Observability**: event ids resolve against `docs/reference/logging-event-ids.md`.

## Why the old ones were deleted

Both predecessors had the same defects, and each is easy to reintroduce:

- **Facts pinned by hand.** One skill asserted `--version → 1.9.1` and "25 tools"; the tree was
  at 1.12.0 with 26 tools. The pins had been wrong for three releases and nothing noticed,
  because the only thing that compared them was a human reading two numbers.
- **Steps for a deleted feature.** Both still tested `ZeroShotEmbeddingNoisePolicy`, removed by
  ADR-0033. A step whose subject does not exist cannot pass honestly.
- **Results written into a bank directory.** `.ai-raccoon/state-checklist-*.json` put reports
  inside the directory name the product uses for banks.
- **No evidence field.** The template recorded a claim (`observed-result`) with no room for the
  command or the output behind it, so a filled checklist and a plausible-sounding one were
  indistinguishable after the fact.
- **`checked`/`accepted` booleans.** Two flags could not express "skipped": an unrun item and a
  failed one both read as `false`/`false`.
- **Two drifting copies.** `learned/ai-raccoon/` and `learned/uncategorized/` each held a copy;
  the `ai-raccoon` copy of the state checklist had lost its `templates/` directory entirely, so
  its own step 1 pointed at a file that was not there.
