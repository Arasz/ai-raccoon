# Setup-script CLI drift — worked example (2026-08-20)

Session: `hermes-provider-cli-compat` (ai-raccoon repo). The one-shot setup script
`scripts/hermes-provider-setup.py` failed against the installed ai-raccoon 1.27.1 in two independent ways. Both root causes were confirmed in source, then witnessed live.

## Failure transcript (user's run)

```
[probe] FAIL: client did not connect
[probe] WARNING: probe failed (exit 2):  on another data root may own port 7721; to serve
  in-process instead, run: ai-raccoon --transport stdio
ai-raccoon provider connect failed: ai-raccoon connect to ai-raccoon --data-root
  /var/folders/.../hermes-setup-probe-XXXX/bank --quiet failed: Connection closed
[exclude] WARNING: extract exclude add failed (15): Required command was not provided.
  Unrecognized command or argument 'exclude'. ... 'add'. ... 'hermes/'.
```

## Bug 1 — probe fails: spawned child is a PROXY, not a server

Root cause chain (source-confirmed):

1. `CliCommandTree.cs:112-116` — root `--transport` option defaults to `proxy`
   ("proxy (default) relays to one HTTP backend").
2. The plugin's `create_client` spawns `ai-raccoon <binary_args> --quiet`; the probe's config only passed `--data-root <temp>` → child is a proxy.
3. The proxy dials `http://127.0.0.1:7721/mcp` and validates the token file beside ITS OWN data root (`BackendSessions.cs:37`: "the backend at ... is listening but ... holds no token — a serve on another data root may own port 7721").
4. Real server owns 7721 on a different data root → `BackendUnavailableException` → child exits → stdio EOF → `AiRaccoonError: ... failed: Connection closed` → `_client = None`
   → `PROBE FAIL`.

Fix (the CLI's own hint + the plugin README's documented isolation recipe):
`binary_args: ['--data-root', '<temp>', '--transport', 'stdio']` — the child then serves in-process over stdio and never touches a port. Works whether or not a real server is running. NOTE: the production provider (no binary_args, default
data root) is UNaffected — its proxy finds the backend + matching token and connects; the collision only happens when the spawned data root differs from the running backend's.

## Bug 2 — `extract exclude add` exit 15: verb family moved

- `CliCommandTree.cs:407-442` — top-level `extract` now has ONLY `prune` (an operation). The exclude family lives under `settings extract exclude add|remove|list`.
- Config verbs live under `settings <area>`; top-level verbs are operations (`prune`, `repair`). The script called the old path → System.CommandLine exit 15
  "Required command was not provided".
- The failure is best-effort by design (the script warns and continues); the desired end state (`hermes/` prefix) may ALREADY exist on the real bank from an older successful run — `ai-raccoon settings extract exclude list` shows it, and
  `add` is deduped.

Fix: `ai-raccoon settings extract exclude add hermes/`; update module docstring, function docstring and the manual-run fallback messages that name the old verb. Then grep the repo for the old verb family name — memory/docs entries survive
after CLI removal.

## Test-harness lesson (masked the real failure)

The fake-`hermes` shim used `#!/usr/bin/env python3`; ambient PATH python (homebrew
`/usr/local/bin/python3`) lacks `yaml`, so every shim-based test crashed with
`ModuleNotFoundError` and the probe test's REAL failure was hidden behind the harness failure. Fix: write shims with `#!{sys.executable}` (the pytest interpreter) — do NOT use an f-string for the whole shim body (its `{}` dict literals
break it); use
`FAKE_HERMES.replace("#!/usr/bin/env python3", f"#!{sys.executable}")` at write time.

## Verification recipe

- Baseline RED: `/usr/bin/python3 -m pytest integrations/hermes/tests/test_setup_script.py`
  — the probe test fails with the exact port-7721 message when a real server owns 7721. (main-checkout `.venv` has NO pytest; `/usr/bin/python3` has pytest + yaml.)
- Deterministic RED on machines without a live 7721 server: pre-bind a throwaway
  `ai-raccoon --data-root <tmp> serve --port 7721 --quiet` (root options precede the verb)
  before the probe; old code fails on the token mismatch, fixed code passes (stdio probe never touches the port).
- The test file is CI-blind: `pyproject.toml` `testpaths = ["scripts/tests"]` does NOT collect `integrations/hermes/tests` (documented gap since 2026-08-07).
