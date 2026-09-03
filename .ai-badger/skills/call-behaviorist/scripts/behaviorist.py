#!/usr/bin/env python3
"""call-behaviorist CLI: switch ai-badger's own debug audit log on and off.

  behaviorist.py on [DURATION] [--project]   enable (user-wide by default)
  behaviorist.py off                          disable
  behaviorist.py status                       mode, scope, remaining, log size
  behaviorist.py tail [N]                     last N records
  behaviorist.py clear                        truncate the log

Debug is user-scoped by default: every project logs. `--project` narrows it to the current
working directory.
"""
from __future__ import annotations
# pylint: disable=no-member  # debug_log is an exec-populated shim; pylint cannot see its members

import argparse
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
try:
    import debug_log as dl  # pylint: disable=import-error
except ImportError:  # pragma: no cover - vendored copy is missing
    print("call-behaviorist: debug_log.py not found beside this script", file=sys.stderr)
    raise

DEFAULT_DURATION = "4h"
DURATION_RE = re.compile(r"^(?:(\d+)h)?(?:(\d+)m)?$")
MAX_DURATION_SECONDS = 24 * 3600

# Durations that mean "until switched off by hand".
FOREVER_WORDS = frozenset({"forever", "never", "always"})

# The component this tool logs its own lifecycle under. Those records are bookkeeping,
# never evidence about a hook.
TOOL_COMPONENT = "call-behaviorist"


def parse_duration(text: str):
    """'4h', '90m', '1h30m', or a bare number of hours -> seconds.

    'forever' / 'never' -> None, meaning no expiry: logging stays on until `off`.
    The MAX_AUDIT_LINES cap still bounds the log, so this costs disk but does not grow it.
    """
    text = text.strip().lower()
    if text in FOREVER_WORDS:
        return None
    if text.isdigit():
        seconds = int(text) * 3600
    else:
        match = DURATION_RE.match(text)
        if not match or (match.group(1) is None and match.group(2) is None):
            raise ValueError(f"cannot parse duration {text!r} (use 4h, 90m, 1h30m)")
        seconds = int(match.group(1) or 0) * 3600 + int(match.group(2) or 0) * 60
    if seconds <= 0:
        raise ValueError("duration must be positive")
    return min(seconds, MAX_DURATION_SECONDS)


def cmd_on(duration: str, project_scoped: bool) -> int:
    seconds = parse_duration(duration)
    expires = None if seconds is None else dl.now() + timedelta(seconds=seconds)
    state = {
        "enabled": True,
        "scope": dl.SCOPE_PROJECT if project_scoped else dl.SCOPE_USER,
        "project": str(Path.cwd()) if project_scoped else None,
        "enabled_at": dl.iso(dl.now()),
        "expires_at": None if expires is None else dl.iso(expires),
    }
    dl.set_state(state)
    where = state["project"] if project_scoped else "every project"
    expiry = state["expires_at"] or "never — switch it off with `behaviorist.py off`"
    print(f"debug logging ON for {where}, expires {expiry}")
    print(f"records: {_records_sink()}")
    dl.log_event(TOOL_COMPONENT, "enabled", project=state["project"], scope=state["scope"])
    return 0


def cmd_off() -> int:
    if dl._state() is None:  # pylint: disable=protected-access
        print("debug logging is already off.")
        return 0
    dl.log_event(TOOL_COMPONENT, "disabled")
    dl.set_state({"enabled": False})
    print("debug logging OFF.")
    return 0


def _records_sink():
    """Where records live now: the store's audit DB, or the legacy jsonl beside DEBUG_DIR."""
    # pylint: disable=protected-access
    return dl.audit_db() if dl._store() is not None else dl.AUDIT_FILE


def cmd_status() -> int:
    state = dl._state()  # pylint: disable=protected-access
    if state is None:
        print("debug logging: OFF")
    else:
        scope = state.get("scope", dl.SCOPE_USER)
        target = state.get("project") or "every project"
        print(f"debug logging: ON ({scope} — {target})")
        print(f"expires: {state.get('expires_at') or 'never'}")
    print(f"records: {dl.count_records()} in {_records_sink()}")
    return 0


