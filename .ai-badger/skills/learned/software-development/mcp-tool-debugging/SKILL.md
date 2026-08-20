---
name: mcp-tool-debugging
description: >-
  Use when an MCP tool call fails or times out over stdio.
version: 1.0.0
author: hermes
license: MIT
platforms: [linux, macos]
scope: default
metadata:
  hermes:
    tags: [mcp, debugging, stdio, timeout, prerequisite-check]
    related_skills: [mcp-tool-surface-testing, mcp-client, dotnet-mcp-server]
---

# MCP tool debugging

A method for "the MCP tool call fails / errors / hangs" that separates the four places a failure can live: the client transport, the server's stdio framing, the handler's argument shape, and the installed package version. Proven on the
semantica export_graph case (2026-08-20): every format broken, client timeouts at 300s while direct calls returned in <1s.

## The four checks, in order

### 1. Reproduce the handler directly — bypass MCP entirely

Run the server's handler in the server's own interpreter, with the exact args the tool receives:

```python
# in the installed package's venv python
from semantica import mcp_server
print(mcp_server._tool_export_graph({"format": "json"}))
```

The comparison that decides the next step:

- **Direct call works, MCP call fails** → transport/framing problem (step 2).
- **Direct call fails too** → handler bug (step 3) or package bug (step 4).

### 2. Stdio framing: stdout IS the protocol

Over stdio MCP, stdout carries JSON-RPC. ANY other stdout output — rich progress bars, print () debug lines, logos — interleaves with the framing and hangs the client, usually until its exact timeout (observed: 300s) while the same call
returns in <1s directly.

- Grep the handler path for console renderers (`rich`, `tqdm`, `print`).
- Look for a kill switch env var — semantica has `SEMANTICA_DISABLE_PROGRESS=1`, verified to silence the tracker. Set it in the server process at import time.
- When the library owns the server entrypoint, patch upstream to force the var; when you own the launcher config, add it to the server's `env`.

### 3. Handler argument shape vs the API signature

Read the actual signature of the thing the handler calls:

- Missing required positional arg: `JSONExporter().export(graph)` failed with
  `missing 1 required positional argument: 'file_path'` — the handler never passed the arg the method needs.
- Type mismatch: passing an object where a dict is expected —
  `RDFExporter().export_to_rdf(graph, ...)` received a `ContextGraph` object,
  `AttributeError: 'ContextGraph' object has no attribute 'get'`. Look for an adapter (`graph.to_kg_dict()`) and convert before handing off.

### 4. Probe the RESOLVED environment, not the ambient one

uv-tool / pipx installs live in their own venv. The ambient python running a check script cannot import the package at all, so an in-process probe's exception path silently reports SUCCESS — the warning never fires in exactly the setup that
is broken.

- Resolve the executable (`shutil.which("semantica")` or the venv bin dir).
- Find the interpreter beside it: `exe.parent / "python"` (uv-tool, pipx, venv all share this layout; `python.exe` on Windows).
- Shell out: `subprocess.run([interpreter, "-c", snippet], timeout=10)`.
- If no interpreter resolves, warn nothing (misattribution is worse than silence). If the probe import raises, say nothing rather than blame the bug.

### 5. Version floors fail closed; functional probes warn

The bug may live in the latest release AND upstream main. Verify against the installed wheel and GitHub main before assuming a release fixes it — a version floor (`pkg>=0.6.7`) fails EVERYONE until the external PR lands and the release
exists. Prefer a deterministic functional probe that warns and exits 0 (the graph tools still work; only the broken tool is broken). Pin the default-distribution claim with a test, so a regression is deliberate.

### 6. Double-encoded MCP result envelopes

Some transports wrap the MCP result as `{"result": "<json-string>"}` (Hermes does). Error checks must apply to the INNER dict too:

- `{"result": "{\"error\": ...}"}` passes an outer-only `error` check and the inner error dict gets treated as DATA (observed: saved as a graph dump).
- Also guard the `structuredContent` envelope the same way.
- A skipped error must be diagnosed: one stderr line (never stdout), or the failure is invisible for weeks.

## Pitfalls

- **Test-suite env leakage**: a repo's conftest may set `CLAUDE_PROJECT_DIR` /
  `HOME` to scratch dirs at import; subprocess-based hook tests must strip or override them in the CHILD env, or the child resolves the wrong project dir.
- **Async review lanes may arrive post-merge**: dispatch review early or budget a follow-up PR — findings that land after merge still need folding.
- **macOS has no `timeout` command**: use `signal.alarm()` in Python or subprocess `timeout=` instead.

## Verification

- RED before fix: watch the failing assertion with the OLD code, then the fix, then re-run — a check that has never been seen to fail is not a check.
- Verify the probe against the REAL broken install (returns False), not only against mocks.

## References

- `references/semantica-export-graph-case.md` — the full 2026-08-20 case:
  exact failure strings, repro commands, upstream fix, and the ai-badger side (error-dump bug, resolved-env probe, all-agent autosave wiring).
