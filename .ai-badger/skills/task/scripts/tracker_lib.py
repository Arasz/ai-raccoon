"""Shared helpers for the /task skill: JSON stores, transcript token parsing, session refs.

Data lives in <project-root>/.ai-badger/task-tracking/ (gitignored):
  executed-tasks.json  — task execution records (session refs, timestamps, state)
  token-usage.json     — per-task token checkpoints, usage deltas, quality grade
  current-session.json — every currently-active session (keyed by sessionId), so multiple
                          concurrent Claude Code sessions can share the file safely — see
                          resolve_own_session().

Project-agnostic: the project root is resolved via `resolve_project_root()` (env var, then a
cwd walk for the `.ai-badger/config.json` contract marker, then a fallback relative to this
file's own location), and every path is then derived from that root via a project-root-relative
`.ai-badger/` tracking convention, never an absolute path baked in at authoring time. This
matters because ai-badger ships `task` as an installable plugin skill: when Claude Code runs it
from its plugin cache (`~/.claude/plugins/cache/ai-badger/ai-badger/skills/task/scripts/`), the
script's own location is nowhere near the user's project, so a naive fixed-depth-from-`__file__`
lookup would misroot. Anything project-specific (build/test commands, source-control platform,
persona routing) lives in the project's `.ai-badger/config.json`, not here.
"""
# pylint: disable=missing-function-docstring,invalid-name
# Ported verbatim from the originating job-search-ai-assistant repo's /task skill: kept in
# lockstep with that source rather than churned for local docstring/naming style rules.
# `locked_store` (lower_snake_case class) is referenced by that name elsewhere; not renamed.

from __future__ import annotations

import fcntl
import json
import os
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

# Session sources: how the tracker identifies the current session and reads its token
# usage. Every agent registers its own source the same way — an adjustment
# (features/<agent>/adjustments/) installs a `<agent>_session_source.py` module beside this
# file that calls register_session_source() (see features/claude/adjustments/ and
# features/hermes/adjustments/). There is no built-in default: the registry is the only
# thing the common scripts know.
SESSION_SOURCES: dict = {}

SCRIPT_DIR = Path(__file__).resolve().parent


def resolve_project_root(
    env: dict | None = None, cwd: Path | None = None, script_dir: Path = SCRIPT_DIR
) -> Path:
    """Resolve the ai-badger project root, in precedence order:

    1. `CLAUDE_PROJECT_DIR` env var, when set and pointing at an existing directory --
       authoritative for hook/statusLine invocations (Claude Code sets it; ai-badger's own
       scaffolded settings.json hooks already rely on it).
    2. Walk up from `cwd` to the nearest ancestor containing `.ai-badger/config.json` (the
       ai-badger contract marker) -- covers script invocations from anywhere in the repo.
    3. Fallback: `script_dir.parents[3]` -- today's behavior for in-repo scaffolded copies
       (`<repo>/.claude/skills/task/scripts` or `<repo>/.ai-badger/skills/task/scripts`)
       invoked with no session context.

    Deliberately does not walk up from `script_dir` looking for a `.claude/` directory (as
    poll_limit's old `_find_project_root` did): from a Claude Code plugin cache
    (`~/.claude/plugins/cache/ai-badger/ai-badger/skills/task/scripts/`), that walk finds
    `$HOME` -- because `~/.claude` always exists there -- which is both the wrong start point
    and the wrong marker.
    """
    env = os.environ if env is None else env
    env_dir = env.get("CLAUDE_PROJECT_DIR")
    if env_dir and Path(env_dir).is_dir():
        return Path(env_dir)

    start = Path.cwd() if cwd is None else Path(cwd)
    for ancestor in (start, *start.parents):
        if (ancestor / ".ai-badger" / "config.json").is_file():
            return ancestor

    return script_dir.parents[3]  # .claude/skills/task/scripts -> repo root


