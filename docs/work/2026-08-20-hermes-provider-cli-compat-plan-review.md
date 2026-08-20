# Plan review — `hermes-provider-cli-compat` (2026-08-20)

Plan reviewed: `docs/work/2026-08-20-hermes-provider-cli-compat-plan.md`
Worktree: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/hermes-provider-cli-compat`
Branch `task/hermes-provider-cli-compat`, base `dc3c742c`, plan commit `a8723238`.
Reviewer: read-only plan review. No tracked files modified. The one uncommitted change
(test shim shebang pin in `integrations/hermes/tests/test_setup_script.py`, 3+/1-) matches the
plan's "harness fix" item and was treated as expected context.

## Verdict: APPROVE-WITH-CHANGES

Both root-cause analyses are **accurate and source-verified**, the two script fixes are
**correct and live-verified in command shape**, and the test plan pins both behavior changes.
However, the proposed probe-test foreign-server precondition is **broken as literally written**:
the command `ai-raccoon serve --port 7721 --data-root <tmp>` exits 15 at parse (live-verified)
and would create no listener, so on a machine with a free 7721 the old code would pass
(proxy starts its own backend + mints its own token) and the test would be a **false green** —
it would not pin the fix. Items 1–3 below are MUST-FIX before implementation; 4–6 are SHOULD-FIX.

---

## 1. Verified claims (all checked against source and the installed CLI)

Installed CLI: `ai-raccoon` 1.27.1+bcd0797067cea8a65e9c38619e653d29cd3a660b
(`/Users/arasz/.dotnet/tools/ai-raccoon`). Real server PID 5537 confirmed listening on
127.0.0.1:7721.

| Plan claim | Verification |
|---|---|
| Root `--transport` default is proxy | `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:112-116` (description "proxy (default) relays to one HTTP backend"); `Setup/DefaultOptions.cs:10` `Transport = McpTransport.Proxy` (ADR-0020: bare launches proxy; `--transport stdio` is the escape hatch). Live `--help` shows the same. |
| `extract` verb has only `prune`; exclude lives under `settings extract exclude add\|remove\|list` | `CliCommandTree.cs:407-414` (ExtractCommand, prune only) and `:416-442` (SettingsExtractCommand with exclude add/remove/list); `Commands/ConfigCommands.cs:91-99` dispatch table (settings extract exclude add/remove/list at 96-98, `extract prune` at 99, **no** `extract exclude` entry). Live: `ai-raccoon extract --help` (only prune) and `ai-raccoon settings extract exclude --help` (add/remove/list). |
| Old verb fails with "Required command was not provided", exit 15 | Live: `ai-raccoon extract exclude add hermes/` → `Required command was not provided.` + unrecognized-argument lines, **exit 15**. |
| `settings extract exclude list` already shows `hermes/` (add is deduped) | Live: `ai-raccoon settings extract exclude list` → `hermes/`, exit 0. |
| `client.py` spawns `ai-raccoon <binary_args> --quiet` | `integrations/hermes/ai-raccoon/client.py:283-292` — `create_client`: `args = list(binary_args)`; `if quiet (default True): args.append("--quiet")`; `StdioClient(binary, args)`. |
| Probe config YAML: `binary_args: ['--data-root', '<bank>']` (no transport) | `scripts/hermes-provider-setup.py:134-137` (`run_probe` writes exactly this). |
| BackendUnavailableException message at `BackendSessions.cs:37` | `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:36-37` — exact text "the backend at … is listening but … holds no token — a serve on another data root may own port {config.Port}". |
| README already documents the `--transport stdio` recipe | `integrations/hermes/ai-raccoon/README.md:59,71-78`. |
| RED witnessed (both bugs) | Re-witnessed live today: current suite (shim fix applied) → `test_probe_spawns_isolated_server_and_passes` FAILS with `[probe] FAIL: client did not connect`, stderr tail "…on another data root may own port 7721; to serve in-process instead, run: ai-raccoon --transport stdio", child line `ai-raccoon connect to ai-raccoon --data-root <tmp>/bank --quiet failed: Connection closed`, and `[exclude] WARNING: extract exclude add failed (15): Required command was not provided.` — 1 failed, 6 passed in 2.31s. |
| Harness fix mechanism (env python3 lacks yaml) | `env python3` on this machine resolves to `/usr/local/bin/python3`, which raises on `import yaml` (traceback observed); `/usr/bin/python3` has yaml + pytest 8.4.2. Shebang pin to `sys.executable` is the right fix; the uncommitted diff implements it. |
| No stale verb refs in `integrations/` | Search for `extract exclude` / `exclude add` across the worktree: matches only in `scripts/hermes-provider-setup.py` (lines 9, 173, 178, 181, 184), the plan file, and `docs/reference/agent-memory-server.md` (already uses the settings verb). `tests/AiRaccoon.Tests/Unit/Setup/Cli/SettingsCommandTreeTests.cs:78-80` pins the settings verb. Main repo `integrations/` also clean. |
| Fix command shape (`--data-root <bank> --transport stdio --quiet`) | Live: `ai-raccoon --data-root $TMP --transport stdio --quiet` with stdin held open 4 s stays alive, exits 0 on EOF, creates `memory.db` + `quiet.log` beside the bank. No parse error. |
| 7 → 8 tests | Current file has 7 tests; plan adds 1 (`test_exclude_prefix_uses_settings_verb`) → 8. Correct. |
| Test file has no slow marker / `--run-slow` only in conftest | Confirmed: no `slow` marker in `test_setup_script.py`; `conftest.py:108-123` defines `--run-slow` and skips marked items. All 7 current tests run in the default suite. |

## 2. Cross-check: planned tests vs planned script changes

- **Bug 1 fix (`--transport stdio` in probe `binary_args`)** — pinned by the updated
  `test_probe_spawns_isolated_server_and_passes` (`[probe] PASS` assertion under a foreign-server
  precondition). With the precondition actually in place, old code deterministically fails at the
  token check (verified mechanism live); new code passes without touching the port. **Covered —
  provided MUST-FIX 1–3 are implemented.**
- **Bug 2 fix (`settings extract exclude add`)** — pinned by the new exclude test's recorded-argv
  assertion (`["settings", "extract", "exclude", "add", "hermes/"]`); RED on old code by
  construction. Covered. Note: the success-line assertion alone would NOT discriminate old vs new
  (both print the success line when the fake exits 0) — the argv assertion is the discriminator;
  the plan implies this but should state it.
- **Docstring / fallback-message updates (script lines 9, 173, 178, 184)** — not pinned by any
  test. Acceptable for a one-shot script (cosmetic strings), but the plan should note they are
  uncovered (SHOULD-FIX 5).
- **Harness fix (shim shebang)** — exercised by `test_install_copies_plugin_and_activates` and
  `test_rerun_is_idempotent` (they invoke the fake hermes). Covered.

## 3. MUST-FIX items

**M1 — The precondition command is invalid as written (false-green risk).**
The plan's `ai-raccoon serve --port 7721 --data-root <tmp>` puts `--data-root` after the verb;
`--data-root` is a root launch option ("must precede the verb"). Live-verified:
`ai-raccoon serve --port 7721 --data-root <tmp>` → **exit 15, "Unrecognized command or argument
'--data-root'"**, nothing binds. Implemented literally, on any machine with a free 7721 the test
creates no listener, the old proxy starts its own backend on 7721 with the probe's temp bank and
mints its own token, the probe **passes**, and the test goes green on old code — the exact
regression the precondition exists to catch.
Correct command: `ai-raccoon --data-root <tmp> serve --port 7721` (live-verified: parses, and
with a server already on the port attaches and exits 0 in ~0.24 s). Fix the plan text and the test.

**M2 — Wrong mechanism claim: "if the real server already owns 7721 the spawn fails".**
Live-verified: with an ai-raccoon server on the port, `serve` **attaches and exits 0** with
"ai-raccoon: attached to the server already listening on http://127.0.0.1:7721/mcp — it may not
be serving <bank>" (`NodeRunner.cs:79-81` → `ReportAttachedAsync`, `:233-244`). It does not fail
and does not hang. Only a foreign (non-ai-raccoon) listener makes serve exit non-zero (live:
python http.server on the port → **exit 3**, "port … is in use", 0.54 s, no hang). The test must
be specified around "**port 7721 is owned by some server**", not around "spawn fails":
- throwaway process still alive after printing the bound-URL line → the throwaway owns the port;
- throwaway exited 0 → an ai-raccoon server (e.g. the real one) owns the port — precondition holds;
- throwaway exited non-zero → a foreign listener owns it — precondition holds;
- no listener at all (parse error, bind failure, etc.) → **do not proceed** (see M3).

**M3 — Spawn/readiness/teardown mechanics are unspecified.**
The throwaway server must be (a) background-spawned (`Popen`, not `subprocess.run` — a bound
server runs until killed; verified: free-port `serve` stays alive and prints
"Now listening on: http://127.0.0.1:7797" (stderr) and the URL line `http://127.0.0.1:7797/mcp`
(stdout)); (b) **readiness-confirmed before running the script** — wait for the URL line on
stdout AND process liveness, or poll a TCP connect to 127.0.0.1:7721 (timeout ~10 s). The
readiness wait is essential for determinism: if the script runs while 7721 is still free, the old
proxy starts its own backend and the test goes green on old code. If no listener can be
established, skip/fail the test with a clear message rather than proceeding; and (c) **terminated
in teardown** (`terminate()` + `wait()`, and remove the temp data root) when the test bound it.
The URL line alone cannot discriminate bind from attach (both print it); liveness is the
discriminator.

