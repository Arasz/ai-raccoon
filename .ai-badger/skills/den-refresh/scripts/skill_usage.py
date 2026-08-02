"""Which delivered skills this project was observed using — den-refresh's prune advice (#172).

Observes; never prunes. `.ai-badger/config.json` is project-owned, so the finding is a
recommendation for the human to accept via `exclude.skills`.

Two channels, neither complete alone: Claude Code's transcript store records skill invocations
(any skill, that host only), and the behaviorist audit log records hook activity (only skills
that ship an instrumented hook). Only the invocation channel can support an "unused" claim —
a hook's silence says nothing, and a skill with no hook cannot appear in the log at all.
"""
from __future__ import annotations

import importlib.util
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

# What a bucket entry cites as its evidence.
INVOCATION_EVIDENCE = "invocation"
HOOK_EVIDENCE = "hook"

# The channel names the report prints, so a reader can tell which host answered.
INVOCATION_CHANNEL = "claude-transcripts"
HOOK_CHANNEL = "audit-log"

# Only this framework's own commands and skills count. `superpowers:task` is somebody else's
# skill of the same name, and reading it as evidence would hide an unused one.
PLUGIN_NAME = "ai-badger"

# Never proposed for pruning, whatever the window shows: pruning it removes the ability to
# scaffold, and it runs once per project lifetime by design (#172's open question).
LIFECYCLE_SKILLS = ("welcome-ai-badger",)

# A claim of "never used" needs a window longer than the interval the skill is used at. Below
# this, the report still says what it saw but flags the window as too short to act on.
SHORT_WINDOW_DAYS = 14.0

ENABLE_HINT = ("no usage evidence available — no Claude Code transcripts for this project and "
               "no audit records: enable the audit log with `call-behaviorist`'s "
               "`behaviorist.py on 4h`, work normally, then refresh again")

SKILL_TOOL = "Skill"
SLASH_TOOL = "SlashCommand"
COMMAND_NAME_RE = re.compile(r"<command-name>/([A-Za-z0-9:_-]+)</command-name>")
# Cheap pre-filter: json.loads on 200MB of transcripts costs seconds, `in` costs milliseconds.
INTERESTING = ('"' + SKILL_TOOL + '"', '"' + SLASH_TOOL + '"', "<command-name>")
TIMESTAMP_KEY = '"timestamp"'
MAX_TIMESTAMP_CHARS = 32


def _mangled(path) -> str:
    """Claude Code's transcript-directory spelling of a working directory."""
    return "".join(char if char.isalnum() else "-" for char in str(path))


def transcript_store() -> Path:
    """Where Claude Code keeps session transcripts on this host."""
    return Path.home() / ".claude" / "projects"


def _under(path: str, project) -> bool:
    """True when `path` is `project` or a directory inside it (a worktree, typically)."""
    try:
        resolved = Path(path).resolve()
        root = Path(project).resolve()
    except (OSError, ValueError):
        return False
    return resolved == root or root in resolved.parents


def _owns(transcript: Path, project) -> bool:
    """True when a transcript's own `cwd` places it in this project.

    The directory name is a mangled path, so a sibling project can share its prefix; the
    records say where the session actually ran, and that is what decides.
    """
    try:
        with transcript.open("r", encoding="utf-8", errors="ignore") as handle:
            for _ in range(20):
                line = handle.readline()
                if not line:
                    return False
                try:
                    cwd = json.loads(line).get("cwd")
                except ValueError:
                    continue
                if cwd:
                    return _under(cwd, project)
    except OSError:
        return False
    return False


def project_transcripts(project, store=None) -> List[Path]:
    """Every transcript this project's own sessions wrote, worktrees included."""
    base = Path(store) if store else transcript_store()
    if not base.is_dir():
        return []
    found = []
    for directory in sorted(base.glob(_mangled(project) + "*")):
        if not directory.is_dir():
            continue
        found.extend(path for path in sorted(directory.glob("*.jsonl"))
                     if _owns(path, project))
    return found