def cmd_tail(count: int) -> int:
    records = dl.read_records(limit=count)
    if not records:
        print("no records yet.")
        return 0
    for rec in records:
        fixed = (dl.KEY_TS, dl.KEY_COMPONENT, dl.KEY_EVENT, dl.KEY_VERSION)
        extra = " ".join(f"{dl.KEY_NAMES.get(k, k)}={v}" for k, v in rec.items()
                         if k not in fixed)
        print(f"{rec[dl.KEY_TS]}  {rec[dl.KEY_COMPONENT]:<40} {rec[dl.KEY_EVENT]:<8} "
              f"v{rec[dl.KEY_VERSION]}  {extra}")
    return 0


ENABLE_HINT = ("nothing observed yet — run `behaviorist.py on 4h`, work normally, "
               "then analyze again")


def _read_records():
    """Every parseable record in the log, from every project."""
    return dl.read_records()


def _resolved(path):
    """`path` with symlinks collapsed, or the text itself when it cannot be resolved.

    `$CLAUDE_PROJECT_DIR` and `Path.cwd()` can spell one directory two ways (/var and
    /private/var on macOS); comparing only the text drops a project's own records.
    """
    try:
        return str(Path(path).resolve())
    except (OSError, ValueError):
        return path


def _for_project(records, project):
    """Records belonging to `project`, how many named none, and which components those were.

    A record that names no project cannot be shown to belong here. Admitting it into every
    project's analysis is how one repo's hooks become another's `unexpected_component` and how
    a third repo's version lands in this one's skew. Set aside and counted, never dropped.
    """
    target = _resolved(project)
    mine, orphaned, unattributed = [], set(), 0
    for rec in records:
        origin = rec.get(dl.KEY_PROJECT)
        if origin is None:
            unattributed += 1
            orphaned.add(rec.get(dl.KEY_COMPONENT) or "?")
        elif origin == project or _resolved(origin) == target:
            mine.append(rec)
    return mine, unattributed, sorted(orphaned)


def _evidence(records) -> list:
    """Records that say something about a hook.

    The tool's own `enabled`/`disabled`/`cleared` lines are bookkeeping: they prove the log
    exists, never that anything was observed.
    """
    return [r for r in records if r.get(dl.KEY_COMPONENT) != TOOL_COMPONENT]


SCRIPT_RE = re.compile(r'([^\s"\']+/)?([A-Za-z0-9_\-]+)\.py')
PROJECT_DIR_VARS = ("${CLAUDE_PROJECT_DIR}", "$CLAUDE_PROJECT_DIR")

# Read in order; a script registered in more than one of them is still one component.
# The first two are what Claude Code actually runs. The third is ai-badger's own declaration
# and the only project-level record where hooks register elsewhere (Hermes, Copilot).
HOOK_SOURCES = (
    ".claude/settings.json",
    ".claude/settings.local.json",
    ".ai-badger/hooks/hooks.json",
)


def _script_path(project, command):
    """(project-relative name, absolute path) for the script a hook command runs, or None."""
    match = SCRIPT_RE.search(command)
    if not match:
        return None
    raw = (match.group(1) or "") + match.group(2) + ".py"
    for var in PROJECT_DIR_VARS:
        raw = raw.replace(var, str(project))
    path = Path(raw)
    if not path.is_absolute():
        path = Path(project) / path
    try:
        name = path.resolve().relative_to(Path(project).resolve()).as_posix()
    except (OSError, ValueError):
        name = raw
    return name, path


