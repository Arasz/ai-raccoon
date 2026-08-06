# Worked example: citation/grade audit of a Hermes memory-interface record

Audited 2026-08-06. Record: `ai-raccoon` worktree
`docs/work/2026-08-06-hermes-memory-provider-interface.md` (evidence-first-research
record, 10 findings, grades MEASURED×1 / READ×8 / INFERRED×1).
Source: `~/.hermes/hermes-agent/` at commit `8f2712725` (confirm with
`git -C ~/.hermes/hermes-agent rev-parse HEAD`).

**Verdict: APPROVE-WITH-CHANGES** — 0 MUST-FIX, 1 SHOULD-FIX, 4 NITs. Every grade was
honest; every one of the 20+ Evidence path:line ranges existed and supported its claim.

## The one SHOULD-FIX (the reusable lesson)

F5 claimed `prefetch_all(query)` "runs before each API call", cited only to
`agent/memory_manager.py` (the callee). `search_files` for `prefetch_all(` found the real
call site at `agent/turn_context.py:1171`, gated by `if not is_trivial_prompt(_query)` —
trivial turns skip provider prefetch entirely, and the manager additionally short-circuits
on bare skill invocations (`memory_manager.py:531-533`). The finding's header ("five
points in every agent turn") also overstated: tool-schema injection runs once at agent
init (`agent/agent_init.py:1765-1766`), not per turn; tool dispatch is conditional on the
model calling a provider tool.

Fix pattern: for any "when does the runtime call X" claim, grep the method name across the
repo, read the caller(s), and state the cadence with its gates.

## NITs worth remembering

- **Gloss drift on a measured finding**: the measured 7-of-8-unavailable output was exact,
  but the gloss "they need external credentials" fit only 5 of the 7 — byterover gates on
  brv CLI presence, hindsight can gate on a local runtime. Spot-check glosses across ALL
  enumerated items.
- **Dormant branch**: "add_provider always admits the built-in provider (registered
  first)" is the method's contract (`memory_manager.py:411-428`) but the only production
  `add_provider(` call passes the external provider; builtin branches are test-only
  (`tests/agent/test_memory_session_switch.py:98`). Report which level a claim describes.
- **Indirect doc citations can be legitimate**: `secret-source-plugin.md:12` supports a
  claim about memory providers by reference ("same policy as memory providers") — verify
  the referenced policy actually exists before accepting.
- **Tool quirk**: `read_file` misdetected `website/docs/.../architecture.md` as binary
  (UTF-8, very long lines); `file` + `grep -n` confirmed the cited text at line 234.

## Technique notes

- Re-ran the record's MEASURED command in the checkout venv — byte-identical output
  (8 provider names in order, holographic available=True, schemas fact_store+fact_feedback).
- Independently verified the "19 public methods, 4 abstract" ABC-surface claim with
  `inspect.getmembers(MemoryProvider)` + `MemoryProvider.__abstractmethods__`.
- Verified the INFERRED finding's named precedent (honcho = cloud client behind the same
  ABC, `plugins/memory/honcho/__init__.py:302-307`) before accepting the inference.
- "Still open" items referenced real things (`hermes_cli/memory_setup.py:219` exists).
