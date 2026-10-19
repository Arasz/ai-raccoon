"""Shared memory-first gate: search-call matchers, the searched-pattern store, the deny decision.

A repo text-search call (grep/egrep/fgrep/rg/find in any segment of a shell command, or a
Grep/Glob/search_files tool) passes only when every pattern it searches for was already
memory-searched for the project. The first sighting of a pattern
is denied: the gate runs one memory search for it and records it as searched when the bank
answered. An agent's own memory_search records its query too (the PostToolUse hook).

Searched patterns live per project id in the audit store's hook_state table (file fallback
under SEARCHED_DIR) and are never reset. record_search keeps the per-session memory_first
telemetry row. A hook must never raise: Copilot's preToolUse is fail-closed, so exit 0 always.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import re
import shlex
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Set, Tuple

try:
    import badger_store  # vendored beside this script in production; engine/ canonical in tests
except ImportError:  # a deployment without the store keeps the file surfaces
    badger_store = None  # pylint: disable=invalid-name

MARKER_DIR = Path.home() / ".ai-badger" / "memory-first"
SEARCHED_DIR = Path.home() / ".ai-badger" / "memory-first-searched"


# Wall clock for the gate's own memory search, from hook start: hook timeouts are 10 s and
# Hermes abandons a callback at 30 s.
SEARCH_SECONDS = 4.0
HITS_MAX_CHARS = 2000
QUERY_MAX_CHARS = 500
# Seconds the searched-pattern store waits on a lock: a read and a write per call stay well
# inside the 10 s hook timeout.
STORE_TIMEOUT_SECONDS = 1.0

_STATE_TABLE = "hook_state"
_STATE_PREFIX = "memory_first.searched."
# Stored entry kinds: a pattern the gate searched for, a query an agent's memory_search ran.
PATTERN_KIND = "p:"
QUERY_KIND = "q:"
_UNKNOWN_PROJECT = "unknown"
_GATE_SESSION = "memory-first-gate"

# Keeps a hung/slow git from blocking the tool call the gate is meant to police.
_GIT_TIMEOUT_SECONDS = 2

# badger_lib.GIT_LOCATION_ENV, repeated because this ships into projects that have no framework
# checkout to import it from. git exports GIT_DIR to its hooks and GIT_COMMON_DIR answers
# `--git-common-dir` outright, so a child that inherits either reports another repository's
# layout. tests/test_git_invocation.py pins every copy against the original.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")


def git_env(env=None) -> dict:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _load_beside(stem: str) -> Optional[Any]:
    """`<stem>.py` from this file's directory under a private module name; None when absent."""
    try:
        spec = importlib.util.spec_from_file_location(
            "ai_badger_memory_first_" + stem, Path(__file__).resolve().parent / f"{stem}.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except Exception:  # pylint: disable=broad-exception-caught  # a gate never raises
        return None
    return module


shell_parser = _load_beside("shell_parser")  # pylint: disable=invalid-name
_MEMORY_CONTEXT: List[Any] = []

# Shell programs that are repo text search, in any segment of a command.
_GREP_PROGRAMS = ("grep", "egrep", "fgrep")
_SEARCH_PROGRAMS = _GREP_PROGRAMS + ("rg", "rg.exe", "find")

# Tool names that are always text search, by any host's spelling (case-insensitive).
_SEARCH_TOOLS = ("search_files", "grep", "rg", "glob")
_SHELL_TOOLS = ("bash", "terminal")

# Options that take a value, per family: a value is never the pattern operand.
_VALUE_SHORT = {"grep": "ABCDdefm", "rg": "ABCEMTdefgjmrt"}
_VALUE_LONG = {
    "grep": ("--after-context", "--before-context", "--context", "--max-count", "--file",
             "--regexp", "--include", "--exclude", "--exclude-dir", "--exclude-from", "--label",
             "--binary-files", "--devices", "--directories", "--group-separator"),
    "rg": ("--after-context", "--before-context", "--context", "--max-count", "--file",
           "--regexp", "--glob", "--iglob", "--type", "--type-not", "--type-add", "--type-clear",
           "--max-columns", "--threads", "--encoding", "--replace", "--max-depth",
           "--max-filesize", "--sort", "--sortr", "--pre", "--pre-glob", "--context-separator",
           "--field-match-separator", "--field-context-separator", "--path-separator",
           "--engine", "--colors", "--color", "--dfa-size-limit", "--regex-size-limit",
           "--ignore-file"),
}
_FIND_PATTERN_FLAGS = ("-name", "-iname", "-path", "-ipath", "-regex", "-iregex")

# A grep/rg that reads a pipe from one of these programs filters live command output, which
# memory cannot answer: the segment is not repo search. Only the immediate upstream counts.
EXCLUDED_UPSTREAM = ("git", "gh", "ps", "ls", "env", "printenv", "history", "docker", "kubectl",
                     "pip", "npm", "lsof", "brew", "dotnet")
# Listed programs whose output is a repo file listing or search, so a grep over it stays gated.
_FILE_LISTING_GIT = ("ls-files", "ls-tree", "grep")
_GIT_VALUE_OPTIONS = ("-C", "-c", "--git-dir", "--work-tree")

_MCP_PREFIX = "mcp__"
_DOUBLE_UNDERSCORE = "__"
_PI_MCP_PREFIX = "mcp_"


def _pi_mcp_spelling_matches(tool_name: str, target: str) -> bool:
    """True when `target` is a trailing underscore-joined segment of pi's MCP spelling.

    pi names MCP tools `mcp_<server>_<tool>` (single underscores) and a server name may
    itself contain underscores, so the tool part is matched as any trailing `_`-joined tail.
    """
    if not tool_name.startswith(_PI_MCP_PREFIX) or tool_name.startswith(_MCP_PREFIX):
        return False
    parts = tool_name[len(_PI_MCP_PREFIX):].split("_")
    return any("_".join(parts[i:]) == target for i in range(1, len(parts)))


def is_memory_search(tool_name: Any) -> bool:
    """True for any naming spelling of the memory_search tool; never matches other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith(_MCP_PREFIX):
        name = name[len(_MCP_PREFIX):].split(_DOUBLE_UNDERSCORE, 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    return name == "memory_search" or _pi_mcp_spelling_matches(tool_name, "memory_search")


_REASON = (
    "Memory-first gate: run memory_search (projectId={project_id}) with the search pattern "
    "before repo text search, then re-issue the call."
)


# ------------------------------------------------------------------ search-call matching
_Segment = Tuple[str, Tuple[str, ...], Tuple[str, ...], Optional[Any]]


def _commands(command: str) -> List[_Segment]:
    """(program, args, every word, parsed command) per simple command; without the lexer,
    shlex as one segment with no parsed command (so nothing downstream of a pipe is known)."""
    if shell_parser is not None:
        try:
            return [(c.program.lower(), tuple(c.args), tuple(c.words), c)
                    for c in shell_parser.parse(command)]
        except Exception:  # pylint: disable=broad-exception-caught
            return []
    try:
        words = shlex.split(command, comments=True)
    except ValueError:
        return []
    if words and words[0].rsplit("/", 1)[-1] == "rtk":  # rtk's `grep`/`rg`/`find` filters
        words = [w for w in words[1:] if not w.startswith("-")]
    if not words:
        return []
    return [(words[0].rsplit("/", 1)[-1].lower(), tuple(words[1:]), tuple(words), None)]


def _split(pattern: str, extended: bool) -> List[str]:
    """A grep pattern's alternatives: `\\|` always, a bare `|` under ERE/PCRE/rg."""
    parts = re.split(r"\\\|", pattern)
    if extended:
        parts = [piece for part in parts for piece in part.split("|")]
    return parts


def _grep_parse(program: str, args: Tuple[str, ...]) -> Tuple[List[str], List[str], bool]:
    """(patterns, file operands, walks) of a grep/rg call: the `-e`/`--regexp` values, else the
    first operand after the options that take a value; every other operand (all of them under
    `-e`/`-f`) is a file; *walks* when it recurses (`-r`, `-R`, `-d recurse`) or lists files."""
    family = "rg" if program.startswith("rg") else "grep"
    shorts, longs = _VALUE_SHORT[family], _VALUE_LONG[family]
    explicit: List[str] = []
    operands: List[str] = []
    extended = family == "rg" or program == "egrep"
    from_file = walks = False
    index = 0
    while index < len(args):
        arg = args[index]
        index += 1
        if arg == "--":
            operands.extend(args[index:])
            break
        if arg.startswith("--"):
            name, equals, value = arg.partition("=")
            extended = extended or name in ("--extended-regexp", "--perl-regexp")
            walks = walks or name in ("--recursive", "--dereference-recursive", "--files")
            if name in longs:
                if not equals:
                    value = args[index] if index < len(args) else ""
                    index += 1
                if name == "--regexp":
                    explicit.append(value)
                from_file = from_file or name == "--file"
                walks = walks or (name == "--directories" and value == "recurse")
            continue
        if arg.startswith("-") and len(arg) > 1:
            for position, letter in enumerate(arg[1:], 1):
                extended = extended or (family == "grep" and letter in "EP")
                walks = walks or (family == "grep" and letter in "rR")
                if letter in shorts:
                    value = arg[position + 1:]
                    if not value:
                        value = args[index] if index < len(args) else ""
                        index += 1
                    if letter == "e":
                        explicit.append(value)
                    from_file = from_file or letter == "f"
                    walks = walks or (family == "grep" and letter == "d" and value == "recurse")
                    break
            continue
        operands.append(arg)
    files = operands if (explicit or from_file) else operands[1:]
    # under -f the patterns live in a file the gate cannot read, so no operand is a pattern
    sources = explicit or ([] if from_file else operands[:1])
    patterns = [part for raw in sources for part in _split(raw, extended)]
    return patterns, files, walks


def _feeding_program(upstream: Any) -> str:
    """The program writing into a pipe: what runs past rtk/sudo/env wrappers, else the first
    word that is not an assignment (`env` alone prints the environment)."""
    if upstream.program:
        return upstream.program.lower()
    for word in upstream.words:
        if not shell_parser.ASSIGNMENT.match(word):
            return word.rsplit("/", 1)[-1].lower()
    return ""


def _reads_excluded_pipe(command: Any, files: List[str], walks: bool) -> bool:
    """True when a grep/rg segment filters the stdin an EXCLUDED_UPSTREAM program pipes it:
    no file operand, no recursion, no input redirect, not run by xargs."""
    upstream = getattr(command, "upstream", None)
    if upstream is None or files or walks:
        return False
    if any(op.startswith("<") for op, _ in command.redirects):
        return False
    if any(word.rsplit("/", 1)[-1] == "xargs" for word in command.words[:command.start]):
        return False
    program = _feeding_program(upstream)
    return program in EXCLUDED_UPSTREAM and not _lists_files(program, upstream)


def _lists_files(program: str, upstream: Any) -> bool:
    """True when an excluded program prints repo files: `git ls-files|ls-tree|grep`, `ls -R`."""
    args = list(upstream.words[upstream.start + 1:]) if upstream.program else []
    if program == "ls":
        return any(a == "--recursive" or (a.startswith("-") and not a.startswith("--")
                                          and "R" in a) for a in args)
    if program == "git":
        skip = False
        for arg in args:
            if skip:
                skip = False
            elif arg in _GIT_VALUE_OPTIONS:
                skip = True
            elif not arg.startswith("-"):
                return arg in _FILE_LISTING_GIT
    return False


def _find_patterns(args: Tuple[str, ...]) -> List[str]:
    return [args[i + 1] for i, arg in enumerate(args[:-1]) if arg in _FIND_PATTERN_FLAGS]


def _tool_patterns(tool_input: Any) -> List[str]:
    if isinstance(tool_input, dict):
        for field in ("pattern", "query"):
            value = tool_input.get(field)
            if isinstance(value, str) and normalize(value):
                return [value]
        words = " ".join(v for v in tool_input.values() if isinstance(v, str))
        if normalize(words):
            return [words]
    return []


def search_patterns(tool_name: Any, tool_input: Any) -> Optional[List[str]]:
    """The patterns a text-search call searches for, or None when it is not text search.

    A segment with no extractable pattern is keyed on its own words, so the list of a
    search call is never empty.
    """
    if not isinstance(tool_name, str):
        return None
    name = tool_name.lower()
    if name in _SEARCH_TOOLS:
        return _tool_patterns(tool_input) or [name]
    if name not in _SHELL_TOOLS or not isinstance(tool_input, dict):
        return None
    command = tool_input.get("command")
    if not isinstance(command, str):
        return None
    found: Optional[List[str]] = None
    for program, args, words, parsed in _commands(command):
        if program not in _SEARCH_PROGRAMS:
            continue
        if program == "find":
            extracted = _find_patterns(args)
        else:
            extracted, files, walks = _grep_parse(program, args)
            if _reads_excluded_pipe(parsed, files, walks):
                continue
        found = (found or []) + ([p for p in extracted if normalize(p)] or [" ".join(words)])
    return found


def is_text_search(tool_name: Any, tool_input: Any) -> bool:
    """True when the tool call is repo text search the gate polices."""
    return search_patterns(tool_name, tool_input) is not None


RAW_GREP_SCRIPT = (Path(__file__).resolve().parents[2] / "get-raw-grep-result" /
                   "scripts" / "get_raw_grep_result.py")


def pattern_hash(pattern: str) -> str:
    """Return the stable SHA-256 handle for one exact grep pattern."""
    return hashlib.sha256(pattern.encode("utf-8")).hexdigest()


def _capture_scope(cwd: str = "") -> str:
    """Namespace raw results by the real working directory to isolate repositories."""
    return hashlib.sha256(os.path.realpath(cwd or os.getcwd()).encode("utf-8")).hexdigest()


def _capture_segment(parsed: Any, program: str, args: Tuple[str, ...]) -> str:
    """Rebuild a grep command with its input pipeline and without adjacent commands."""
    if parsed is None or not getattr(parsed, "upstream", None):
        return shlex.join((program, *args))
    pipeline = []
    current = parsed
    while current is not None:
        words = shlex.join(current.words)
        redirects = " ".join(
            operator + (" " + shlex.quote(target) if target else "")
            for operator, target in current.redirects)
        pipeline.append((words + (" " + redirects if redirects else "")).strip())
        current = current.upstream
    return " | ".join(reversed(pipeline))


def capture_greps(tool_name: Any, tool_input: Any, cwd: str = "",
                  selected: Optional[Set[str]] = None) -> List[Tuple[str, str]]:
    """Launch each relevant shell grep/rg segment once; return (pattern, hash) handles."""
    if not isinstance(tool_name, str) or tool_name.lower() not in _SHELL_TOOLS:
        return []
    command = tool_input.get("command") if isinstance(tool_input, dict) else None
    if not isinstance(command, str):
        return []
    launched: List[Tuple[str, str]] = []
    captured: Set[str] = set()
    for program, args, _words, parsed in _commands(command):
        if program not in _GREP_PROGRAMS + ("rg", "rg.exe"):
            continue
        patterns, files, walks = _grep_parse(program, args)
        if _reads_excluded_pipe(parsed, files, walks):
            continue
        patterns = [pattern for pattern in patterns
                    if (selected is None or pattern in selected) and pattern not in captured]
        if not patterns:
            continue
        segment = _capture_segment(parsed, program, args)
        keys = [pattern_hash(pattern) for pattern in patterns]
        try:
            subprocess.Popen(
                [sys.executable, str(RAW_GREP_SCRIPT), "--capture",
                 _capture_scope(cwd) + ":" + ",".join(keys), segment],
                cwd=cwd or os.getcwd(), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, start_new_session=True, close_fds=True)
            launched.extend(zip(patterns, keys))
            captured.update(patterns)
        except OSError:
            continue
    return launched


# ------------------------------------------------------------------ the searched-pattern store
def normalize(text: str) -> str:
    """Comparable form: regex escapes undone (`\\.` -> `.`), `^ $ \\b` and grouping
    parentheses dropped, lowercase."""
    text = re.sub(r"\\[bB<>]", "", text)
    text = re.sub(r"(?<!\\)[()]", "", text)
    text = re.sub(r"\\(.)", r"\1", text)
    text = text.replace("^", "").replace("$", "")
    return " ".join(text.lower().split())


def _persistable(project: str) -> bool:
    return bool(project) and project != _UNKNOWN_PROJECT


def _searched_path(project: str) -> Path:
    return SEARCHED_DIR / (_safe_session(project) or "_")


def searched(project: str) -> Set[str]:
    """Every normalized pattern and query recorded as memory-searched for the project."""
    if not _persistable(project):
        return set()
    out: Set[str] = set()
    if badger_store is not None:
        try:
            store = badger_store.open_audit(timeout=STORE_TIMEOUT_SECONDS)
            try:
                value = store.kv_get(_STATE_TABLE, _STATE_PREFIX + project, [])
            finally:
                store.close()
            if isinstance(value, list):
                out.update(v for v in value if isinstance(v, str))
        except Exception:  # pylint: disable=broad-exception-caught  # a gate never raises
            pass
    try:
        out.update(line for line in
                   _searched_path(project).read_text(encoding="utf-8").splitlines() if line)
    except OSError:
        pass
    return out


def remember(project: str, entries: Iterable[str], kind: str = PATTERN_KIND) -> bool:
    """Record *entries* of *kind* (PATTERN_KIND or QUERY_KIND) as memory-searched for the
    project; False for `unknown`, nothing to record, or on failure."""
    if not _persistable(project):
        return False
    new = sorted({kind + key for key in (normalize(e) for e in entries if isinstance(e, str))
                  if key})
    if not new:
        return False
    if badger_store is not None:
        try:
            store = badger_store.open_audit(timeout=STORE_TIMEOUT_SECONDS)
            try:
                store.kv_update(
                    _STATE_TABLE, _STATE_PREFIX + project,
                    lambda current: sorted(set(current if isinstance(current, list) else [])
                                           | set(new)),
                    default=[])
                return True
            finally:
                store.close()
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    path = _searched_path(project)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as handle:
            handle.write("".join(entry + "\n" for entry in new))
        return True
    except OSError:
        return False


def _in_query(key: str, query: str) -> bool:
    """True when *key* is a contiguous whole-word run of *query* (both normalized)."""
    return re.search(r"(?<!\w)" + re.escape(key) + r"(?!\w)", query) is not None


def unsearched(project: str, patterns: Iterable[str]) -> List[str]:
    """The patterns not yet memory-searched: neither a recorded pattern exactly nor a
    whole-word run inside a recorded query."""
    known = searched(project)
    queries = [entry[len(QUERY_KIND):] for entry in known if entry.startswith(QUERY_KIND)]
    out: List[str] = []
    seen_keys: Set[str] = set()
    for pattern in patterns:
        key = normalize(pattern)
        if not key or key in seen_keys:
            continue
        seen_keys.add(key)
        if PATTERN_KIND + key not in known and not any(_in_query(key, q) for q in queries):
            out.append(pattern)
    return out


def credit_search(project: str, args: Any, failed: bool = False) -> bool:
    """Record a memory_search's query as searched for *project*; only a call that succeeded
    and named that same projectId counts."""
    if failed or not isinstance(args, dict) or args.get("projectId") != project:
        return False
    return record_query(project, args.get("query"))


def record_query(project: str, query: Any) -> bool:
    """Record an agent memory_search query as searched; False when blank or not persisted."""
    return isinstance(query, str) and remember(project, [query], QUERY_KIND)


# ------------------------------------------------------------------ session telemetry
def _safe_session(session_id: Optional[str]) -> str:
    """Session id made filesystem-safe; the empty string is not a valid marker.

    Keeps [A-Za-z0-9._-]; substitutes '_' for everything else.
    Guards dot-only traversal segments: '.' -> '_' and '..' -> '__'.
    """
    if not session_id:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(session_id)
    )
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def marker_path(session_id: Optional[str]) -> Path:
    """The consulted marker file for a session (empty path when no session id)."""
    safe = _safe_session(session_id)
    return MARKER_DIR / safe if safe else Path("")