def compute_paths(project_root: Path) -> dict:
    """Derive every tracker_lib path constant from a resolved project root."""
    data_dir = project_root / ".ai-badger" / "task-tracking"
    return {
        "project_root": project_root,
        "data_dir": data_dir,
        "executed_tasks": data_dir / "executed-tasks.json",
        "token_usage": data_dir / "token-usage.json",
        "current_session": data_dir / "current-session.json",
        "lock_file": data_dir / ".write.lock",
        "claude_md": project_root / "CLAUDE.md",
        "state_json": project_root / ".ai-badger" / "state.json",
        "config_json": project_root / ".ai-badger" / "config.json",
    }


_PATHS = compute_paths(resolve_project_root())
PROJECT_ROOT = _PATHS["project_root"]
DATA_DIR = _PATHS["data_dir"]

EXECUTED_TASKS = _PATHS["executed_tasks"]
TOKEN_USAGE = _PATHS["token_usage"]
CURRENT_SESSION = _PATHS["current_session"]
LOCK_FILE = _PATHS["lock_file"]

STATE_STARTED = "STARTED"
STATE_IN_PROGRESS = "IN_PROGRESS"
STATE_FINISHED = "FINISHED"

CLAUDE_MD = _PATHS["claude_md"]
CLAUDE_MD_MAX_CHARS = 12000
CLAUDE_MD_MAX_LINES = 110

# Every agent's discovery file pays the same context cost, so all of them share the budget.
AGENT_DOC_FILES = ("CLAUDE.md", "HERMES.md", ".hermes.md", "AGENTS.md",
                   ".github/copilot-instructions.md")
STATE_JSON = _PATHS["state_json"]
CONFIG_JSON = _PATHS["config_json"]


def now_iso() -> str:
    # Full microsecond precision: startedAt is compared against file mtimes
    # (state.json freshness), and second-level truncation flips comparisons
    # for events less than a second apart.
    return datetime.now(timezone.utc).isoformat()


def parse_iso(value: str) -> datetime:
    return datetime.fromisoformat(value)


def ensure_data_dir() -> None:
    DATA_DIR.mkdir(parents=True, exist_ok=True)


SPAWN_LOG_ENV = "AI_BADGER_SPAWN_LOG"


def _record_spawn(proc, argv: list, cwd: Path) -> None:
    """Append a breadcrumb naming this spawn, when the suite asked for one (#232).

    `conftest` wraps Popen to reap detached children, which reaches nothing a *child*
    interpreter spawns — a subprocess gets a clean `subprocess` module. Naming the call site
    is what a wrapper cannot do. Unset outside the suite, so this is inert in production, and
    it never raises: a diagnostic that can fail the thing it observes is worse than none.
    """
    destination = os.environ.get(SPAWN_LOG_ENV)
    if not destination:
        return
    try:
        record = json.dumps({
            # str() each element, not list(argv): a Path — which every call site here is one
            # forgotten str() away from — makes json.dumps raise, and the except below would
            # then drop the record silently. A probe that goes quiet on unusual input is worse
            # than one that is absent, because an empty log reads as "nothing spawned".
            "argv": [str(part) for part in argv],
            "cwd": str(cwd),
            # getattr, not proc.pid: a test that stubs Popen may hand back anything, including
            # None, and this is a diagnostic — it does not get to break the spawn it observes.
            "pid": getattr(proc, "pid", None),
            "test": os.environ.get("PYTEST_CURRENT_TEST", ""),
            # The process that did the spawning. When it is the pytest process itself, #222's
            # Popen wrapper already tracked and reaped the child; when it is anything else,
            # the spawn happened in a child interpreter the wrapper cannot reach — which is
            # the case #232 is about, and the only one worth failing a run over.
            "by": os.getpid(),
        })
        with open(destination, "a", encoding="utf-8") as fh:
            fh.write(record + "\n")
    except (OSError, TypeError, ValueError):
        pass


