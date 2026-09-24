"""Pure logic for the test-economy skill: recognize a test-runner command from a shell
invocation, classify it full-suite vs filtered, count full-suite runs per session, and say
when the count has crossed the budget the test-run-economy invariant allows.

The invariant (features/common/invariants/test-run-economy.md): normal flow runs each
affected suite once — the modified surface plus the consumers of changed behavior — and
leaves the full suite to CI on push. CI alive means CI's run is the gate; CI dead means the
hooked-up local gates once, or one manual full-suite run when nothing is hooked up.
Repetition is diagnosis work, never verification.

State lives as ``test_economy`` rows in the user store (~/.ai-badger/ai-badger.db), one row
per project, each holding per-session counters. Advisory only: nothing here blocks a tool
call — the hook emits additionalContext and nothing else (changelog 0.33.0).
"""
from __future__ import annotations

import os
import shlex
from pathlib import Path, PurePosixPath
from typing import Any, Dict, List, Optional, Tuple

import badger_store  # vendored beside this script in production; engine/ canonical in tests

# Full-suite runs a session may make before the nudge fires (the nudge lands on run 3).
MAX_FULL = 2
# The escalation bar: from this many full-suite runs on, every run nags with the STOP form.
ESCALATE_AT = 5

THRESHOLD_ENV = "AI_BADGER_TEST_ECONOMY_MAX_FULL"
ESCALATE_ENV = "AI_BADGER_TEST_ECONOMY_ESCALATE_AT"

# Per-project session buckets kept in one row; older buckets fall off the front.
MAX_SESSIONS_PER_PROJECT = 8

_SHELL_TOOL_EXACT = frozenset({"Bash", "bash", "sh", "zsh", "shell"})
_SHELL_TOOL_SUBSTRINGS = ("bash", "shell", "exec", "command")

# Flags whose argument (or value) selects a subset of the suite.
_SELECTOR_FLAGS = frozenset({
    "-k", "-m",              # pytest marker/keyword
    "-run", "--run",         # go test / generic
    "--filter",              # dotnet test
    "--tests",               # gradle
    "--test",                # cargo --test <name>
    "--filter=",             # defensive
})
_SELECTOR_ASSIGNMENTS = ("-dtest=", "-dtest.testcasefilter=", "--filter", "-pl")

_PY_LAUNCHERS = frozenset({"python", "python3", "python3.10", "python3.11", "python3.12",
                           "python3.13", "python3.14", "pypy", "pypy3"})
_JS_PACKAGE_MANAGERS = frozenset({"npm", "yarn", "pnpm", "bun"})


def is_shell_tool(tool_name: Any) -> bool:
    """True for the shell-shaped tool names Claude/Copilot/Hermes use."""
    if not isinstance(tool_name, str) or not tool_name:
        return False
    if tool_name in _SHELL_TOOL_EXACT:
        return True
    lowered = tool_name.lower()
    if lowered != tool_name:
        return False  # Claude/Copilot names are CamelCase and already matched exactly
    return any(sub in lowered for sub in _SHELL_TOOL_SUBSTRINGS)


def extract_command(payload: Dict[str, Any]) -> str:
    """The shell command from a PostToolUse payload: `tool_input.command` (or `cmd`)."""
    tool_input = payload.get("tool_input") or {}
    if not isinstance(tool_input, dict):
        return ""
    command = tool_input.get("command") or tool_input.get("cmd") or ""
    return command if isinstance(command, str) else ""


def _argv(command: str) -> List[str]:
    """Split a command line; on unbalanced quoting fall back to a naive whitespace split."""
    try:
        return shlex.split(command)
    except ValueError:
        return command.split()