def _wired_hooks(project) -> dict:
    """project-relative script path -> absolute path, for every registered hook.

    Keyed by path, not filename: two skills can ship the same hook filename, and collapsing
    them hides one's silence behind the other's excuse.
    """
    found = {}
    for source in HOOK_SOURCES:
        try:
            data = json.loads((Path(project) / source).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        for entries in (data.get("hooks") or {}).values():
            for entry in entries or []:
                for hook in entry.get("hooks") or []:
                    script = _script_path(project, hook.get("command", ""))
                    if script:
                        found.setdefault(script[0], script[1])
    return found


def expected_components(project) -> list:
    """Components a registered project should produce records for.

    Derived from what is actually registered. A component absent from here is not judged; one
    present here and silent is the finding this tool exists for.
    """
    return sorted(_wired_hooks(project))


def is_instrumented(script_path) -> bool:
    """True when the script actually calls the debug logger.

    A wired hook with no logging call *cannot* produce records, so its silence says nothing
    about health. Reporting that as a failure would make the whole report untrustworthy.
    """
    try:
        return "debug_log" in Path(script_path).read_text(encoding="utf-8")
    except OSError:
        return False


DECLARED_COMPONENT = re.compile(r"""^COMPONENT\s*=\s*["']([^"']+)["']""", re.MULTILINE)


def _component_stem(expected: str) -> str:
    """The name a wired script logs under: its filename stem, or the name as given."""
    return expected[:-len(".py")].rsplit("/", 1)[-1] if expected.endswith(".py") else expected


def declared_component(script_path) -> str:
    """The name the script logs under, read from its `COMPONENT = "..."`, or empty.

    A filename is not an identity: `prompt-markers/…/user_prompt_hook.py` logs as
    `prompt_markers_hook`, and matching on the stem reports a live hook as never observed.
    """
    try:
        found = DECLARED_COMPONENT.search(Path(script_path).read_text(encoding="utf-8"))
    except OSError:
        return ""
    return found.group(1) if found else ""


def _component_key(expected: str, script_path=None) -> str:
    """What `expected` is expected to log under — its declared name, else its filename stem."""
    return (declared_component(script_path) if script_path else "") or _component_stem(expected)


def _matches(key: str, component: str) -> bool:
    """True when `component` is `key`, or `key` qualified by a phase.

    Observed names may be phase-qualified (`session_start_hook/drift`). Matching is on the
    whole key, not a prefix fragment, so `hooks/alpha` never satisfies `hooks/never`.
    """
    return component == key or component.startswith(key + "/")


def _moment(stamp):
    """A record timestamp as a comparable UTC datetime, or None when it cannot be read."""
    if not stamp:
        return None
    text = stamp[:-1] + "+00:00" if stamp.endswith("Z") else stamp
    try:
        parsed = datetime.fromisoformat(text)
    except (TypeError, ValueError):
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=timezone.utc)


def _widen(ranges, version, stamp) -> None:
    """Widen `version`'s observed [from, to] to cover one more record."""
    span = ranges.setdefault(version, {"from": None, "to": None, "records": 0})
    span["records"] += 1
    moment = _moment(stamp)
    if moment is None:
        return
    if span["from"] is None or moment < _moment(span["from"]):
        span["from"] = stamp
    if span["to"] is None or moment > _moment(span["to"]):
        span["to"] = stamp


def _observed(records) -> dict:
    """component -> {count, events, versions, version_ranges}."""
    seen = {}
    for rec in records:
        name = rec.get(dl.KEY_COMPONENT)
        if not name:
            continue
        entry = seen.setdefault(
            name, {"count": 0, "events": {}, "versions": [], "version_ranges": {}})
        entry["count"] += 1
        event = rec.get(dl.KEY_EVENT, "?")
        entry["events"][event] = entry["events"].get(event, 0) + 1
        version = rec.get(dl.KEY_VERSION)
        if version:
            if version not in entry["versions"]:
                entry["versions"].append(version)
            _widen(entry["version_ranges"], version, rec.get(dl.KEY_TS))
    return seen


def _finding(kind, component, severity, detail, **extra):
    finding = {"kind": kind, "component": component, "severity": severity, "detail": detail}
    finding.update(extra)
    return finding


# Context, not a fault: reported so the reader sees it, never counted against health.
SEVERITY_INFO = "info"


def _overlaps(one, other) -> bool:
    """True when two observed spans share any instant, endpoints included.

    A span whose timestamps cannot be read cannot be shown to be disjoint, so it counts as
    overlapping: dropping the finding on unreadable evidence would hide the real instance.
    """
    bounds = (_moment(one["from"]), _moment(one["to"]),
              _moment(other["from"]), _moment(other["to"]))
    if any(bound is None for bound in bounds):
        return True
    a_lo, a_hi, b_lo, b_hi = bounds
    return a_lo <= b_hi and b_lo <= a_hi