def spawn_detached(argv: list, cwd: Path | None = None, log_path: Path | None = None):
    """Start a background process that deliberately outlives its parent.

    The one place ai-badger detaches a child, so how detaching works is decided once and a
    test has a single seam to patch instead of `subprocess.Popen` at each call site (#222).
    Raises whatever Popen raises; callers decide whether a failed spawn is fatal.
    """
    cwd = PROJECT_ROOT if cwd is None else cwd
    if log_path is None:
        proc = subprocess.Popen(  # pylint: disable=consider-using-with
            argv, cwd=str(cwd), start_new_session=True)
    else:
        with open(log_path, "a", encoding="utf-8") as log_fh:
            proc = subprocess.Popen(  # pylint: disable=consider-using-with
                argv, cwd=str(cwd), stdout=log_fh, stderr=subprocess.STDOUT,
                start_new_session=True)
    _record_spawn(proc, argv, cwd)
    return proc


class locked_store:
    """Context manager: exclusive lock over the tracking data dir for read-modify-write."""

    def __init__(self):
        self._fh = None

    def __enter__(self):
        ensure_data_dir()
        # Marked for the same reason save_json is: this opens LOCK_FILE for writing, so it is
        # the other way a suite process can touch real tracking state.
        _record_real_write(LOCK_FILE)
        self._fh = open(LOCK_FILE, "w", encoding="utf-8")
        fcntl.flock(self._fh, fcntl.LOCK_EX)
        return self

    def __exit__(self, *exc):
        fcntl.flock(self._fh, fcntl.LOCK_UN)
        self._fh.close()
        return False


def load_json(path: Path, default):
    try:
        with open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (FileNotFoundError, json.JSONDecodeError):
        return default


REAL_WRITE_LOG_ENV = "AI_BADGER_REAL_WRITE_LOG"
REAL_ROOT_ENV = "AI_BADGER_REAL_ROOT"


def _is_inside(candidate: Path, parent: Path) -> bool:
    """Whether *candidate* sits under *parent*. `Path.is_relative_to` is 3.9+; the floor is 3.8."""
    try:
        candidate.relative_to(parent)
        return True
    except ValueError:
        return False


def _record_real_write(path: Path) -> None:
    """Mark a tracking write that lands outside the suite's scratch project.

    The tracking-state guard used to infer "the suite wrote this" from an mtime diff, which a
    leftover `poll_limit.py` daemon or a `*/30` resume cron satisfies just as well — it failed
    four clean runs on 2026-08-01. An external writer inherits none of the suite's environment,
    so a write it makes is unmarked, and the guard can report it without blaming the suite.

    Inert when the variable is unset, and never raises: a diagnostic does not get to fail the
    save it observes.
    """
    destination = os.environ.get(REAL_WRITE_LOG_ENV)
    real_root = os.environ.get(REAL_ROOT_ENV)
    if not destination or not real_root:
        return
    try:
        # Against the real checkout, not CLAUDE_PROJECT_DIR: tests monkeypatch that freely, so
        # comparing to it marked writes into the scratch project and into pytest tmpdirs — all
        # of them legitimate. The invariant is about the real repo and nothing else.
        resolved = Path(path).resolve()
        if not _is_inside(resolved, Path(real_root).resolve()):
            return
        record = json.dumps({
            "path": str(resolved),
            "pid": os.getpid(),
            "test": os.environ.get("PYTEST_CURRENT_TEST", ""),
        })
        with open(destination, "a", encoding="utf-8") as fh:
            fh.write(record + "\n")
    except (OSError, TypeError, ValueError):
        pass


def save_json(path: Path, data) -> None:
    _record_real_write(path)
    ensure_data_dir()
    fd, tmp = tempfile.mkstemp(dir=str(path.parent), prefix=path.name, suffix=".tmp")
    try:
        with os.fdopen(fd, "w") as fh:
            json.dump(data, fh, indent=2)
            fh.write("\n")
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def load_tasks() -> dict:
    return load_json(EXECUTED_TASKS, {"tasks": []})


