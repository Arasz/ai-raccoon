# hermes-provider-cli-compat — adjust the hermes provider setup to the current ai-raccoon CLI

Task: `hermes-provider-cli-compat` (2026-08-20). The one-shot setup script
`scripts/hermes-provider-setup.py` fails against the installed ai-raccoon
1.27.1 in two independent ways; both root causes are confirmed in source and
witnessed live (RED, 2026-08-20).

## Root causes (from review, source-confirmed)

### Bug 1 — probe fails: `client did not connect` (exit 2)

- `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:112-116`: the root `--transport`
  option defaults to `proxy` ("proxy (default) relays to one HTTP backend").
- The plugin (`integrations/hermes/ai-raccoon/client.py`, `create_client`)
  spawns the child as `ai-raccoon <binary_args> --quiet`; with the probe's
  `binary_args: ['--data-root', '<temp>']` the child is a PROXY.
- The user's real server (PID 5537, `~/.ai-raccoon` bank) owns port 7721. The
  proxy probes it; the token file beside the TEMP data root holds no token →
  `BackendUnavailableException` ("a serve on another data root may own port
  7721", `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:37`) → child exits →
  stdio EOF → `AiRaccoonError: ... failed: Connection closed` →
  `provider._client = None` → `PROBE FAIL`.
- The CLI's own hint names the fix: "to serve in-process instead, run:
  `ai-raccoon --transport stdio`". `integrations/hermes/ai-raccoon/README.md:73`
  already documents it: "Pass `--transport stdio` in `binary_args` for the old
  in-process behaviour." The script does not follow its own plugin's recipe.

### Bug 2 — exclude write fails: `extract exclude add` exit 15

- `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:407-442`: the `extract` verb now
  only has `prune`; the exclude verb family lives under
  `settings extract exclude add|remove|list`.
- `scripts/hermes-provider-setup.py:181` still calls
  `ai-raccoon extract exclude add hermes/` → "Required command was not
  provided" (exit 15). Confirmed live: `ai-raccoon settings extract exclude
  list` works and already shows `hermes/` on the real bank (add is deduped).

## Scope

1. `scripts/hermes-provider-setup.py`
   - probe config (`run_probe`): `binary_args: ['--data-root', '<bank>',
     '--transport', 'stdio']` — hermetic in-process probe; never touches a
     port, so it passes whether or not a real server owns 7721.
   - `wire_exclude_prefix`: invoke `settings extract exclude add hermes/`;
     update the module docstring, the function docstring, and the
     manual-run fallback messages.
2. `integrations/hermes/tests/test_setup_script.py` (TDD)
   - Harness fix (pre-existing breakage on this machine, masked the real
     symptom): the fake-hermes shim's `#!/usr/bin/env python3` resolves to
     homebrew python3 which lacks `yaml` → `ModuleNotFoundError` in
     `test_install_copies_plugin_and_activates` and
     `test_rerun_is_idempotent`. Pin the shim shebang to `sys.executable`
     (the pytest interpreter).
   - NEW test `test_exclude_prefix_uses_settings_verb`: a fake `ai-raccoon`
     shim on PATH records argv and exits 0; assert the recorded command is
     `settings extract exclude add hermes/` and the success line prints.
     (RED on old code: records `extract exclude add hermes/`.)
   - UPDATE `test_probe_spawns_isolated_server_and_passes`: keep the
     `[probe] PASS` assertion; add a deterministic foreign-server
     precondition — if 7721 is free, start a throwaway
     `ai-raccoon serve --port 7721 --data-root <tmp>` (if the real server
     already owns 7721 the spawn fails and that IS the precondition), so
     the old code fails deterministically on every machine, not just this
     one. RED already witnessed here (port-7721 message in the probe
     stderr, 2026-08-20).
3. No plugin source change: `client.py` / `__init__.py` verified working
   against 1.27.1 — this session's provider connects and `memory_search`
   works through the proxy. README.md already documents the isolation
   recipe; no stale verb references remain anywhere in `integrations/`.

## Out of scope

- Plugin code, CLI/C# code, version bump (no C# change → no VERSION bump).
- CI wiring for `integrations/hermes/tests` (documented gap, 2026-08-07
  review; not this task).

## Acceptance gates

- G1 — suite green: `/usr/bin/python3 -m pytest
  integrations/hermes/tests/test_setup_script.py` → all pass (7 → 8 tests).
- G2 — witnessed RED → GREEN: probe test failed pre-fix with the port-7721
  message (witnessed); exclude test is RED on the old verb by construction.
- G3 — live script: `python3 scripts/hermes-provider-setup.py` prints
  `[probe] PASS` and no `[exclude] WARNING` (idempotent; `hermes/` already
  present on the real bank — deduped).
- G4 — `py_compile` both changed Python files.

## Flow (before → after)

Probe, before:
```
hermes probe → spawn ai-raccoon --data-root <tmp> --quiet
  → child = PROXY (default transport)
  → probes http://127.0.0.1:7721/mcp → REAL server (other data root) answers
  → token file beside <tmp> holds no token → BackendUnavailableException
  → child exits → stdio EOF → "Connection closed" → _client = None → FAIL
```
Probe, after:
```
hermes probe → spawn ai-raccoon --data-root <tmp> --quiet --transport stdio
  → child = IN-PROCESS stdio MCP server (no port involved)
  → initialize handshake → _client set → PASS
```
Exclude, before: `ai-raccoon extract exclude add hermes/` → exit 15.
Exclude, after:  `ai-raccoon settings extract exclude add hermes/` → exit 0.