def _has_selector(argv: List[str], start: int, positional_is_selector: bool = True) -> bool:
    """Do the tokens after the runner phrase name a subset of the suite?

    A selector is a known subset flag (-k, --filter, --tests, -run, -Dtest=...) or a
    positional argument (a path, a test-name filter, a module). `./...` for go is the
    exception: it means the whole tree, so the caller handles it before us.
    """
    i = start
    while i < len(argv):
        token = argv[i]
        lowered = token.lower()
        if any(lowered.startswith(flag) for flag in _SELECTOR_ASSIGNMENTS):
            return True
        if token in _SELECTOR_FLAGS:
            return True
        if lowered.startswith("--") and "=" in token:
            name = token.split("=", 1)[0].lower()
            if name in {f.rstrip("=") for f in _SELECTOR_FLAGS}:
                return True
        if not token.startswith("-") and positional_is_selector:
            return True
        i += 1
    return False


def is_test_run(command: str) -> Optional[Dict[str, str]]:
    """Classify a shell command as a test run, or None when it is not one.

    Returns ``{"runner": <display name>, "kind": "full" | "filtered"}``. Err toward full:
    the pattern this hook exists to catch is the repeated full suite, so an unrecognized
    flag never demotes a run to filtered — only a known selector does.

    Matches on the launcher's basename, not its raw ``argv[0]``: this repo's own mandated
    ``.venv/bin/python3 -m pytest`` (the venv-python invariant) is a path, not a bare
    ``python3``, and a runner invoked by an absolute or relative path must count the same
    as one found on PATH.
    """
    argv = _argv(command or "")
    if not argv:
        return None
    first = PurePosixPath(argv[0]).name

    # python -m pytest / python -m unittest
    if first in _PY_LAUNCHERS and len(argv) >= 3 and argv[1] == "-m":
        module = argv[2]
        if module == "pytest":
            return {"runner": "pytest", "kind": _pytest_kind(argv[3:])}
        if module == "unittest":
            rest = argv[3:]
            named = [t for t in rest if not t.startswith("-") and t != "discover"]
            return {"runner": "unittest", "kind": "filtered" if named else "full"}
        return None

    named_runs = {
        "pytest": _pytest_kind(argv[1:]),
        "py.test": _pytest_kind(argv[1:]),
    }
    if first in named_runs:
        return {"runner": first, "kind": named_runs[first]}

    if first == "go" and len(argv) >= 2 and argv[1] == "test":
        rest = argv[2:]
        if "-run" in rest or any(t.startswith("-run=") for t in rest):
            return {"runner": "go test", "kind": "filtered"}
        if any(t == "./..." for t in rest):
            return {"runner": "go test", "kind": "full"}
        positional = [t for t in rest if not t.startswith("-")]
        return {"runner": "go test", "kind": "filtered" if positional else "full"}

    if first == "dotnet" and len(argv) >= 2 and argv[1] in ("test", "vstest"):
        rest = argv[2:]
        filtered = any(t == "--filter" or t.startswith("--filter=")
                       or (not t.startswith("-") and (".csproj" in t or ".sln" in t
                                                      or "/" in t))
                       for t in rest)
        return {"runner": "dotnet test", "kind": "filtered" if filtered else "full"}

    if first in _JS_PACKAGE_MANAGERS:
        rest = argv[1:]
        if rest and rest[0] in ("run",):
            rest = rest[1:]
        if rest and rest[0] in ("test", "t"):
            selector = [t for t in rest[1:] if not t.startswith("-")]
            return {"runner": f"{first} test",
                    "kind": "filtered" if selector else "full"}
        if first == "bun" and rest and rest[0] == "test":
            return {"runner": "bun test", "kind": "full"}

    if first == "npx" and len(argv) >= 2:
        runner = argv[1]
        if runner in ("jest", "vitest", "playwright", "mocha"):
            rest = argv[2:]
            if runner == "vitest" and rest and rest[0] == "run":
                rest = rest[1:]
            selector = [t for t in rest if not t.startswith("-")]
            return {"runner": runner, "kind": "filtered" if selector else "full"}

    if first in ("jest", "vitest", "mocha", "playwright"):
        selector = [t for t in argv[1:] if not t.startswith("-")]
        return {"runner": first, "kind": "filtered" if selector else "full"}

    if first == "cargo" and len(argv) >= 2 and argv[1] == "test":
        rest = argv[2:]
        if "--test" in rest:
            return {"runner": "cargo test", "kind": "filtered"}
        positional = [t for t in rest if not t.startswith("-")]
        return {"runner": "cargo test", "kind": "filtered" if positional else "full"}

    if first in ("mvn", "mvnw") and "test" in argv[1:]:
        rest = argv[1:]
        if any(t.lower().startswith(("-dtest=", "-dtestcase=", "-dgroups=")) for t in rest):
            return {"runner": "mvn test", "kind": "filtered"}
        return {"runner": "mvn test", "kind": "full"}

    gradle = first if first in ("gradle", "gradlew") else None
    if gradle and "test" in argv[1:]:
        if "--tests" in argv[1:]:
            return {"runner": "gradle test", "kind": "filtered"}
        return {"runner": "gradle test", "kind": "full"}

    if first == "mix" and len(argv) >= 2 and argv[1] == "test":
        positional = [t for t in argv[2:] if not t.startswith("-")]
        return {"runner": "mix test", "kind": "filtered" if positional else "full"}

    if first in ("phpunit", "pest"):
        positional = [t for t in argv[1:] if not t.startswith("-")]
        return {"runner": first, "kind": "filtered" if positional else "full"}

    if first == "php" and len(argv) >= 3 and argv[1] == "artisan" and argv[2] == "test":
        positional = [t for t in argv[3:] if not t.startswith("-")]
        return {"runner": "php artisan test", "kind": "filtered" if positional else "full"}

    if first == "rake" and len(argv) >= 2 and argv[1] in ("test", "spec"):
        return {"runner": "rake test", "kind": "full"}

    if first == "swift" and len(argv) >= 2 and argv[1] == "test":
        if "--filter" in argv[2:]:
            return {"runner": "swift test", "kind": "filtered"}
        return {"runner": "swift test", "kind": "full"}

    return None


