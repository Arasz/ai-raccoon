# Quality gate — `hermes-provider-cli-compat` (2026-08-20)

Worktree: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/hermes-provider-cli-compat`
Branch: `task/hermes-provider-cli-compat`, base `dc3c742c` (merge-base with `main` confirmed).
Reviewed diff: `git diff main..HEAD` — 4 commits (2 docs, 1 test RED `2c2c1357`, 1 fix `bee2b621`).
Reviewer: read-only quality-gate review. Only write: this file.

## Verdict: APPROVE

Both fixes are correct and complete, the tests are honest discriminators (RED witnessed on all three
failure shapes), the M1–M3 foreign-server precondition branch is correct by reading, S1 shim isolation
is sound, and scope is disciplined. Two Low nits and two informational notes follow; none block merge.

**Explicit caveat as required:** the throwaway-spawn branch of `test_probe_spawns_isolated_server_and_passes`
has **not been executed on this machine** — the real ai-raccoon server (PID 5537, live-verified via
`lsof -nP -iTCP:7721`) owns 127.0.0.1:7721, so the test takes the "port already owned" path. The
spawn/readiness/classification/teardown code was judged **by reading** (findings F4–F8). Recommendation:
the first run of this suite on a machine with 7721 free will exercise that branch; nothing in the
reading suggests a hang, leak, or race that would bite there, but that run should be treated as the
branch's first witnessed execution.

## Scope discipline (F1) — PASS

- Branch-only diff (`git diff dc3c742c HEAD --stat`): exactly 4 files —
  `docs/work/2026-08-20-hermes-provider-cli-compat-plan.md`,
  `docs/work/2026-08-20-hermes-provider-cli-compat-plan-review.md`,
  `integrations/hermes/tests/test_setup_script.py`, `scripts/hermes-provider-setup.py`. No C# change,
  no version bump — consistent with the plan's out-of-scope list. `git status` clean.
- `git diff main..HEAD` additionally shows `Directory.Packages.props` (DotNext 6.6.1→6.6.2,
  AWSSDK.S3 4.0.102.1→4.0.102.2, plus indentation). **Attributed to main-side drift, not branch
  work**: `git show dc3c742c:Directory.Packages.props` == `git show HEAD:...` (identical), and `main`
  advanced past the base with `b716c1a8 deps: update packages`. The branch never touched it; the merge
  will keep main's versions. No action needed — informational only.

## Test honesty / discriminators (F2, F3) — PASS

**F2 — `test_exclude_prefix_uses_settings_verb` (test_setup_script.py:159-171).** The recorded-argv
assertion `"settings extract exclude add hermes/" in calls` is a true RED discriminator: old code
records `extract exclude add hermes/`, which does not contain the `settings` substring → RED; new
code records the settings form → GREEN. The test docstring explicitly states why the success-line
assertion alone would not discriminate (both old and new print it when the shim exits 0) — exactly
the honesty the plan review (S2) demanded. RED was witnessed on this exact assertion (3 failed / 6
passed on commit `2c2c1357`). The success-line assertion is additionally present as a secondary
pin. No false-green path: the fake shim logs argv verbatim (`" ".join(sys.argv[1:])`), so the
assertion binds the actual invocation, not a message.

**F3 — `test_exclude_failure_path_mentions_settings_verb` (174-179).** `FAKE_AIRACCOON_FAIL=1` makes
the shim exit 3 with `boom` on stderr; the assertion `"settings extract exclude add" in result.stdout`
pins the WARNING format. Old code prints `[exclude] WARNING: extract exclude add failed (3): boom`
→ no `settings` substring → RED (witnessed). New code prints
`[exclude] WARNING: settings extract exclude add failed (3): boom` → GREEN. No other stdout source
of the verb exists in a failing run, so the assertion cannot pass spuriously. The shim exit code 3
≠ 0 correctly exercises the failure branch while the script still exits 0 (best-effort warning) —
asserted correctly.

**F3b — Probe-test honesty (188-235).** The `[probe] PASS` assertion under the foreign-server
precondition is honest on **every** machine:

- *Any machine, old script*: the probe child (proxy transport) targets a port owned by SOME server —
  throwaway (different data root, no token), the real server (token mismatch), or a foreign
  listener. All three fail the token/MCP handshake (`BackendUnavailableException`,
  `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:37` → `Connection closed` → `_client is None`) →
  `[probe] FAIL`/WARNING, never PASS → RED. Witnessed on this machine (real-server branch).
- *Any machine, new script*: child is `--data-root <tmp> --transport stdio --quiet` → in-process
  stdio server, no port involved → initialize handshake succeeds → PASS, regardless of the 7721
  state. GREEN.
- No false-green path found: PASS requires `provider._client` non-None, which for the old proxy is
  impossible while a server (with a different token file) owns the port; `PROBE SKIP` (binary
  missing) prints SKIP, not PASS.
- The test does not assert on stderr text; it asserts the behavior-level outcome, which is the
  stronger discriminator (implementation-agnostic to error-message wording).

## M1–M3 foreign-server precondition — judged by reading (F4–F8) — PASS

**F4 — M1 command shape (204-207).** `Popen([binary, "--data-root", str(foreign_bank), "serve",
"--port", "7721"])` — root option `--data-root` **precedes** the verb, exactly the live-verified
correct shape from the plan review (`ai-raccoon serve --port 7721 --data-root <tmp>` exits 15 and
binds nothing; the corrected shape parses). The inline comment records why. Correct.

**F5 — Readiness polling (208-210).** Bounded TCP-connect poll of 127.0.0.1:7721 (`_port_listening`,
0.5 s socket timeout) in 0.25 s steps up to a 10 s monotonic deadline. Deterministic: the script
runs only after a listener is confirmed, so the old proxy can never find a free port and self-bind
(which would be the false green the precondition exists to prevent). Bounded loop — no hang.

**F6 — M2 liveness classification (211-221).** Implemented as: *listener presence* is the
precondition; the liveness taxonomy decorates the fail path. This is semantically equivalent to the
plan-review's M2 matrix, because all three "precondition holds" outcomes imply a listener exists
and therefore proceed through the `_port_listening()` check without needing the classification:
throwaway-alive → it bound the port → listener up; exit-0 → it ATTACHED to an ai-raccoon server →
that server is listening; exit-nonzero → foreign listener → listening. The `poll()`-based
classification branch is only reached when no listener exists after 10 s, and there failing is
correct in every sub-case (still-running-but-unbound = anomalous startup; exit-0 = the owner
vanished after attach; exit-nonzero = bind failed or a foreign listener died) — the folded G-gap
("never proceed with 7721 free") is honored, and the fail message includes the throwaway's state
for diagnosis. No false proceed.

**F7 — M3 teardown (225-235).** `finally`: if the test spawned a throwaway that is still alive →
`terminate()` → `wait(timeout=10)` → on `TimeoutExpired` escalate to `kill()` + `wait()`. Bounded,
no hang, no zombie. The temp `foreign_bank` is removed (`shutil.rmtree(..., ignore_errors=True)`)
only after the child is dead, so the running server cannot recreate files under it. The
`pytest.fail` path still reaches `finally` (exception propagates through it). The spawn's
stdout/stderr pipes are never drained, but ai-raccoon `serve` emits a handful of lines (well under
the 64 KB pipe buffer), so no pipe-backpressure deadlock. No leak identified.

**F8 — Fail-if-no-listener path (216-221).** `pytest.fail` with an explicit message explaining both
the state and why proceeding is unsafe ("old proxy code would start its own backend and pass").
The `assert binary` guard for a missing `ai-raccoon` on a free-port machine is a plain assert but
carries a clear message; pytest does not run with `-O`, so it cannot be stripped. Satisfies the
folded G-gap.

**Residual race (informational):** between the last successful poll and the script's probe, the
7721 owner could vanish (e.g., throwaway attached to a server that then dies). That would let old
code self-bind and pass. The window is milliseconds, the plan accepted best-effort here, and on
this machine the owner is a long-lived daemon. Not a blocker.

## Script fix completeness (F9) — PASS

`bee2b621` updates **all seven sites**; grep of the worktree (excluding `docs/work/` records)
confirms zero stale `extract exclude add`:

1. Module docstring (`:9`) — `ai-raccoon settings extract exclude add hermes/` ✓
2. `run_probe` docstring (`:125-127`) — describes the stdio in-process probe ✓
3. Probe YAML `binary_args` (`:137`) — `['--data-root', '<bank>', '--transport', 'stdio']` ✓
4. `wire_exclude_prefix` docstring (`:174`) — settings verb in the operator hint ✓
5. CLI-missing fallback (`:179`) — `ai-raccoon settings extract exclude add {EXCLUDE_PREFIX}` ✓
6. Invocation (`:182`) — `["ai-raccoon", "settings", "extract", "exclude", "add", EXCLUDE_PREFIX]` ✓
7. WARNING format (`:185`) — `settings extract exclude add failed ({rc})` ✓

The only remaining `extract exclude add` (non-settings) matches are historical records —
`docs/work/*-plan*.md` and `.ai-badger/status-history.json` (a past-session log) — plus
`test_setup_script.py:163`, which cites the old verb only as documentation of what old code records
(the discriminator explanation). `docs/reference/agent-memory-server.md` already uses the settings
verb. The argument order in `binary_args` (`--data-root` then `--transport stdio`, `--quiet`
appended by `client.py`) matches the plugin's documented recipe and the plan review's live-verified
command shape.

## S1 fake-shim isolation (F10) — PASS

- **Install/rerun/exclude tests** (`fake_ai_raccoon` on `test_install_copies_plugin_and_activates`,
  `test_rerun_is_idempotent`, `test_exclude_prefix_uses_settings_verb`,
  `test_exclude_failure_path_mentions_settings_verb`): the fixture prepends its `bin/` to PATH, so
  both the script's `shutil.which("ai-raccoon")` and its `subprocess.run(["ai-raccoon", ...])`
  resolve to the shim; the real binary is never invoked. Additionally, the probe is **skipped** in
  these tests: no `--python` is passed, and `resolve_hermes_python` finds the *fake* hermes shim
  (no `exec "…/bin/hermes"` pattern) → returns `None` → probe skipped. So the real binary and the
  real bank are never touched by these four tests at all.
- **Probe test** keeps the real binary (required for the probe) and does NOT request
  `fake_ai_raccoon` — correct per plan S1. Its one real-bank side effect (`settings extract exclude
  add hermes/` against the default bank) is a deduped no-op while `hermes/` is already excluded
  (live-verified in the plan review) and is documented in the module docstring (lines 8-9). On a
  fresh machine it would perform the script's intended idempotent write — accepted by plan S1.
- **HERMES_HOME**: every test uses `temp_home`; the probe child additionally gets the script's own
  isolated `iso_home` (`env={"HERMES_HOME": str(iso_home)}`). No leakage to `~/.hermes`.
- **PATH ordering**: shim dir is prepended, so no shadowing hazard; the probe test has no shim, so
  the real `ai-raccoon` resolves.
- Fake shims require no real CLI state and record argv to test-local logs.

## Harness fix (F11) — PASS

Both shim bodies (`FAKE_HERMES`, `FAKE_AIRACCOON`) have their `#!/usr/bin/env python3` replaced with
`#!{sys.executable}` at write time — the pytest interpreter, which has yaml — fixing the pre-existing
breakage (env python3 lacks yaml → `ModuleNotFoundError`). `test_activation_failure_is_fatal`'s
broken shim imports nothing (no yaml), so its env-python3 shebang is harmless. The change is in the
RED test commit, exercised by the install/rerun tests on every run.

## Gates

- **G1** — re-verified live by this reviewer: `/usr/bin/python3 -m pytest
  integrations/hermes/tests/test_setup_script.py` → **9 passed in 2.84 s** (orchestrator: 9 passed
  in 2.73 s). Count matches plan G1 (7 → 9: + exclude-success, + exclude-failure-path).
- **G2** — RED witnessed: 3 failed / 6 passed on `2c2c1357`, exactly the three expected shapes
  (exclude-success argv, failure-path WARNING verb, probe `[probe] FAIL` with port-7721 stderr).
  GREEN after `bee2b621`; the fix commit touches only the script.
- **G3** — probe PASS/no-exclude-WARNING on the live script: command shape live-verified in the
  plan review (`--data-root <tmp> --transport stdio --quiet` stays alive, exits 0 at EOF);
  `hermes/` already excluded on the real bank. Consistent.
- **G4** — re-verified live by this reviewer: `py_compile` clean for both changed Python files.

## Low nits (non-blocking)

1. **M2 fail-message nuance** (test_setup_script.py:216-221): for the `exited with code 0`
   sub-case, the message reads `(throwaway exited with code 0)` without explaining that
   attach-exit-0 means an ai-raccoon owner existed and *vanished* during the poll. A future reader
   hitting this rare race would benefit from that sentence; the current text is still accurate.
   Optional.
2. **Module docstring phrasing** (test_setup_script.py:3-9): "so full-script runs never touch the
   real ~/.ai-raccoon bank" is immediately qualified by the next sentence (probe test keeps the
   real binary and its exclude write is a deduped no-op), so it is not misleading in context. Could
   be tightened to "shimmed full-script runs" for precision. Optional.

## Evidence (this review)

- `git merge-base main HEAD` = `dc3c742c`; `git diff dc3c742c HEAD --stat` = 4 files.
- `git show dc3c742c:Directory.Packages.props` == `git show HEAD:Directory.Packages.props`;
  `main` has `b716c1a8 deps: update packages` (DotNext/AWSSDK bumps) — main-side drift.
- `lsof -nP -iTCP:7721` → `AiRaccoon 5537 … 127.0.0.1:7721 (LISTEN)` — real server owns the port;
  the probe test ran the non-spawn branch in both witnessed suite runs.
- Suite re-run: `9 passed in 2.84s`; `py_compile` both files OK; venv python imports yaml OK.
- Grep: no stale `extract exclude add` outside `docs/work/` and historical records.

## Sign-off

Preflight complete. All phases PASS. Verdict: **APPROVE** — ready to merge to `main`.