def _invoked_names(record: Dict[str, Any]) -> List[str]:
    """The skill names one transcript record invokes, plugin prefixes already resolved."""
    message = record.get("message")
    content = message.get("content") if isinstance(message, dict) else None
    names = []
    for block in content if isinstance(content, list) else []:
        if not isinstance(block, dict) or block.get("type") != "tool_use":
            continue
        payload = block.get("input") or {}
        if block.get("name") == SKILL_TOOL:
            names.append(str(payload.get("skill") or ""))
        elif block.get("name") == SLASH_TOOL:
            command = str(payload.get("command") or "").lstrip("/").split(" ", maxsplit=1)[0]
            names.append(command)
    text = content if isinstance(content, str) else json.dumps(content, ensure_ascii=False)
    names.extend(COMMAND_NAME_RE.findall(text))
    return [bare for bare in (_bare(name) for name in names if name) if bare]


def _bare(name: str) -> str:
    """`ai-badger:task` -> `task`; another plugin's `x:task` -> "" (not this framework's)."""
    if ":" not in name:
        return name
    plugin, _, skill = name.partition(":")
    return skill if plugin == PLUGIN_NAME else ""


def _stamp(line: str) -> str:
    """A record's timestamp, read without parsing the line — most lines are large."""
    at = line.find(TIMESTAMP_KEY)
    if at < 0:
        return ""
    opening = line.find('"', at + len(TIMESTAMP_KEY))
    closing = line.find('"', opening + 1) if opening > 0 else -1
    if closing < 0 or closing - opening > MAX_TIMESTAMP_CHARS:
        return ""
    return line[opening + 1:closing]


def _days(first: str, last: str) -> Optional[float]:
    """Whole-day span between two record timestamps, or None when either cannot be read."""
    moments = []
    for stamp in (first, last):
        text = stamp[:-1] + "+00:00" if stamp.endswith("Z") else stamp
        try:
            parsed = datetime.fromisoformat(text)
        except (TypeError, ValueError):
            return None
        moments.append(parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc))
    return round((moments[1] - moments[0]).total_seconds() / 86400, 1)


def scan_invocations(project, store=None) -> Dict[str, Any]:
    """What this project's transcripts say about skill invocation.

    {"available": bool, "transcripts": int, "from": ts, "to": ts,
     "skills": {name: {"count": int, "last": ts}}}
    """
    transcripts = project_transcripts(project, store)
    skills: Dict[str, Dict[str, Any]] = {}
    stamps: List[str] = []
    for transcript in transcripts:
        try:
            handle = transcript.open("r", encoding="utf-8", errors="ignore")
        except OSError:
            continue
        with handle:
            for line in handle:
                stamp = _stamp(line)
                if stamp:
                    stamps.append(stamp)
                if not any(marker in line for marker in INTERESTING):
                    continue
                try:
                    record = json.loads(line)
                except ValueError:
                    continue
                for name in _invoked_names(record):
                    entry = skills.setdefault(name, {"count": 0, "last": None})
                    entry["count"] += 1
                    if stamp and (entry["last"] is None or stamp > entry["last"]):
                        entry["last"] = stamp
    return {
        "available": bool(transcripts),
        "transcripts": len(transcripts),
        "from": min(stamps) if stamps else None,
        "to": max(stamps) if stamps else None,
        "skills": skills,
    }


def load_behaviorist():
    """The call-behaviorist module beside this skill, or None when the project declined it."""
    path = (Path(__file__).resolve().parent.parent.parent
            / "call-behaviorist" / "scripts" / "behaviorist.py")
    if not path.is_file():
        return None
    try:
        spec = importlib.util.spec_from_file_location("aib_behaviorist", path)
        module = importlib.util.module_from_spec(spec)
        sys.modules["aib_behaviorist"] = module
        spec.loader.exec_module(module)
        return module
    except Exception:  # pylint: disable=broad-except
        return None


NO_HOOK_ACTIVITY = {"records": 0, "skills": {}}


def hook_activity(project) -> Dict[str, Any]:
    """What the audit log says about this project's hook-shipping skills; empty when it cannot."""
    behaviorist = load_behaviorist()
    if behaviorist is None:
        return dict(NO_HOOK_ACTIVITY)
    try:
        return behaviorist.hook_activity(str(project))
    except Exception:  # pylint: disable=broad-except
        return dict(NO_HOOK_ACTIVITY)