## 4. SHOULD-FIX items

**S1 — Real-bank side effect in full-script tests after the fix.**
`test_install_copies_plugin_and_activates`, `test_rerun_is_idempotent`, and the probe test run
the whole script, which executes `wire_exclude_prefix` unconditionally with the real `ai-raccoon`
on PATH. Today the old verb parse-fails (exit 15, no write). After the fix these tests will run
`settings extract exclude add hermes/` against the **real default bank**. On this machine it is a
deduped no-op (`hermes/` already listed — verified), but the module docstring's claim "the real
~/.ai-raccoon bank is never touched" (test file line 7) becomes false, and on a fresh machine the
suite would add the exclusion to the real bank. Recommended: give the install/rerun/exclude tests
a fake `ai-raccoon` (exits 0) so only the probe test — which needs the real binary for the probe
— runs the real verb; at minimum, update the docstring to state the remaining idempotent real-bank
add. The fake must be test-local (per-test `monkeypatch` PATH, same mechanism as `fake_hermes`);
state explicitly that the probe test keeps the real binary.

**S2 — State which assertions discriminate old vs new.**
In the exclude test, the recorded-argv assertion is the RED discriminator; the success-line
assertion is not (both old and new code print it when the fake exits 0). Say so in the test
docstring, and consider a failure-path case (fake exits non-zero → WARNING line contains the new
verb) to pin the fallback-message text.

