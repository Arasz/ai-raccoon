# Verification-tracker evidence: schema and worked diagnosis

Mechanics learned running wave-based worktree implementations (2026-08-04): the
per-turn verification reminder fired repeatedly for changed paths in git worktrees,
and events recorded "passed" without a real run.

## Database

`~/.hermes/verification_evidence.db` (SQLite):

```sql
CREATE TABLE verification_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at TEXT NOT NULL,
    session_id TEXT NOT NULL,
    cwd TEXT NOT NULL,
    root TEXT NOT NULL,
    command TEXT NOT NULL,
    canonical_command TEXT NOT NULL,
    kind TEXT NOT NULL,
    scope TEXT NOT NULL,
    status TEXT NOT NULL,
    exit_code INTEGER NOT NULL,
    output_summary TEXT NOT NULL
);

CREATE TABLE verification_state (
    session_id TEXT NOT NULL,
    root TEXT NOT NULL,
    last_event_id INTEGER,
    last_edit_at TEXT,
    changed_paths_json TEXT NOT NULL DEFAULT '[]',
    PRIMARY KEY (session_id, root)
);
```

The reminder fires when `changed_paths_json` (for this session+root) has paths newer
than the last event. `verification_state` rows appear per session per root — including
rows for subagent sessions and other concurrent sessions working the same repo.

## Worked diagnosis: event recorded but the reminder kept firing

Symptom: `hermes-verify-*.sh` ran, result metadata showed
`verification_evidence: {status: passed}`, yet the next turn re-flagged the same paths.

1. Inspect recent events:
   `SELECT id, cwd, root, status FROM verification_events ORDER BY id DESC LIMIT 5;`
   → the event's `cwd`/`root` were the MAIN checkout, because the terminal command ran
   with `workdir=/Users/.../repo` and the script only `cd`'d internally. The changed
   paths pointed at `.ai-badger/worktrees/<wave>/...` — a different root.
2. Re-run the identical script with the terminal `workdir` set to the worktree itself.
   → new event recorded with root = the worktree; reminder cleared.

Rule: the hook snapshots the terminal invocation's working directory. An internal
`cd` in the script is invisible to it.

## False-pass from text parsing

Symptom: `mktemp /private/var/.../T/hermes-verify-XXXXXX.sh` failed with
`mkstemp failed: File exists`. Root cause: the template name was reused across runs —
an earlier aborted attempt left a file under that same name (BSD mktemp can create
literal-named files when the template's X-run is not at the very end), and the next
`mktemp` collided. The script never ran, yet the result carried
`verification_evidence: {status: passed}` — the hook matched "PASS" strings in the
heredoc body against the command text.

Fix: a UNIQUE topic-specific template per run (`hermes-verify-<topic>-XXXXXX.sh`,
one run per topic; `mktemp -t hermes-verify-<topic>` also works), then verify the run
really executed: real stdout + `rc=0` in the result. If the event was recorded from
text only, re-run properly and state that the earlier record was a text-parse
artifact, not evidence.

## Canonical-gate wrapper (code changes)

```bash
VERIFY_SCRIPT=$(mktemp /private/var/folders/k9/gxjyv0q50tn0_sngj8zg30140000gn/T/hermes-verify-<purpose>-XXXXXX.sh)
cat > "$VERIFY_SCRIPT" <<'EOF'
#!/bin/bash
set -u
cd <root-to-verify> || exit 1
git status --short | grep -q . && { echo "FAIL: tree not clean"; exit 1; }
BUILD=$(dotnet build --nologo -v q 2>&1)
echo "$BUILD" | grep -q "0 Error(s)" || { echo "FAIL build"; exit 1; }
FULL=$(dotnet test --nologo 2>&1)
echo "$FULL" | grep -q "Passed!" || { echo "FAIL suite"; exit 1; }
echo "$FULL" | grep -q "Failed:     0" || { echo "FAIL suite-count"; exit 1; }
echo "PASS build 0 errors"
echo "PASS full suite"
EOF
chmod +x "$VERIFY_SCRIPT" && "$VERIFY_SCRIPT"; RC=$?; rm -f "$VERIFY_SCRIPT"; echo "script cleaned up (rc=$RC)"
```

Key points: assert the marker strings (`0 Error(s)`, `Passed!`, `Failed:     0`) so
the hook's text parse and the real output agree; keep the tree-clean check; capture RC.

## First-run-after-build flake

Twice the first full-suite run after a build/merge failed (1 test) while every
subsequent run passed: the build copies `tests/.../Resources/jsaa-memory.db` to the
test output dir (PreserveNewest), and a test starting mid-copy reads a stale or
half-written file. Re-run once before investigating; treat two consecutive green runs
as the confirmation, not the first failure.