def _used(skill, evidence, count, last=None, detail=None) -> Dict[str, Any]:
    entry = {"skill": skill, "evidence": evidence, "count": count}
    if last:
        entry["last"] = last
    if detail:
        entry["detail"] = detail
    return entry


def _classify(skill, invocation, hooks) -> Dict[str, Any]:
    """Which bucket one delivered skill belongs in, and why.

    Order matters: any invocation is the strongest evidence, hook records are next (the skill
    is doing work here even if nobody typed its name), and everything after that is a reason
    the question could not be answered.
    """
    invoked = invocation["skills"].get(skill)
    if invoked:
        return {"bucket": "used", "entry": _used(skill, INVOCATION_EVIDENCE, invoked["count"],
                                                 invoked["last"])}
    hook = hooks["skills"].get(skill) or {}
    if hook.get("records"):
        return {"bucket": "used",
                "entry": _used(skill, HOOK_EVIDENCE, hook["records"],
                               detail="nobody invoked it, but its hook ran — it is doing work "
                                      "here every session, so pruning it changes behaviour")}
    if skill in LIFECYCLE_SKILLS:
        return {"bucket": "cannotTell", "entry": {
            "skill": skill,
            "reason": "runs once per project lifetime, by design — a window that does not cover "
                      "the initial scaffold cannot observe it, and pruning it removes the "
                      "ability to scaffold"}}
    if hook and not hook.get("instrumented"):
        return {"bucket": "cannotTell", "entry": {
            "skill": skill,
            "reason": "wires a hook that calls no debug logger, so its work cannot be observed "
                      "— its silence says nothing"}}
    if not invocation["available"]:
        return {"bucket": "cannotTell", "entry": {
            "skill": skill,
            "reason": "no invocation channel on this host: nothing here records skill calls, "
                      "and no hook of its own left records"}}
    return {"bucket": "unused", "entry": {
        "skill": skill,
        "reason": "delivered, and not invoked once in the observed window"}}


def _limits(invocation, hooks) -> List[str]:
    """What the report cannot know, stated with the finding rather than left to be assumed."""
    limits = []
    if invocation["available"]:
        limits.append(
            f"the invocation channel reads Claude Code transcripts only "
            f"({invocation['transcripts']} retained for this project) — work done through "
            f"another agent is invisible to it")
        span = _days(invocation["from"], invocation["to"]) if invocation["to"] else None
        if span is not None and span < SHORT_WINDOW_DAYS:
            limits.append(f"the window is {span} day(s) — a skill used less often than that can "
                          f"be absent from it without being unused")
    else:
        limits.append("no Claude Code transcripts for this project, so no invocation was "
                      "observable — nothing here is reported as unused")
    limits.append("hook records exist only for skills that ship an instrumented hook, so a "
                  "hookless skill's silence in the audit log is not evidence" if hooks["records"]
                  else "the audit log holds no records for this project, so hook activity could "
                       "not confirm a skill that ships one")
    return limits


def report(project, delivered, store=None, hooks=None) -> Optional[Dict[str, Any]]:
    """den-refresh's `skillUsage` section, or None when the project delivered no skills."""
    names = sorted(set(delivered or []))
    if not names:
        return None
    invocation = scan_invocations(project, store)
    hooks = hook_activity(project) if hooks is None else hooks
    section: Dict[str, Any] = {
        "channels": {
            "invocation": INVOCATION_CHANNEL if invocation["available"] else None,
            "hooks": HOOK_CHANNEL if hooks["records"] else None,
        },
        "window": {
            "from": invocation["from"],
            "to": invocation["to"],
            "days": _days(invocation["from"], invocation["to"]) if invocation["to"] else None,
            "transcripts": invocation["transcripts"],
            "auditRecords": hooks["records"],
        },
        "used": [], "unused": [], "cannotTell": [],
        "limits": _limits(invocation, hooks),
    }
    if not invocation["available"] and not hooks["records"]:
        section["hint"] = ENABLE_HINT
        return section
    for skill in names:
        placed = _classify(skill, invocation, hooks)
        section[placed["bucket"]].append(placed["entry"])
    return section
