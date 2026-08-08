---
name: mcp-index
description: >-
  Use when MCP tool selection needs help — the agent keeps picking the wrong tool, server tool
  definitions are bloating the prompt, or MCP servers were just added or removed. Manages
  .ai-badger/mcp-tools.json: tags, intent descriptions, and the hook that recommends tools per
  turn.
version: 0.1.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [mcp, indexing, tool-discovery, prompt-compression]
    related_skills: [hermes-mcp-setup]
---

# MCP Tool Index

## Prerequisites

The index itself is JSON — no dependency needed to read, tag, intent, or list it. PyYAML is
only needed to read a project's not-yet-migrated legacy `mcp-tools.yaml`:
```bash
python3 -m pip install pyyaml   # also in $AI_BADGER/engine/requirements.txt
```
Without it, a legacy-YAML-only project falls back to a stricter built-in parser and, if that
can't safely read the file, refuses with a hint rather than a traceback (see `migrate` below).

Manage `.ai-badger/mcp-tools.json` — a machine-readable index that maps every MCP server tool to tags (for filtering) and intent (for semantic matching). The index feeds the `ai_badger_hooks.py` plugin's `pre_llm_call` hook, which injects relevant tool recommendations into every LLM turn.

## Overview

MCP servers expose 40+ tools per server. Agents scan ALL tool definitions in the system prompt, wasting tokens and sometimes picking the wrong tool (e.g., `search_text` when `search_in_files_by_text` is faster). The index solves this by:

1. **Tagging** each tool with category labels (`[build]`, `[database, sql]`, `[diagnostic]`)
2. **Intent description** for semantic disambiguation ("Compile the solution" vs "List project run configs")
3. **Hook-driven recommendation** — the `pre_llm_call` hook loads the index, extracts domain keywords from the user's message, and injects top-N matching tools as a context hint

Tags and intents come from three places, in descending authority — and each entry records which
one spoke, in an `origin` field:

| `origin` | source | survives `update`? |
|---|---|---|
| `manual` | you, via `mcp-index tag` / `mcp-index intent` | **yes** — a human outranks both |
| `catalog` | `features/<stack>/mcp/<server>/tools.json` in the framework | refreshed from the catalog |
| `heuristic` | `_auto_tags` guessing from the tool name | replaced as soon as the catalog covers the tool |

The catalog is a curation library, not a completeness claim: it applies to a server however that
server arrived (project `.mcp.json`, user-global config, a plugin, a cloud connector), and the
heuristics are the last resort for the servers it does not know.

## When to Use

- **After `hermes mcp add`** — run `mcp-index update` to index new tools
- **Before complex multi-tool tasks** — run `mcp-index validate` to ensure the index is complete
- **When the agent picks the wrong tool** — run `mcp-index tag <tool> <correct-tags...>` to fix tagging
- **After removing MCP servers** — run `mcp-index update` to mark stale tools

## When NOT to Use

- A one-off tool lookup — read the index JSON (`.ai-badger/mcp-tools.json`) directly
- Writing a brand-new MCP server — use `hermes-mcp-setup`
- No MCP servers in the project — there is nothing to index
- A wrong-tool call that is a one-off — tag it, don't re-architect

## Tag Taxonomy

Tags come from a closed set in `features/common/mcp-tags.json`:

| Category | Tags |
|---|---|
| Language | `csharp`, `typescript`, `javascript`, `python`, `sql`, `css`, `html` |
| Action | `navigation`, `diagnostic`, `build`, `run`, `refactoring`, `search`, `read`, `write`, `terminal` |
| Domain | `database`, `tracing`, `opentelemetry`, `browser`, `dotnet`, `semantic`, `files` |
| Meta | `batch`, `slow`, `unsafe` |

Tools auto-tagged as `[general]` need manual curation.

## Commands

### `init` — create the index

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py init --target <project-root>
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py init --target <project-root> --host hermes
```

Asks the host CLIs for their MCP servers (see *Where the server list comes from* below), describes
each tool from the catalog where it can and by name heuristics otherwise, records each server's
`status`, and writes `.ai-badger/mcp-tools.json`. Prints which listing answered and which sources
were skipped, then how many tools were tagged as `general` and which servers reported no tools.

**Completion criterion:** `.ai-badger/mcp-tools.json` exists with all current MCP tools indexed.

### `update` — sync index with current MCP state

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py update --target <project-root>
```

Adds new tools, marks vanished ones with `status: removed` (preserving their curation), adds new
MCP servers, and restates every server's `status`. **Preserves manually-set tags and intents on
existing tools**; a tool the catalog describes is re-described from it unless `origin` is `manual`,
and the tools that changed are printed by name. Takes `--host` like `init`.

A listing that carries **no tool detail at all** (every source but `hermes mcp list --json`) cannot
tell "this server is gone" from "this is another host's listing", so a source it does not name is
**left untouched** — same status, same tools — and named in the output. Only a listing that carries
tools can move a source to `absent` and its tools to `removed`.

**Completion criterion:** All current MCP tools appear in the index; removed tools have `status: removed`.

### `validate` — check index quality

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py validate --target <project-root>
```

Fails (exit code 1) if any tool has `[general]` tags, empty tags, missing intent, or invalid tags.

**Completion criterion:** Exit 0 with "OK: N tool(s) validated".

### `tag` — set tags for a tool

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py tag rider:search_symbol semantic search --target <project-root>
```

Validates tags against the taxonomy. Rejects unknown tags.