# Shell metacharacters shlex.split leaves as literal tokens: whatever follows one of these
# belongs to the next stage of the pipeline (`| tail -5`), not to pytest's own arguments.
_SHELL_METACHARS = frozenset({"|", ";", "&&", ">"})

# pytest flags that take a separate value token (`-p no:cacheprovider`, `-n 4`, `--tb short`):
# the value must never be mistaken for a positional selector (a path or node id).
_PYTEST_VALUE_FLAGS = frozenset({"-p", "-n", "--tb", "-W", "-c", "--rootdir", "--confcutdir"})


def _split_at_metachar(token: str) -> Tuple[str, Optional[int]]:
    """*token* truncated at its first shell metacharacter (even glued on with no space, as
    in ``-q;``), and that cut position, or ``(token, None)`` when it carries none."""
    positions = [p for p in (token.find(mc) for mc in _SHELL_METACHARS) if p != -1]
    if not positions:
        return token, None
    cut = min(positions)
    return token[:cut], cut


def _pytest_kind(rest: List[str]) -> str:
    """pytest's kind: a known selector flag or a real positional (path/node id) means
    filtered. Skips a known flag's value token and stops at a shell metacharacter, so
    neither is mistaken for a positional filter (L4-7)."""
    i = 0
    while i < len(rest):
        token, cut = _split_at_metachar(rest[i])
        if token:
            if token in ("-k", "-m") or token.startswith(("-k=", "-m=")):
                return "filtered"
            if token in _PYTEST_VALUE_FLAGS:
                if cut is None:
                    i += 2
                    continue
                break  # the flag's value was cut off by the metachar; nothing more to see
            name = token.split("=", 1)[0]
            if "=" in token and name in _PYTEST_VALUE_FLAGS:
                i += 1
                continue
            if not token.startswith("-"):
                return "filtered"
        if cut is not None:
            break
        i += 1
    return "full"


def new_session_entry() -> Dict[str, Any]:
    """A fresh per-session counter row."""
    return {"full": 0, "filtered": 0, "fired": 0, "since": "", "last_seen": ""}