def load_usage() -> dict:
    return load_json(TOKEN_USAGE, {"tasks": []})


def load_config() -> dict:
    """Project profile from `.ai-badger/config.json` (see schemas/config.schema.json).

    Returns {} if the project hasn't scaffolded ai-badger config yet (or it's unreadable) —
    callers must treat every key as optional and fall back to a clear no-op message rather
    than assume a stack-specific default.
    """
    return load_json(CONFIG_JSON, {})


def find_entry(doc: dict, task_id: str):
    for entry in doc["tasks"]:
        if entry.get("taskId") == task_id:
            return entry
    return None


def find_other_entry_with_session(doc: dict, session_id: str, exclude_task_id: str):
    """Another task already attached to session_id, if any (used to catch cross-task collisions)."""
    for entry in doc["tasks"]:
        if entry.get("taskId") != exclude_task_id and entry.get("sessionId") == session_id:
            return entry
    return None


def _pid_alive(pid) -> bool:
    if not pid:
        return False
    try:
        os.kill(int(pid), 0)
    except ProcessLookupError:
        return False
    except (PermissionError, OSError, ValueError):
        return True  # exists but not ours to signal (or malformed input) — assume alive
    return True


def load_current_sessions() -> dict:
    """Every currently-known active session, keyed by sessionId."""
    return load_json(CURRENT_SESSION, {"sessions": {}}).get("sessions", {})


def save_current_session(session_id: str, transcript_path: str, cwd: str = "") -> None:
    """Record this session into the shared multi-session index.

    Lock-protected read-modify-write: multiple Claude Code sessions call this concurrently
    (once per SessionStart/UserPromptSubmit), so it must not race a plain save_json overwrite
    that would drop another session's entry. Also opportunistically prunes entries whose
    process no longer exists, so the file self-cleans without a separate GC job.
    """
    with locked_store():
        doc = load_json(CURRENT_SESSION, {"sessions": {}})
        sessions = doc.setdefault("sessions", {})
        for sid in list(sessions):
            if sid != session_id and not _pid_alive(sessions[sid].get("pid")):
                del sessions[sid]
        sessions[session_id] = {
            "transcriptPath": transcript_path,
            "cwd": cwd or str(PROJECT_ROOT),
            "pid": os.getppid(),  # the long-lived Claude Code process that spawned this hook
            "recordedAt": now_iso(),
        }
        save_json(CURRENT_SESSION, doc)


def _own_pid_ancestry(max_depth: int = 12) -> list[int]:
    """PIDs of this process and its ancestors, nearest first (best-effort via `ps`)."""
    chain = []
    pid = os.getpid()
    for _ in range(max_depth):
        chain.append(pid)
        result = subprocess.run(
            ["ps", "-o", "ppid=", "-p", str(pid)], capture_output=True, text=True, check=False
        )
        ppid_str = result.stdout.strip()
        if not ppid_str:
            break
        try:
            ppid = int(ppid_str)
        except ValueError:
            break
        if ppid <= 1 or ppid == pid:
            break
        pid = ppid
    return chain


def resolve_own_session() -> dict:
    """Ask every registered session source to identify the session invoking this process.

    Each source owns its resolution (env var, hook-recorded sessions, process ancestry —
    however that agent identifies its sessions); the first source that resolves wins, in
    registration order. A source that cannot identify the session returns {} and the next
    is asked. Returns {} when nothing resolves — callers should then require explicit
    --session-id.
    """
    for name, source in SESSION_SOURCES.items():
        resolved = source["resolve"]()
        if resolved.get("sessionId"):
            resolved["source"] = name
            return resolved
    return {}