**Completion criterion:** `mcp-index list` shows the tool with the new tags.

### `intent` — set intent for a tool

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py intent rider:get_file_problems "Check a file for Rider code analysis errors and warnings" --target <project-root>
```

Requires ≥10 characters. Use a concise one-sentence description that would help an agent pick this tool from a list of candidates.

**Completion criterion:** `mcp-index list` shows the tool with the new intent.

### `list` — display tools

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py list --target <project-root>
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py list --tag diagnostic --target <project-root>
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py list --untagged --target <project-root>
```

**Completion criterion:** All matching tools are displayed with server, tags, and intent.

### `migrate` — one-shot legacy YAML to JSON conversion

```bash
python3 .ai-badger/skills/mcp-index/scripts/mcp_index.py migrate --target <project-root>
```

Converts a legacy `.ai-badger/mcp-tools.yaml` to `.ai-badger/mcp-tools.json`, preserving every
curated tag and intent. A no-op (exit 0) if the project already has `mcp-tools.json`. Any other
write command (`init`/`update`/`tag`/`intent`) migrates a legacy file the same way as a side
effect — `migrate` exists for a project that only wants the conversion, without also running
`init`/`update` against a live MCP source. The old file is renamed to `mcp-tools.yaml.migrated`,
never deleted.

If PyYAML is absent and the legacy file falls outside the built-in parser's verified subset,
`migrate` refuses rather than risk a silently wrong conversion, and prints two remedies:
install PyYAML and re-run, or regenerate via `mcp-index init --from-json` (which loses curated
tags and intents — stated so the cost is explicit before choosing it).

**Completion criterion:** `.ai-badger/mcp-tools.json` exists with the same tools, tags, and
intents the legacy file had; `.ai-badger/mcp-tools.yaml.migrated` exists.

## Where the server list comes from

`hermes mcp list --json` — the source this skill was built on — **no longer exists**: the installed
hermes answers `error: unrecognized arguments: --json` (measured 2026-07, issue #188). `init` and
`update` therefore ask three sources in order and take the first that lists a server:

| order | source | what it carries |
|---|---|---|
| 1 | `hermes mcp list --json` | server names **and their tools** — the only source that can |
| 2 | `claude mcp list` | every server, plus a reachability phrase per server; no tools. Health-checks each server first (~14s for 17) |
| 3 | `hermes mcp list` | server names and an enabled flag, from the text table; no tools |

`--host hermes` or `--host claude` restricts the chain to one CLI — use it when the other is slow,
noisy, or listing the wrong project's servers. `--from-json <document>` skips the hosts entirely and
reads a saved `hermes mcp list --json` document.

If no source answers, both commands **refuse** and print what each one said — a missing CLI, a
non-zero exit with its error line, or an empty listing. They never write a half-index.

> Status meanings: read references/status.md if `update` reports a status other than `ok` (or when a silent server needs explaining).

> Auto-tagging rules: read references/heuristics.md when a tool came back `[general]` and you are deciding whether to curate it or extend the catalog.

## Gotchas

1. **Auto-tagging covers only ~60% of tools.** Expect 10-20 tools tagged as `[general]` after `init`. Curate them with `mcp-index tag`, or — better, if the server is worth describing for every project — add its `tools.json` to the framework's mcp catalog.
2. **The first `update` after upgrading rewrites heuristic tags.** Any tool the catalog describes gets the curated tags and intent, because an entry with no `origin` cannot be told apart from a guess. Tools curated with `mcp-index tag`/`intent` from now on are marked `manual` and left alone.
3. **Index goes stale after adding MCP servers.** Run `mcp-index update` after every `hermes mcp add` or `hermes mcp remove`.
4. **Tags aren't free-form.** Use only tags from the taxonomy. `mcp-index tag` rejects unknown tags.
5. **Intent field is for disambiguation, not documentation.** A 10-30 word sentence beats a paragraph. Write it to answer: "why would I pick this tool over a sibling with the same tags?"
6. **The `list` filter uses substring matching on tool names.** Avoid naming tools with names that are substrings of each other in tests.
7. **`--target` is required.** The script does not default to `.` — always pass `--target <path>`.
8. **The two hosts name the same server differently.** `claude mcp list` decorates a server with
   where it routes it from — `plugin:<plugin>:<server>` for a plugin-provided server,
   `claude.ai <Name>` for a connector — where `hermes mcp list` prints the bare name. A source keeps
   whatever the host called it, so switching `--host` adds sources rather than renaming them, and the
   sources the new listing cannot speak for are left untouched rather than removed. The **mcp
   catalog** does reach through the decoration: a listing name is matched against the decorated name
   first and the undecorated server second, so `plugin:ai-badger:code-review-graph` picks up
   `features/common/mcp/code-review-graph/tools.json`. Curating a specific plugin's copy is possible
   — set `"server": "plugin:<plugin>:<server>"` in its `tools.json` and the exact key wins — because
   two plugins may ship same-named servers, which is also why the bare name is never rewritten.

## Verification Checklist

- [ ] `mcp-index init` produces `.ai-badger/mcp-tools.json` with all current MCP servers
- [ ] `mcp-index validate` exits 0
- [ ] No tools are tagged `[general]` (all manually curated)
- [ ] Every tool has a meaningful intent (≥10 chars, describes what it does)
- [ ] `mcp-index list` shows all expected tools
- [ ] All tests pass: `python3 -m pytest tests/test_mcp_index.py -q`
