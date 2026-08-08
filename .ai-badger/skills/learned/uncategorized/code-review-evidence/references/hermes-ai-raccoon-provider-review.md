# Worked case: Hermes ai-raccoon memory-provider plugin review (PR #61)

Review scope: Python MemoryProvider plugin (`integrations/hermes/ai-raccoon/`, named
`hermes-provider/ai-raccoon/` at review time),
its tests, and the design doc, against the Hermes MemoryProvider ABC and the
ai-raccoon MCP server. Verdict: APPROVE-WITH-CHANGES (1 MUST-FIX, 3
SHOULD-FIX, rest NIT). This file keeps the reusable evidence patterns; the
PR-specific findings are not the point.

## 1. The isolation claim that was false — and how it was proven

The integration suite claimed temp-bank isolation:

```python
monkeypatch.setenv("AIRACCOON_DATA_ROOT", str(tmp_path / "bank"))
```

Proof of falsity, in two steps:

1. **Mechanism check** — does production read the var?
   `grep -rn "AIRACCOON_DATA_ROOT" src/` → zero hits (only
   `tests/AiRaccoon.Tests/E2E/E2ETestCollection.cs` comment + docs). The CLI
   resolved its data root ONLY from `--data-root`, defaulting to
   `~/.ai-raccoon` (`src/AiRaccoon/Setup/Cli/CliArgs.cs:89`,
   `Setup/DefaultOptions.cs:10`). An env var set in the parent flows to the
   child but the child ignores it.
2. **Forensic confirmation** — the real store carries the probes:
   `python3 -c` opening `~/.ai-raccoon/memory.db` (plain SQLite — check
   `PRAGMA table_info(entries)` first for the value column name, here `value`
   not `content`), then:
   `SELECT project_id, substr(value,1,60), source_file, section FROM entries
   WHERE project_id='hermes-itest'` → all 4 probe strings verbatim
   (`integration probe fact 12345`, `prefetch probe marker 777`, `stats probe
   entry 999`, `assistant says sync probe 555`). A test-only project id in the
   REAL bank is the smoking gun; no amount of "the fixture sets the env var"
   argument beats it.

Fix direction: pass the CLI arg through the spawn args
(`StdioServerParameters(command=binary, args=["--data-root", tmp])` — the mcp
SDK supports `args`), not env. Also: README + fixture docstring claims about
the env var were factually false and needed correcting with the code; and the
4 probe rows in the real bank needed deletion.

## 2. Installed-SDK verification (mcp 1.28.1, hermes venv)

- Tuple shapes: `grep -n "yield" .../site-packages/mcp/client/stdio/__init__.py`
  → `yield read_stream, write_stream` (2-tuple); `streamable_http.py` →
  `yield (read_stream, write_stream, transport.get_session_id)` (3-tuple).
  The plugin unpacked both correctly.
- Child termination: read the stdio `__aexit__` finally block — stdin close →
  `process.wait()` under `anyio.fail_after(PROCESS_TERMINATION_TIMEOUT)` →
  SIGTERM→SIGKILL process-tree escalation. `close()` therefore really kills
  the child; no need to probe the process table.
- Plugin manifest: `plugin.yaml` `hooks:` key is NOT in the manifest schema
  (`hermes_cli/plugins.py` parses `provides_hooks`); `hooks:` is decorative
  (mirrors bundled holographic plugin). NIT, not a bug — memory-provider
  hook dispatch goes through MemoryManager, not plugin.yaml.
- Config helpers `cfg_get`/`load_config_readonly`/`read_user_config_raw` and
  the `register(ctx)` + `register_memory_provider` discovery path all exist
  and match; the general PluginManager auto-coerces user memory plugins to
  kind="exclusive" and skips them — memory discovery handles loading.

## 3. Non-bug I nearly flagged — empirical check first

Suspicion: `from .client import create_client` (relative import) inside a
module loaded via `importlib.util.spec_from_file_location("name", dir/"__init__.py")`
should raise "attempted relative import with no known parent package".
Repro: 5 lines — spec-load, print `__package__` / `__path__`, exec the import.
Result: `spec_from_file_location` on a file named `__init__.py` sets
`__package__` = the name and `__path__` = [dir], so relative imports resolve.
Dropped the finding. Verify before flagging.

## 4. Skip-on-spawn-failure

`if provider._client is None: pytest.skip("server failed to spawn")` — with
`--run-slow` explicitly requested, a dead stdio path would report "5 passed".
Skip on missing precondition (binary not on PATH) is fine; skip on system
failure masks regressions. Should `pytest.fail`.

## 5. Result-shape pinning (what "pins the REAL shape" means here)

Fake/assertion shapes were verified against the C# records:
`WriteResult(Hash, Path, Context, CreatedAt)` → `{hash, path, context,
createdAt}`; `SearchResultList(IReadOnlyList<MemorySearchResult>)` →
`{"results": [...]}` with `{hash, seq, ranking, path, snippet, ...}`;
`StatsResult(Entries, Pending, Contexts)`; `ShareResult(Shared, Context)`
(`src/AiRaccoon/Tools/MemoryTools.cs:669-681`,
`src/AiRaccoon.Core/Memory/MemorySearchResult.cs`). Tool-count claims
("20-tool surface") verified via `grep -c "McpServerTool(Name"` across
src/ (17 MemoryTools + 3 WatchTools = 20).

## 6. Design-fidelity check

Six approved decisions (D1-D6) all implemented as recommended (stdio default
+ http option; curated 4 tools; derived project id; sync writes assistant
message with `sourceFile=hermes/<session>`, `section=turn`; no session-end
summary; bridge coexistence documented). The catch: the design doc's decision
table was EMPTY and status said "decisions open" — the record never captured
the approvals. Separate doc finding.