def _concurrent(ranges) -> list:
    """The versions whose spans overlap another's — empty when every version ran in turn."""
    names = sorted(ranges)
    competing = set()
    for index, one in enumerate(names):
        for other in names[index + 1:]:
            if _overlaps(ranges[one], ranges[other]):
                competing.update((one, other))
    return sorted(competing)


def _span(version, ranges) -> str:
    """`0.36.0 [first → last]`, with `?` where a timestamp could not be read."""
    span = ranges[version]
    return f"{version} [{span['from'] or '?'} → {span['to'] or '?'}]"


def _version_findings(component, ranges) -> list:
    """What the versions behind a component's records say: an upgrade, a clash, or a gap.

    Disjoint spans are an upgrade; overlapping spans are two copies live at once. The
    `unknown` sentinel means "no VERSION found above the code that ran" — an absent number,
    not a second one, so counting it as a version manufactures skew.
    """
    findings = []
    real = {v: span for v, span in ranges.items() if v != dl.VERSION_UNKNOWN}
    competing = _concurrent(real)
    if competing:
        findings.append(_finding(
            "version_skew", component, "high",
            "two copies were live at once — their observed ranges overlap: "
            + "; ".join(_span(v, real) for v in competing),
            versions=competing, ranges={v: real[v] for v in competing}))
    elif len(real) > 1:
        in_order = sorted(real, key=lambda v: (real[v]["from"] or "", v))
        findings.append(_finding(
            "version_progression", component, SEVERITY_INFO,
            "upgraded through " + " → ".join(in_order)
            + f" between {real[in_order[0]]['from']} and {real[in_order[-1]]['to']}"
            " — the ranges are disjoint, so this is a release train, not copies disagreeing",
            versions=in_order, ranges=real))
    unresolved = ranges.get(dl.VERSION_UNKNOWN)
    if unresolved:
        findings.append(_finding(
            "version_unresolvable", component, "low",
            f"{unresolved['records']} record(s) could not name a version — the copy that ran "
            "carries no VERSION and no manifest above it, so it predates 0.35.4 and needs "
            "re-scaffolding",
            records=unresolved["records"], range=unresolved))
    return findings


SKILLS_DIR_SEGMENT = "skills"


def _skill_of(script: str) -> str:
    """The skill a wired hook belongs to: the directory under `skills/` in its path, or empty.

    Both deployment shapes spell it the same way — `.ai-badger/skills/<skill>/scripts/…` in a
    project, `features/common/skills/<skill>/scripts/…` from a plugin root.
    """
    parts = script.split("/")
    for index, part in enumerate(parts[:-2]):
        if part == SKILLS_DIR_SEGMENT:
            return parts[index + 1]
    return ""


def hook_activity(project) -> dict:
    """What the log can say about this project's *skills*, for a reader deciding what to prune.

    {"records": <records attributed to this project>,
     "skills": {skill: {"hooks": [...], "instrumented": bool, "records": int}}} — hook-shipping
    skills only. Records prove a skill is doing work here; silence proves nothing, since a
    skill that wires no hook can never appear in the log at all.
    """
    wired = _wired_hooks(project)
    records, _, _ = _for_project(_evidence(_read_records()), project)
    observed = _observed(records)
    skills: dict = {}
    for script, path in sorted(wired.items()):
        skill = _skill_of(script)
        if not skill:
            continue
        entry = skills.setdefault(skill, {"hooks": [], "instrumented": False, "records": 0})
        entry["hooks"].append(script)
        entry["instrumented"] = entry["instrumented"] or is_instrumented(path)
        key = _component_key(script, path)
        entry["records"] += sum(seen["count"] for name, seen in observed.items()
                                if _matches(key, name))
    return {"records": len(records), "skills": skills}


