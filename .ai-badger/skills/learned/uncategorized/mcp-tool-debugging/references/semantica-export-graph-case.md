# Case study: semantica export_graph broken on every format (2026-08-20)

The full investigation that the SKILL.md method was extracted from. Task:
semantica-integration-fix (ai-badger 0.130.0/0.130.1), upstream PR semantica-agi/semantica#1151.

## Symptom

`export_graph` — the only window into Semantica's in-memory graph — failed on every format. The AiRaccoon persistence bridge (`.semantica/` dumps → watch → memory) was dead for weeks, silently: graph empty, no dumps, no watch.

## Exact failure strings (all verified live)

1. json branch:
   `{"error": "JSONExporter.export() missing 1 required positional argument: 'file_path'"}`
2. json-ld/turtle/nt/xml via MCP: client timeout after 300s (the configured MCP timeout), while the direct call returned in <1s.

## Root causes (three, nested)

1. **Missing arg**: `_tool_export_graph` called `JSONExporter().export(graph)`
   but `export(self, data, file_path, ...)` requires `file_path` → TypeError. Present in 0.6.5, the 0.6.6 wheel, AND upstream main at the time — the changelog did not mention it, so a version bump did not fix it.
2. **Type mismatch**: RDF branches passed the `ContextGraph` OBJECT to
   `RDFExporter().export_to_rdf(...)` which needs the canonical kg dict →
   `AttributeError: 'ContextGraph' object has no attribute 'get'`. Fix:
   `graph.to_kg_dict()` before exporting.
3. **Stdio corruption**: `RDFExporter` prints a rich progress bar (`🔄 Semantica is exporting: ...`) to STDOUT. Over stdio MCP stdout IS the JSON-RPC framing → client hangs at its timeout. The progress tracker honors
   `SEMANTICA_DISABLE_PROGRESS=1` (verified: env var set → direct call clean).

## Repro commands

```bash
# direct handler call in the package's own interpreter
~/.local/share/uv/tools/semantica/bin/python -c "
from semantica import mcp_server
print(mcp_server._tool_export_graph({'format': 'json'}))"

# check the wheel, not just the installed copy
curl -sL https://pypi.org/pypi/semantica/0.6.6/json | python3 -c \
  "import json,sys; d=json.load(sys.stdin); [print(u['url']) for u in d['urls'] if u['packagetype']=='bdist_wheel']"
# unzip the wheel, grep _tool_export_graph — identical bug on 0.6.6
```

macOS note: no `timeout` command — use `signal.alarm(20)` in the Python repro.

## Upstream fix (PR #1151)

- convert via `ContextGraph.to_kg_dict()` before handing to exporters,
- `json.dumps(kg, indent=2, ensure_ascii=False)` for the json branch,
- `os.environ["SEMANTICA_DISABLE_PROGRESS"] = "1"` at mcp_server import time.

## ai-badger side (0.130.0 + 0.130.1)

- **Error-dump bug**: `extract_graph_json` checked `error` only on the OUTER envelope; `{"result": "{\"error\": ...}"}` passed through and the error dict was SAVED as a `.semantica/<session>.json` graph dump (observed live). Fix: apply the
  same inner-error guard to the `result` AND `structuredContent`
  branches. RED→GREEN witnessed.
- **Autosave reached all agents (#418)**: claude PostToolUse + copilot postToolUse via a stdin-JSON shim (`semantica_export_autosave_hook.py`)
  delegating to `autosave_export`; source hooks.json command added; the stale
  "deferred — #418" exemptions deleted from `tooling/validate.py`.
- **check.py probe**: `export_graph_works(exe)` shells out to the interpreter beside the resolved executable (`exe.parent / "python"`) with a timeout — the ambient-import version silently passed on uv-tool installs (verified:
  returned True false-success before, False against real 0.6.5 after).
- **No fail-closed version floor**: `semantica>=0.6.7` does not exist; a floor would hide semantica everywhere. Probe warns, exits 0.
- **Shim project-dir resolution**: PostToolUse payloads carry no cwd; the hook process cwd is the agent launcher. Resolve `$CLAUDE_PROJECT_DIR` → payload
  `cwd` → no write (commit_reminder_hook pattern), or dumps scatter.
- **stderr diagnostic**: a skipped error result prints one line to stderr (never stdout), or failure is invisible.
- **Test-env pitfall found**: ai-badger's conftest sets `CLAUDE_PROJECT_DIR`
  to a scratch dir at import; subprocess hook tests must strip it from the CHILD env or the child resolves the wrong project dir.

## MoE/quality-gate flow used

- Plan reviewed by architect + test-engineer lanes (both APPROVE-WITH-FIXES); MUST-FIXes folded: no fail-closed floor; delete manifest exemptions.
- Code-reviewer gate (APPROVE-WITH-FIXES, 5 findings) arrived AFTER the merge — folded into follow-up PR (0.130.1): structuredContent guard, shim missing-sibling crash path, honest probe, nudge deferral documented, plan-doc drift. Lesson:
  budget a follow-up PR when review is async.