def advance(entry: Dict[str, Any], is_full: bool, now: str = "",
            max_full: Optional[int] = None, escalate_at: Optional[int] = None,
            ) -> Tuple[bool, bool, Dict[str, Any]]:
    """Count one run and decide whether the nudge fires.

    Fires on the run after the budget (full == max_full + 1, the 3rd by default) and then on
    every full run from the escalation bar onward. Runs between nudge and bar stay silent —
    the agent was told; give it room to act. Returns ``(fires, escalated, entry)``.
    """
    budget = _max_full() if max_full is None else max_full
    escalation_bar = _escalate_at() if escalate_at is None else escalate_at
    updated = dict(entry)
    updated["last_seen"] = now
    if is_full:
        updated["full"] = int(entry.get("full", 0)) + 1
    else:
        updated["filtered"] = int(entry.get("filtered", 0)) + 1

    full = updated["full"]
    fires = is_full and (full == budget + 1 or full >= escalation_bar)
    if fires:
        updated["fired"] = int(entry.get("fired", 0)) + 1
        if not entry.get("since"):
            updated["since"] = now
    return fires, is_full and full >= escalation_bar, updated


def _max_full() -> int:
    try:
        return int(os.environ.get(THRESHOLD_ENV, str(MAX_FULL)))
    except (TypeError, ValueError):
        return MAX_FULL


def _escalate_at() -> int:
    try:
        return int(os.environ.get(ESCALATE_ENV, str(ESCALATE_AT)))
    except (TypeError, ValueError):
        return ESCALATE_AT


# --- per-project persistence --------------------------------------------------------


def open_store():
    """The default user store: ``test_economy`` is already registered in USER_FAMILIES,
    born in SQLite with ``legacy_path=None`` — there is nothing to import or resurrect.

    A prior inline family passed ``legacy_path=lambda: None`` (a callable returning None,
    not an absent one), so ``_check_resurrections`` called ``None.exists()`` on every open.
    """
    return badger_store.open_user()


def load_state() -> Dict[str, Any]:
    """Per-project entries; ``{}`` fail-open when the store cannot be opened."""
    try:
        store = open_store()
        try:
            return store.kv_all("test_economy")
        finally:
            store.close()
    except Exception:  # pylint: disable=broad-exception-caught
        return {}


def get_entry(root: str) -> Dict[str, Any]:
    """The persisted entry for ``root``, normalised to the sessions-map shape."""
    value = load_state().get(str(Path(root).resolve()), {"sessions": {}})
    if not isinstance(value, dict) or not isinstance(value.get("sessions"), dict):
        return {"sessions": {}}
    return value


def set_entry(root: str, entry: Dict[str, Any]) -> None:
    """Persist ``entry`` as ``root``'s own row, keyed by its resolved absolute path."""
    store = open_store()
    try:
        store.kv_set("test_economy", str(Path(root).resolve()), entry)
    finally:
        store.close()


def update_entry(root: str, session: str, is_full: bool, now: str = "",
                  max_full: Optional[int] = None, escalate_at: Optional[int] = None,
                  ) -> Tuple[bool, bool, Dict[str, Any]]:
    """Atomically advance ``root``'s session entry through the store's own `kv_update` (L4-8).

    `get_entry` then `advance_session` then `set_entry`, as the hook used to run it, is three
    separate store round trips: a second invocation between the read and the write computes
    from the same stale entry, so one of the two increments is lost. `kv_update` runs the
    read, the advance, and the write inside one transaction, so a concurrent call blocks on
    the write lock and sees this call's result before it computes its own.
    """
    outcome: Dict[str, Any] = {}

    def _advance(current: Any) -> Dict[str, Any]:
        normalised = (current if isinstance(current, dict)
                     and isinstance(current.get("sessions"), dict) else {"sessions": {}})
        fires, escalated, updated = advance_session(
            normalised, session, is_full, now=now, max_full=max_full, escalate_at=escalate_at)
        outcome["fires"], outcome["escalated"] = fires, escalated
        return updated

    store = open_store()
    try:
        entry = store.kv_update("test_economy", str(Path(root).resolve()), _advance,
                                {"sessions": {}})
    finally:
        store.close()
    return outcome["fires"], outcome["escalated"], entry


