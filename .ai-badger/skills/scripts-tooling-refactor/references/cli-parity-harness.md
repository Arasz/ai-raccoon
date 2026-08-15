# Old-vs-new CLI parity harness (script conversions)

Recipe behind "Step 6b — parity harness". Validated on the project scripts-refactor
P3/P4: `patch-tool-shell.py` (125→26-line wrapper + `src/tool_shell.py` extraction)
and `regenerate-retrieval-golden.sh` → `.py`. Proves "usage/exit behavior identical
to today" with diffs, not eyeballing.

## When

Any conversion/move whose AC says "behavior identical to today" (CLI wrappers,
.sh → .py). Stronger than "each converted script at least runs": it diffs the
observable contract (stdout, stderr, exit code, output artifact bytes).

## Recipe

1. Extract the old script from git — works even after the rewrite landed in the
   worktree: `git show HEAD:scripts/patch-tool-shell.py > /tmp/old_pts.py`.
2. Build one fixture, copy it for each side (in-memory zip → bytes → two files).
3. Run old and new on the SAME arg matrix — the modes that matter: no-args
   usage, wrong-arg-count rejection, duplicate-arg rejection, full success.
   Capture stdout, stderr, exit code, and any output artifact separately.
4. Compare: `diff` each stream, `diff` exit codes, `cmp` output artifacts
   (patched nupkg must be byte-identical).
5. For .sh → .py that shells out to an external tool (`dotnet`), use a shim on
   PATH instead of running the real tool:
   ```bash
   cat > /tmp/shimdir/dotnet <<'EOF'
   #!/bin/bash
   echo "cwd=$(pwd)" >&2
   echo "env_regenerate=${<APP>_HARNESS_REGENERATE_GOLDEN:-UNSET}" >&2
   echo "args=$*" >&2
   [ "$FAIL" = "1" ] && exit 42
   exit 0
   EOF
   chmod +x /tmp/shimdir/dotnet && export PATH="/tmp/shimdir:$PATH" && command -v dotnet
   ```
   Then compare .sh vs .py: cwd, env var, args, echo output, and failure
   propagation (exit 42 must propagate, success-only echoes suppressed on failure).

## Pitfalls

- **The shim file MUST be named exactly like the real binary** (`dotnet`, not
  `fake_dotnet.sh`) or PATH lookup silently runs the real tool. Verify with
  `command -v dotnet` before the run.
- If the real tool ran anyway (missing shim), it may have side effects (dotnet
  test restores/builds); check `git status` after for stray changes.
- Export the repo var: `export REPO=...` — a plain `REPO=...` assignment in an
  earlier terminal call does NOT persist across calls.
- Positional args must be substituted per-invocation (old/new nupkg filenames
  differ). A shared static args string leaks the fixture filename into the
  rids/positionals and silently invalidates that mode (observed: the "wrong
  count" case became a bogus 6-rid success on BOTH sides).
- **Test-expectation trap:** when a ported module's test fails, probe the
  ORIGINAL first (`git show HEAD` + `importlib` exec) — error-ordering
  differences (e.g. prefix check firing before rid-count check, so a 0-RID
  file reports "unexpected shell shape" not "expected exactly one…") are the
  original's contract. Fix the test, not the port; the port is right when it
  matches the original byte-for-byte.

## Decision pattern: gated behavior beats suggested implementation

Requirement "usage/exit behavior identical to today" + suggestion "use
argparse" conflict: argparse changes the no-args case from exit 1 + docstring
to exit 2 + usage text. The GATED behavior wins — keep the exact sys.argv
shape (validation lives in src anyway, so the wrapper stays thin) and flag the
argparse deviation explicitly in the report. When a requirement and a
suggested implementation collide, the gated requirement wins; the report
carries the deviation.