def analyze(project, expected=None) -> dict:
    """Compare what a project should do against what it was observed doing."""
    wired = _wired_hooks(project) if expected is None else {}
    expected = list(expected if expected is not None else sorted(wired))
    records, unattributed, orphaned = _for_project(_evidence(_read_records()), project)
    observed = _observed(records)
    keys = {name: _component_key(name, wired.get(name)) for name in expected}
    findings = []
    for name in expected:
        if any(_matches(keys[name], seen) for seen in observed):
            continue
        script = wired.get(name)
        if script and not is_instrumented(script):
            findings.append(_finding(
                "not_instrumented", name, "low",
                "wired but calls no debug logger, so it cannot produce records — "
                "its silence says nothing about health",
            ))
        elif records:
            # Vacuous without evidence: with nothing observed, everything is trivially silent.
            findings.append(_finding(
                "never_observed", name, "high",
                "wired and instrumented, but produced no record — "
                "it may never load or never fire",
            ))
    for name, entry in sorted(observed.items()):
        if expected and not any(_matches(key, name) for key in keys.values()):
            findings.append(_finding(
                "unexpected_component", name, "low",
                "produced records but is not among the components this project wires",
            ))
        findings.extend(_version_findings(name, entry["version_ranges"]))
        starts = entry["events"].get("start", 0)
        skips = entry["events"].get("skip", 0)
        if starts and skips >= starts:
            findings.append(_finding(
                "always_skipped", name, "medium",
                f"fired {starts} time(s) and exited early every time — live but doing nothing",
                starts=starts, skips=skips,
            ))

    if not records:
        health = "unknown"
    elif any(f["severity"] == "high" for f in findings):
        health = "degraded"
    elif any(f["severity"] != SEVERITY_INFO for f in findings):
        health = "warn"
    else:
        health = "ok"

    stamps = [r.get(dl.KEY_TS) for r in records if r.get(dl.KEY_TS)]
    return {
        "project": project,
        "health": health,
        "window": {
            "records": len(records),
            "unattributed": unattributed,
            "unattributed_components": orphaned,
            "from": min(stamps) if stamps else None,
            "to": max(stamps) if stamps else None,
        },
        "expected": expected,
        "observed": observed,
        "findings": findings,
        "hint": ENABLE_HINT if not records else "",
    }


def cmd_analyze(project, as_json: bool) -> int:
    report = analyze(project or str(Path.cwd()))
    if as_json:
        print(json.dumps(report, indent=2))
        return 0
    print(f"ai-badger health: {report['health'].upper()}  ({report['window']['records']} "
          f"record(s) for {report['project']})")
    if report["hint"]:
        print(report["hint"])
    if report["window"]["unattributed"]:
        print(f"  {report['window']['unattributed']} record(s) named no project and were set "
              f"aside: {', '.join(report['window']['unattributed_components'])}")
    for finding in report["findings"]:
        print(f"  [{finding['severity']}] {finding['kind']}: {finding['component']}")
        print(f"      {finding['detail']}")
    if not report["findings"] and report["window"]["records"]:
        print("  no findings — every wired component produced records")
    return 0


def cmd_clear() -> int:
    dl.clear_records()
    dl.log_event(TOOL_COMPONENT, "cleared")
    print("audit log cleared.")
    return 0


def main(argv=None) -> int:
    """CLI entry point."""
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command")

    on = sub.add_parser("on", help="enable debug logging")
    on.add_argument("duration", nargs="?", default=DEFAULT_DURATION)
    on.add_argument("--project", action="store_true",
                    help="limit to the current directory (default: every project)")
    sub.add_parser("off", help="disable debug logging")
    sub.add_parser("status", help="show mode, scope and record count")
    tail = sub.add_parser("tail", help="show the last records")
    tail.add_argument("count", nargs="?", type=int, default=20)
    sub.add_parser("clear", help="truncate the audit log")
    analyse = sub.add_parser("analyze", help="health state and findings for a project")
    analyse.add_argument("--project", default=None)
    analyse.add_argument("--json", action="store_true", dest="as_json",
                         help="machine-readable report, for an agent to turn into a report")

    args = parser.parse_args(argv)
    if args.command == "on":
        try:
            return cmd_on(args.duration, args.project)
        except ValueError as err:
            print(f"error: {err}", file=sys.stderr)
            return 1
    if args.command == "off":
        return cmd_off()
    if args.command == "tail":
        return cmd_tail(args.count)
    if args.command == "clear":
        return cmd_clear()
    if args.command == "analyze":
        return cmd_analyze(args.project, args.as_json)
    return cmd_status()


if __name__ == "__main__":
    raise SystemExit(main())