**S3 — Environment assumptions.**
The probe test hardcodes `/Users/arasz/.hermes/hermes-agent/venv/bin/python` (exists; imports
`plugins.memory` — verified) and requires the real `ai-raccoon` on PATH; G1's `/usr/bin/python3
-m pytest` works on this machine (pytest 8.4.2, yaml present — verified). These are machine-local
assumptions; fine while the suite has no CI wiring (documented out of scope), but worth a note in
the plan so the next environment change is diagnosed fast.

## 5. Acceptance-gate assessment

- **G1** (suite green, 8 tests): feasible on this machine once M1–M3 are fixed. Current baseline
  re-verified: 1 failed (the target RED), 6 passed.
- **G2** (witnessed RED → GREEN): probe-test RED re-witnessed today with exactly the port-7721
  message; exclude-test RED is by construction (argv assertion). GREEN after the fix will
  additionally exercise the stdio child end-to-end (command shape already validated).
- **G3** (live script): consistent with verified CLI surface; `hermes/` already present →
  deduped no-op, no `[exclude] WARNING`.
- **G4** (`py_compile`): fine.
- Gap noted: no gate checks that the throwaway server was actually established (M3's skip/fail
  path) — fold that into the probe test itself.

## 6. Non-issues confirmed

- Fix ordering in `binary_args` (`['--data-root', '<bank>', '--transport', 'stdio']` + appended
  `--quiet`) matches `client.py`'s arg assembly (flags appended after binary_args, pinned by
  `tests/test_client.py:38`).
- `serve` idle watchdog (default 4 h; `--idle-timeout` optional) cannot expire during the test;
  a throwaway with `--idle-timeout 30s` was verified to exit cleanly on idle.
- No plugin/C# change needed; no version bump needed (scope correct).
- `BackendSessions`/`ServerProbe` behavior confirms the failure path is the token check, exactly
  as the plan's "Flow" section describes.

## 7. Evidence appendix (live, 2026-08-20)

- `ai-raccoon --version` → `1.27.1+bcd0797067cea8a65e9c38619e653d29cd3a660b`
- `lsof -nP -iTCP:7721` → `AiRaccoon 5537 … 127.0.0.1:7721 (LISTEN)`
- `ai-raccoon extract exclude add hermes/` → exit 15, "Required command was not provided."
- `ai-raccoon settings extract exclude list` → `hermes/`, exit 0
- `ai-raccoon --data-root $TMP serve --port 7721` → exit 0, 0.24 s, "attached to the server
  already listening on http://127.0.0.1:7721/mcp"
- `ai-raccoon serve --port 7721 --data-root $TMP` → exit 15, "Unrecognized command or argument
  '--data-root'"
- `ai-raccoon --data-root $TMP serve --port 7798` (python http.server on 7798) → exit 3,
  "port 7798 is in use — pass --port 0", 0.54 s, no hang
- `ai-raccoon --data-root $TMP serve --port 7797 --idle-timeout 30s` → binds, prints
  "Now listening on: http://127.0.0.1:7797" + `http://127.0.0.1:7797/mcp`, stays alive; exits
  cleanly on idle watchdog
- `ai-raccoon --data-root $TMP --transport stdio --quiet` (stdin open 4 s) → stays alive, exit 0
  at EOF, `memory.db` + `quiet.log` created
- `/usr/bin/python3 -m pytest integrations/hermes/tests/test_setup_script.py -q` → 1 failed
  (probe test, port-7721 + exit-15 symptoms), 6 passed, 2.31 s