def parse_transcript_usage(transcript_path: str) -> dict:
    """Aggregate token usage from a Claude Code transcript (JSONL).

    contextTokens  — context-window occupancy of the latest main-chain assistant
                     message (input + cache_read + cache_creation).
    cumulative     — sums over every assistant message in the main transcript *and* in
                     `<dir>/<session-id>/subagents/*.jsonl`, which is where Claude Code
                     writes dispatched work. No record carries `isSidechain: true`, so a
                     branch on it counted nothing (#235 follow-up).
    dispatches     — count, agent types and how many named no model, read from the
                     `agent-<id>.meta.json` beside each subagent transcript.
    """
    cumulative = {
        "inputTokens": 0,
        "outputTokens": 0,
        "cacheReadTokens": 0,
        "cacheCreationTokens": 0,
    }
    by_model: dict = {}
    context_tokens = 0
    messages = 0
    no_dispatches = {"count": 0, "undeclaredModel": 0, "byAgentType": {}}
    path = Path(transcript_path) if transcript_path else None
    if path is None or not path.exists():
        return {
            "contextTokens": 0, "assistantMessages": 0, "byModel": {},
            "dispatches": no_dispatches,
            "cumulative": cumulative, "transcriptFound": False,
        }

    def tally(source: Path, main_chain: bool) -> None:
        """Fold one transcript's assistant messages into the running totals."""
        nonlocal context_tokens, messages
        with open(source, encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if record.get("type") != "assistant":
                    continue
                usage = (record.get("message") or {}).get("usage")
                if not isinstance(usage, dict):
                    continue
                messages += 1
                inp = usage.get("input_tokens") or 0
                out = usage.get("output_tokens") or 0
                cr = usage.get("cache_read_input_tokens") or 0
                cc = usage.get("cache_creation_input_tokens") or 0
                cumulative["inputTokens"] += inp
                cumulative["outputTokens"] += out
                cumulative["cacheReadTokens"] += cr
                cumulative["cacheCreationTokens"] += cc
                # `<synthetic>` and absent both key as unknown, so the split stays a
                # partition of `cumulative` rather than a second opinion.
                model = (record.get("message") or {}).get("model") or "unknown"
                slot = by_model.setdefault(model, {
                    "inputTokens": 0, "outputTokens": 0,
                    "cacheReadTokens": 0, "cacheCreationTokens": 0, "assistantMessages": 0,
                })
                slot["inputTokens"] += inp
                slot["outputTokens"] += out
                slot["cacheReadTokens"] += cr
                slot["cacheCreationTokens"] += cc
                slot["assistantMessages"] += 1
                # Context occupancy is a property of the main conversation; a subagent has
                # its own window and never occupies this one.
                if main_chain:
                    context_tokens = inp + cr + cc

    tally(path, main_chain=True)

    dispatches = {"count": 0, "undeclaredModel": 0, "byAgentType": {}}
    for sub in sorted((path.parent / path.stem / "subagents").glob("*.jsonl")):
        tally(sub, main_chain=False)
        dispatches["count"] += 1
        # meta.json is an undocumented CLI artefact: a format change must degrade to
        # "unknown", never to a count of zero, or it reads as a delegation collapse.
        try:
            meta = json.loads(sub.with_suffix(".meta.json").read_text(encoding="utf-8"))
        except (OSError, ValueError):
            meta = {}
        if not isinstance(meta, dict):
            meta = {}
        agent_type = meta.get("agentType") or "unknown"
        dispatches["byAgentType"][agent_type] = dispatches["byAgentType"].get(agent_type, 0) + 1
        if not meta.get("model"):
            dispatches["undeclaredModel"] += 1

    return {
        "contextTokens": context_tokens,
        "assistantMessages": messages,
        "byModel": by_model,
        "dispatches": dispatches,
        "cumulative": cumulative,
        "transcriptFound": True,
    }


def make_checkpoint(transcript_path: str) -> dict:
    usage = parse_transcript_usage(transcript_path)
    return {
        "timestamp": now_iso(),
        "contextTokens": usage["contextTokens"],
        "assistantMessages": usage["assistantMessages"],
        "byModel": usage["byModel"],
        "cumulative": usage["cumulative"],
    }


def register_session_source(name: str, *, env_var: str, resolve, checkpoint, resume,
                      delegation_usage=None, transcript: bool = False) -> None:
    """Register a session source (called by an agent adjustment's session source module).

    `resolve()` identifies the invoking session for this agent, returning
    {"sessionId", "transcriptPath"} or {}; `checkpoint(session)` returns the checkpoint
    dict for a session; `resume(session_id)` returns the resume command;
    `delegation_usage(delegation_id)` returns a delegation's token record or None;
    `transcript=True` marks the source that reads JSONL transcripts (the one an explicit
    --session-id is attributed to).
    """
    SESSION_SOURCES[name] = {
        "env_var": env_var,
        "resolve": resolve,
        "checkpoint": checkpoint,
        "resume": resume,
        "delegation_usage": delegation_usage,
        "transcript": transcript,
    }


def session_source(name: str):
    """Descriptor for a registered session source; None when no such source is registered."""
    return SESSION_SOURCES.get(name)


def transcript_source():
    """Name of the registered source that reads transcripts, or None.

    An explicit --session-id is a transcript id: the CLI attributes it to this source so the
    right checkpoint maker is chosen. None when nothing transcript-capable is registered.
    """
    for name, source in SESSION_SOURCES.items():
        if source.get("transcript"):
            return name
    return None


def compute_usage(start_cp: dict, finish_cp: dict, subagents: list) -> dict:
    """Delta between two checkpoints of the same session, plus reported subagent tokens."""

    def delta(key: str) -> int:
        return max(0, finish_cp["cumulative"].get(key, 0) - start_cp["cumulative"].get(key, 0))

    subagent_tokens = sum(entry.get("totalTokens", 0) for entry in subagents)
    input_d = delta("inputTokens")
    output_d = delta("outputTokens")
    cache_read_d = delta("cacheReadTokens")
    cache_creation_d = delta("cacheCreationTokens")
    # Main-session cache health: fraction of cacheable input served from cache (~0.1x) vs.
    # freshly written (~1.25x). None when nothing cacheable ran. Main-session only because the
    # deltas above come from checkpoints, whose `cumulative` sums sidechains in; a
    # main-vs-sidechain split is available from parse_transcript_usage (it branches on
    # isSidechain already), but a per-subagent one is not — that needs parentUuid attribution
    # nothing here implements, and the completion notification carries only totalTokens.
    cacheable = cache_read_d + cache_creation_d
    cache_efficiency = round(cache_read_d / cacheable, 3) if cacheable else None
    # Which model did this task's work. `cacheEfficiency` above sits at 0.98 on essentially
    # every real session, so it cannot separate a cheap task from an expensive one; the model
    # mix can, and it is the thing the skill's delegation policy actually steers. No prices
    # here — they change, and output tokens per model is the durable half of the answer.
    output_by_model = {}
    for model, totals in (finish_cp.get("byModel") or {}).items():
        was = ((start_cp.get("byModel") or {}).get(model) or {}).get("outputTokens", 0)
        spent = max(0, totals.get("outputTokens", 0) - was)
        if spent:
            output_by_model[model] = spent
    mix_total = sum(output_by_model.values())
    model_mix = ({model: round(spent / mix_total, 3)
                  for model, spent in output_by_model.items()} if mix_total else {})
    return {
        "outputByModel": output_by_model,
        "modelMix": model_mix,
        "inputTokens": input_d,
        "outputTokens": output_d,
        "cacheReadTokens": cache_read_d,
        "cacheCreationTokens": cache_creation_d,
        "cacheEfficiency": cache_efficiency,
        "subagentTokens": subagent_tokens,
        "contextTokensAtStart": start_cp.get("contextTokens", 0),
        "contextTokensAtFinish": finish_cp.get("contextTokens", 0),
        "contextGrowth": finish_cp.get("contextTokens", 0) - start_cp.get("contextTokens", 0),
        "mainSessionTotal": input_d + output_d + cache_read_d + cache_creation_d,
        # Not `+ subagent_tokens`: the transcript now reads `subagents/*.jsonl` directly, so
        # the checkpoint delta already contains that work. `subagentTokens` stays reported —
        # it is what dispatches said about themselves, useful to compare against.
        "grandTotal": input_d + output_d + cache_read_d + cache_creation_d,
    }


def _positive_int(value, fallback: int) -> int:
    """A budget override is only honoured when it is a positive int. Typos fail safe."""
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        return fallback
    return value


def doc_budget() -> tuple:
    """The (chars, lines) budget for agent discovery files.

    Overridable per project via `agentDocs` in config.json, because the files being measured
    are generated: a project that selects many invariants pays lines it did not author and
    cannot shorten without dropping policy.
    """
    config = load_config()
    override = config.get("agentDocs") if isinstance(config, dict) else None
    if not isinstance(override, dict):
        return CLAUDE_MD_MAX_CHARS, CLAUDE_MD_MAX_LINES
    return (_positive_int(override.get("maxChars"), CLAUDE_MD_MAX_CHARS),
            _positive_int(override.get("maxLines"), CLAUDE_MD_MAX_LINES))


def doc_stats(path, max_chars=None, max_lines=None) -> dict:
    """Size + budget verdict for one agent discovery file.

    `max_chars`/`max_lines`, when given, win over `doc_budget()` — an explicit caller
    (e.g. a CLI flag) must be able to override a project's own `agentDocs` config.
    """
    try:
        text = Path(path).read_text(encoding="utf-8")
    except (FileNotFoundError, IsADirectoryError):
        text = ""
    budget_chars, budget_lines = doc_budget()
    max_chars = budget_chars if max_chars is None else max_chars
    max_lines = budget_lines if max_lines is None else max_lines
    chars = len(text)
    lines = text.count("\n") + (1 if text and not text.endswith("\n") else 0)
    return {
        "path": str(path),
        "chars": chars,
        "lines": lines,
        "maxChars": max_chars,
        "maxLines": max_lines,
        "overBudget": chars > max_chars or lines > max_lines,
    }


def claude_md_stats(max_chars=None, max_lines=None) -> dict:
    return doc_stats(CLAUDE_MD, max_chars, max_lines)


def over_budget_docs() -> list:
    """Every agent discovery file that exists and exceeds the budget.

    The budget is a context-window cost every agent pays, not a Claude-only concern —
    HERMES.md and copilot-instructions.md were unchecked by anything (F-36).
    """
    over = []
    for rel in AGENT_DOC_FILES:
        path = PROJECT_ROOT / rel
        if not path.is_file():
            continue
        stats = doc_stats(path)
        if stats["overBudget"]:
            over.append(stats)
    return over


def state_json_updated_since(started_at: str) -> bool:
    try:
        mtime = datetime.fromtimestamp(STATE_JSON.stat().st_mtime, tz=timezone.utc)
    except FileNotFoundError:
        return False
    return mtime > parse_iso(started_at)


# Optional agent-specific session sources. Agent adjustments (features/<agent>/adjustments/)
# install a `<agent>_session_source.py` module beside this file (e.g. claude_session_source.py,
# hermes_session_source.py); each exposes register() that wires that agent's source into the
# registry above. Discovery by filename pattern rather than a fixed name, so any number of
# agents can coexist — and absent all of them, the tracker has no session source at all. The
# import must never fail the tracker over a module that was never installed.
try:
    import importlib
    for _path in sorted(SCRIPT_DIR.glob("*_session_source.py")):
        importlib.import_module(_path.stem)
        sys.modules[_path.stem].register(sys.modules[__name__])
except ImportError:
    pass