def session_entry(entry: Dict[str, Any], session: str) -> Dict[str, Any]:
    """One session's counter row inside a project entry (its own budget)."""
    found = entry["sessions"].get(session)
    if not isinstance(found, dict):
        return new_session_entry()
    normalised = new_session_entry()
    normalised.update({k: found[k] for k in normalised if isinstance(found.get(k),
                                                                    (int, str))})
    return normalised


def advance_session(entry: Dict[str, Any], session: str, is_full: bool, now: str = "",
                    max_full: Optional[int] = None, escalate_at: Optional[int] = None,
                    ) -> Tuple[bool, bool, Dict[str, Any]]:
    """Advance one session's counters inside the project entry and prune old sessions.

    Eviction drops the session with the oldest ``last_seen``, never dict insertion order
    (R24): the busiest, longest-lived session is exactly the one insertion order evicted
    first, since it is also the one that joined earliest.
    """
    fires, escalated, session_row = advance(session_entry(entry, session), is_full,
                                            now=now, max_full=max_full,
                                            escalate_at=escalate_at)
    updated = dict(entry)
    sessions = dict(entry.get("sessions", {}))
    sessions[session] = session_row
    while len(sessions) > MAX_SESSIONS_PER_PROJECT:
        oldest = min(sessions, key=lambda s: sessions[s].get("last_seen", ""))
        sessions.pop(oldest)
    updated["sessions"] = sessions
    return fires, escalated, updated


# --- the message --------------------------------------------------------------------


def _gates_sentence(gates: List[str]) -> str:
    if gates:
        return (f"Local gate wiring detected: {', '.join(gates)} — if CI is dead, "
                f"run them once before push.")
    return ("No local gate wiring detected — if CI is dead, one manual full-suite run "
            "before push is the gate.")


def build_message(count: int, runner: str, gates: List[str], escalated: bool = False) -> str:
    """The text the hook emits. Imperative: a suggestion is what this replaces."""
    head = (f"[ai-badger] STOP — full-suite run #{count} this session ({runner})."
            if escalated else
            f"[ai-badger] Test-run economy: full-suite run #{count} this session ({runner}).")
    body = ("In normal flow each affected suite runs once — the modified surface plus the "
            "consumers of changed behavior — and the full suite is CI's job on push: read "
            "CI's verdict instead of re-running. Re-run only after a code change; if this "
            "repetition is deliberate flake diagnosis, say so and continue.")
    return f"{head} {body} {_gates_sentence(gates)}"


# --- local gate wiring --------------------------------------------------------------


def detect_local_gates(root: str) -> List[str]:
    """The local gate wiring visible in ``root``; ``[]`` on anything missing or unreadable.

    Recognised: lefthook, pre-commit, husky, and a real (non-sample) git pre-push hook.
    This is the CI-dead branch's input: what the project can run once before a push.
    """
    base = Path(root)
    gates: List[str] = []
    try:
        if any((base / name).exists() for name in
               ("lefthook.yml", "lefthook.yaml", ".lefthook")):
            gates.append("lefthook")
        if (base / ".pre-commit-config.yaml").exists():
            gates.append("pre-commit")
        if (base / ".husky").is_dir() or _package_json_mentions_husky(base):
            gates.append("husky")
        git_hook = base / ".git" / "hooks" / "pre-push"
        if git_hook.is_file() and os.access(git_hook, os.X_OK) and not _is_hook_sample(git_hook):
            gates.append("git pre-push hook")
    except OSError:
        return []
    return gates


def _package_json_mentions_husky(base: Path) -> bool:
    package_json = base / "package.json"
    try:
        return "husky" in package_json.read_text(encoding="utf-8")
    except (OSError, ValueError, UnicodeDecodeError):
        return False


def _is_hook_sample(path: Path) -> bool:
    return path.name.endswith(".sample")