def _open_store():
    """The user store narrowed to the memory_first family; MARKER_DIR is its legacy seam."""
    return badger_store.open_user(families={
        "memory_first": badger_store.Family(
            table="memory_first", db="user",
            legacy_path=lambda: MARKER_DIR, legacy_kind="markers",
        ),
    })


def record_search(session_id: Optional[str]) -> bool:
    """Record the session's memory_search as a consulted telemetry row; False on failure.

    The first write lazy-migrates the legacy marker set (D6). An unavailable store falls
    back to touching the legacy marker file.
    """
    safe = _safe_session(session_id)
    if not safe:
        return False
    if badger_store is not None:
        try:
            store = _open_store()
            try:
                store.migrate("memory_first")
                store.conn.execute(
                    "INSERT INTO memory_first(session_id, payload, denials, updated_at) "
                    "VALUES (?, ?, 0, ?) ON CONFLICT(session_id) DO UPDATE SET "
                    "payload = excluded.payload, updated_at = excluded.updated_at",
                    # the shared row-stamp format comes from the store's own helper
                    # pylint: disable-next=protected-access
                    (safe, json.dumps({"consulted": True}), badger_store._now()),
                )
                store.conn.commit()
                return True
            finally:
                store.close()
        # a gate never raises
        except Exception:  # pylint: disable=broad-exception-caught
            pass
    path = marker_path(session_id)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.touch()
        return True
    except OSError:
        return False


