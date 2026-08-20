# Hermetic subprocess test harness — shim shebangs & port preconditions

Patterns from task hermes-provider-cli-compat (2026-08-20, PR #392): a subprocess-driven Python test suite for a setup script that spawns CLIs.

## 1. Fake-CLI shims: pin the shebang to sys.executable

A test shim written as `#!/usr/bin/env python3` resolves at spawn time to whatever `python3` is FIRST on the child's PATH — not the interpreter running pytest. On a dev machine where homebrew python3 lacks a module the shim needs (`yaml`),
every test that invokes the shim fails with `ModuleNotFoundError`
from INSIDE the shim — and, worse, the failure can MASK the real bug: two unrelated tests failed on the missing module while the target test's true symptom (a port conflict) went unreported behind the same error shape.

Fix (in the fixture, not the shim string):

```python
shim.write_text(
    SHIM_TEXT.replace("#!/usr/bin/env python3", f"#!{sys.executable}"),
    encoding="utf-8")
```

- Do NOT convert the shim body to an f-string to inject the shebang: the body usually contains `{}` dict literals (`cfg.setdefault("memory", {})`) →
  `SyntaxError: f-string: empty expression not allowed`.
- Same pattern for ANY shim standing in for a CLI on PATH (hermes, ai-raccoon, bws, ...). Keep the body free of dependencies the interpreter might lack (`import yaml` in the shim is what bit us; argv-recording shims need only stdlib).
- Broken-shim variants used to simulate CLI failure must ALSO be pinned or they may fail for the wrong reason (a shim that should print 'boom' and exit 3 dies earlier on an import error — same exit class, wrong cause).

## 2. Port-precondition tests: deterministic RED on any machine

A test that must go RED when a background service owns a fixed port (old code uses the port; new code must not) needs the port to be OWNED during the test — on the dev machine a real server may already own it, on CI nothing does. With the
port free, old code starts its own listener and the test goes GREEN (false green — pins nothing).

Mechanics (M1-M3 from the plan review, all live-verified):

- **M1 — command shape:** root options MUST precede the verb. For ai-raccoon:
  `serve --port 7721 --data-root <dir>` exits 15 ("Unrecognized command or argument '--data-root'") and binds nothing → false green. Correct:
  `ai-raccoon --data-root <dir> serve --port 7721`. (For other CLIs, verify option ordering against the tool's own help before writing the test.)
- **M2 — classify by liveness, not spawn outcome:** a `serve` against a port already owned by the same kind of server ATTACHES and exits 0; only a FOREIGN listener makes it exit non-zero; and when it binds the port itself it stays alive. So
  after the readiness poll:
    - throwaway still alive → it owns the port (precondition holds);
    - throwaway exited 0 → the real server owns it (attach; holds);
    - throwaway exited non-zero → a foreign listener owns it (holds);
    - NO listener after the timeout → `pytest.fail(...)` with a message saying the port must be owned or old code passes for the wrong reason. Never proceed with the port free.
- **M3 — spawn/teardown:** `subprocess.Popen` (a bound server runs until killed — never `subprocess.run`), readiness via polling a TCP connect to 127.0.0.1:<port> (~0.25 s interval, ~10 s deadline), and `try/finally`
  teardown: `terminate()` + `wait(timeout=10)` (fall back to `kill()` +
  `wait()`), remove the throwaway's temp data root. Only terminate what the test spawned — if the real server owns the port, touch nothing.

The stdin/stdout bound-URL line alone cannot discriminate bind from attach (both print it) — liveness is the discriminator.

## 3. Discriminator assertions

When old and new behavior differ only in an argv the script passes to a CLI, assert on the RECORDED argv (the shim appends `" ".join(sys.argv[1:])` to a log file), not on the script's stdout: both old and new code print the same success
line when the shim exits 0. State in the test docstring which assertion is the RED discriminator. Add a failure-path sibling test (shim exits non-zero) to pin the fallback message text.
