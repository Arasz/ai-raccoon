"""Memory-grade hook logic: match memory_search calls, gate on AI_BADGER_MEMORY_GRADE=1,
append one JSONL line per search to a machine-wide quality log, stash the grade ask,
and fill grades back in via the `grade`/`probe` CLI. Default OFF (absent/unset/0/garbage):
no reads, no writes, no injection. IO is internally guarded: a hook must never raise."""
from __future__ import annotations

import json
import os
import shlex
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional

ENABLED_ENV = "AI_BADGER_MEMORY_GRADE"
LOG_FILE = Path.home() / ".ai-badger" / "memory-grade" / "memory-quality.jsonl"
PENDING_FILE = Path.home() / ".ai-badger" / "memory-grade" / "pending.json"
# The helper's own absolute path, so every deployment shape asks for its own copy.
HELPER = Path(__file__).resolve()

_SEARCH_TOOL = "memory_search"
_MCP_PREFIX = "mcp__"
_DOUBLE_UNDERSCORE = "__"


def enabled() -> bool:
    """True when AI_BADGER_MEMORY_GRADE is exactly "1" (anything else is off)."""
    return os.environ.get(ENABLED_ENV, "") == "1"


def is_memory_search(tool_name: Any) -> bool:
    """True for any naming spelling of the memory_search tool; never matches other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith(_MCP_PREFIX):
        name = name[len(_MCP_PREFIX):].split(_DOUBLE_UNDERSCORE, 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == _SEARCH_TOOL


def _now_iso() -> str:
    """UTC timestamp with microseconds; unique per line and the grade pointer."""
    return datetime.now(timezone.utc).isoformat()


def _result_payload(result: Any) -> Any:
    """The parsed tool result, or {"raw": result} when it is not JSON."""
    if isinstance(result, (dict, list, int, float, bool)) or result is None:
        return result
    if isinstance(result, bytes):
        try:
            result = result.decode("utf-8")
        except UnicodeDecodeError:
            return {"raw": str(result)}
    if isinstance(result, str):
        try:
            return json.loads(result)
        except ValueError:
            return {"raw": result}
    return {"raw": str(result)}


def _build_line(args: Dict[str, Any], result: Any) -> Dict[str, Any]:
    """One JSONL line; key spellings are read defensively (F4), workspace null when absent."""
    return {
        "ts": _now_iso(),
        "query": args.get("query", ""),
        "scope": args.get("scope", "all"),
        "projectId": args.get("projectId", args.get("project_id")),
        "workspaceId": args.get("workspaceId", args.get("workspace_id")),
        "result": _result_payload(result),
        "usefulness": None,
        "note": None,
    }


def _append_log(line: Dict[str, Any]) -> bool:
    """Append one line, creating the log's parent directory lazily; False on IO failure."""
    try:
        LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
        with LOG_FILE.open("a", encoding="utf-8") as fh:
            fh.write(json.dumps(line) + "\n")
        return True
    except OSError:
        return False


def _ask_text(ts: str) -> str:
    """The one-line grade ask; ts is the exact log-line pointer to grade."""
    return (f"[ai-badger] Rate that memory_search's usefulness 1-5 (5=best, or skip): "
            f"python3 {shlex.quote(str(HELPER))} grade {ts} <1-5> [note]")


def _load_pending() -> Dict[str, str]:
    """Load the pending-ask file; {} on missing file, read error, or malformed JSON."""
    try:
        raw = PENDING_FILE.read_text(encoding="utf-8")
    except OSError:
        return {}
    try:
        data = json.loads(raw)
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _save_pending(pending: Dict[str, str]) -> bool:
    """Persist the pending-ask file, creating parent directories as needed; False on IO failure."""
    try:
        PENDING_FILE.parent.mkdir(parents=True, exist_ok=True)
        PENDING_FILE.write_text(json.dumps(pending), encoding="utf-8")
        return True
    except OSError:
        return False


def _set_pending(project: str, ask: str) -> None:
    """Stash the ask keyed by the project's resolved absolute path (last wins)."""
    pending = _load_pending()
    pending[str(Path(project).resolve())] = ask
    _save_pending(pending)


def log_search(args: Dict[str, Any], result: Any, cwd: str,
               stash: bool = True) -> Optional[str]:
    """Append one line for a memory_search call and stash the ask; the ask text back.

    Returns None when disabled (no reads, no writes) or when the log write fails.
    ``stash=False`` for transports that return the ask directly (Claude's
    PostToolUse) — they must not leave a stale ask a Hermes session would pop.
    """
    if not enabled():
        return None
    line = _build_line(args, result)
    ts = line["ts"]
    if not _append_log(line):
        return None
    ask = _ask_text(ts)
    if stash:
        _set_pending(cwd, ask)
    return ask


def pop_ask(project: str) -> Optional[str]:
    """Return and clear the pending grade ask for project, or None (also when disabled)."""
    if not enabled():
        return None
    pending = _load_pending()
    key = str(Path(project).resolve())
    ask = pending.pop(key, None)
    if ask is not None:
        _save_pending(pending)
    return ask


def _valid_grade(value: str) -> bool:
    """Grade guard: an integer in 1..5, or not a grade at all."""
    try:
        return int(value) in range(1, 6)
    except ValueError:
        return False


def grade_line(ts: str, usefulness: str, note: str = "") -> int:
    """Fill the line whose exact ts matches, in place; exit 1 on a bad grade or unknown ts."""
    if not _valid_grade(usefulness):
        print("grade must be an integer 1-5", file=sys.stderr)
        return 1
    try:
        lines = LOG_FILE.read_text(encoding="utf-8").splitlines()
    except OSError:
        lines = []
    target = None
    for index, raw in enumerate(lines):
        if not raw.strip():
            continue
        try:
            line = json.loads(raw)
        except ValueError:
            continue
        if line.get("ts") == ts:
            target = index
            break
    if target is None:
        print(f"no log line with ts {ts} — not found", file=sys.stderr)
        return 1
    line = json.loads(lines[target])
    line["usefulness"] = int(usefulness)
    line["note"] = note or None
    lines[target] = json.dumps(line)
    try:
        LOG_FILE.write_text("\n".join(lines) + "\n", encoding="utf-8")
    except OSError:
        print("could not write the quality log", file=sys.stderr)
        return 1
    return 0


def probe() -> int:
    """Print the config state, the log path, and the last 3 lines."""
    print(f"{ENABLED_ENV}={os.environ.get(ENABLED_ENV, '')} "
          f"({'enabled' if enabled() else 'disabled'})")
    print(f"log: {LOG_FILE}")
    try:
        lines = LOG_FILE.read_text(encoding="utf-8").splitlines()
    except OSError:
        lines = []
    for raw in lines[-3:]:
        if raw.strip():
            print(raw)
    return 0


def main(argv: Optional[list] = None) -> int:
    """CLI: `grade <ts> <1-5> [note]` fills a line; `probe` prints state and the log."""
    args = list(sys.argv[1:] if argv is None else argv)
    if not args:
        print("usage: memory_grade.py grade <ts> <1-5> [note] | probe", file=sys.stderr)
        return 1
    command, rest = args[0], args[1:]
    if command == "grade" and len(rest) >= 2:
        return grade_line(rest[0], rest[1], " ".join(rest[2:]))
    if command == "probe":
        return probe()
    print(f"unknown command: {command}", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