# ------------------------------------------------------------------ project id
def _main_checkout_basename(cwd: str) -> Optional[str]:
    """The main checkout's basename for `cwd`, collapsing a linked worktree to it.

    `--git-common-dir` points at the shared `.git`, whose parent is the main checkout.
    None on any failure (not a repo, no git, timeout) or empty `cwd`.
    """
    if not cwd:
        return None
    try:
        result = subprocess.run(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=cwd, capture_output=True, text=True, timeout=_GIT_TIMEOUT_SECONDS, check=False,
            env=git_env(),
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if result.returncode != 0:
        return None
    common_dir = result.stdout.strip()
    return Path(common_dir).parent.name if common_dir else None


def project_id(cwd: str = "") -> str:
    """The bank's project id for a working directory: the memory hook's own resolution.

    badger_store.resolve_project_id reads ``AI_BADGER_PROJECT_ID`` first, else the
    nearest-ancestor ``.ai-badger/project-id`` file. Without the store or an id file, a
    linked worktree collapses to the main checkout's basename, a plain directory to its own,
    else ``unknown``. A store failure falls back rather than propagating.
    """
    if badger_store is not None:
        try:
            resolved = badger_store.resolve_project_id(cwd)
        except Exception:  # pylint: disable=broad-exception-caught  # a gate never raises
            resolved = None
        if resolved and resolved.strip():
            return resolved.strip()
    return _main_checkout_basename(cwd) or Path(cwd or "").name or _UNKNOWN_PROJECT


# ------------------------------------------------------------------ the decision
def memory_hits(query: str, project: str, session_id: Optional[str],
                seconds: float) -> Optional[str]:
    """One memory search within *seconds*: the hits block, `""` for none, None when it could
    not run (no memory_context sibling, no executable, timeout, error)."""
    if not _MEMORY_CONTEXT:
        _MEMORY_CONTEXT.append(_load_beside("memory_context"))
    context = _MEMORY_CONTEXT[0]
    if context is None or seconds <= 0:
        return None
    return context.search_block(query, project, session_id or _GATE_SESSION,
                                context.Budget(seconds))


def search_query(patterns: Iterable[str]) -> Tuple[str, List[str]]:
    """The memory query for a set of unsearched patterns, capped at QUERY_MAX_CHARS, and the
    patterns that made it into it whole (only those may be recorded as searched)."""
    included: List[str] = []
    for pattern in dict.fromkeys(p.strip() for p in patterns):
        if len(" ".join(included + [pattern])) > QUERY_MAX_CHARS:
            break
        included.append(pattern)
    if not included:
        first = next(iter(dict.fromkeys(p.strip() for p in patterns)), "")
        return first[:QUERY_MAX_CHARS], [first] if first else []
    return " ".join(included), included


def deny_reason(project: str, unseen: List[str], hits: Optional[str],
                captures: Optional[List[Tuple[str, str]]] = None) -> str:
    """Why the call is denied, including any raw-result retrieval handles."""
    retry = "re-issue the call."
    handles = captures or []
    raw = (" Raw output can be fetched with `get-raw-grep-result` using "
           + ", ".join(f"pattern-hash={key}" for _pattern, key in handles) + ".") if handles else ""
    if not unseen:
        return ("Memory-first gate: every pattern here was already memory-searched "
                f"(projectId={project}); {retry}{raw}")
    listed = ", ".join(repr(p) for p in unseen)
    if hits is None:
        return (f"Memory-first gate: first sighting of {listed}. Run memory_search "
                f"(projectId={project}) with that pattern as the query, then {retry}{raw}")
    found = hits.strip()[:HITS_MAX_CHARS] or "(no hits)"
    return (f"Memory-first gate: first sighting of {listed}; memory_search "
            f"(projectId={project}) ran for you:\n{found}\nIf that answers it, skip the "
            f"search; otherwise {retry}{raw}")


def decide(host: str, tool_name: Any, tool_input: Any, session_id: Optional[str],
           cwd: str = "") -> Dict[str, Any]:
    """The per-host deny payload for a search call that has not earned its pass, else {}.

    Fails open when the project id cannot be persisted or the searched patterns cannot be
    recorded: a pattern the gate cannot remember would be denied forever.
    """
    started = time.monotonic()
    patterns = search_patterns(tool_name, tool_input)
    if patterns is None:
        return {}
    project = project_id(cwd)
    if not _persistable(project):
        return {}
    unseen = unsearched(project, patterns)
    if not unseen:
        return {}
    captures = capture_greps(tool_name, tool_input, cwd, set(unseen))
    hits = None
    if unseen:
        query, queried = search_query(unseen)
        try:
            hits = memory_hits(query, project, session_id,
                               SEARCH_SECONDS - (time.monotonic() - started))
        except Exception:  # pylint: disable=broad-exception-caught  # a gate never raises
            hits = None
        if hits is not None and queried and not remember(project, queried):
            return {}
    return build_decision(host, tool_name, tool_input, session_id, cwd,
                          reason=deny_reason(project, unseen, hits, captures))


def build_decision(host: str, tool_name: str, tool_input: Any,  # pylint: disable=unused-argument
                   session_id: Optional[str], cwd: str = "",
                   reason: Optional[str] = None) -> Dict[str, Any]:
    """The per-host deny payload, or {} for an unknown host (fail open)."""
    reason = reason or _REASON.format(project_id=project_id(cwd))
    if host == "hermes":
        return {"action": "block", "message": reason}
    if host == "claude":
        return {"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }}
    if host == "copilot":
        return {"permissionDecision": "deny", "permissionDecisionReason": reason}
    return {}
