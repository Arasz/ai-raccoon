#!/usr/bin/env python3
"""Limited mode for machines where policy disables Claude Code hooks.

`checkin` replays the project's own hook commands; `on`/`off` manage a block in CLAUDE.local.md.
"""
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

BEGIN = "<!-- BEGIN ai-badger limited mode -->"
END = "<!-- END ai-badger limited mode -->"
LOCAL_FILE = "CLAUDE.local.md"
TIMEOUT_SECONDS = 60
STDERR_TAIL_LINES = 10

# badger_lib.GIT_LOCATION_ENV, repeated because this ships into projects with no framework
# checkout to import it from; tests/test_git_invocation.py pins every copy against the original.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")

BLOCK_TEXT = """## ai-badger limited mode (hooks disabled on this machine)

Your organisation's policy disables Claude Code hooks here, so ai-badger's automatic context, guards and checkpoints do not run. Do them by hand:

- From the project root, at session start and again before opening a PR or finishing a task, run `python3 .ai-badger/skills/task/scripts/limited_mode.py checkin`; when a turn finishes a task, run it with `--end`. A non-zero exit means the check-in did not happen: read its stderr.
- Prompt markers (`f:`, `q:`, `i!:` ...) are not expanded, not even by `checkin`: apply them yourself per the Prompt markers section.
- Every Agent dispatch names its `model` (see `.ai-badger/delegation.md`); nothing checks it.
- Where the ai-raccoon memory server is configured, search memory (`query`) before repo text search; nothing checks it.
- Never hand-edit files under `.git/` or the generated `.ai-badger/` output: use git commands, and change `.ai-badger/config.json` and re-scaffold instead.
- Before killing processes or clearing shared caches while other agents run, scope the kill to your own lane.
- Commit small and often, run each test suite once, and cite failing output before a fix: the invariants still apply, only their reminders are gone.
- A turn that is interrupted is not checkpointed; run `checkin --end` at the next turn.
"""


class CheckinError(Exception):
    """A check-in that did not happen; carries its exit code."""

    def __init__(self, message, code=1):
        super().__init__(message)
        self.code = code


class MarkerError(Exception):
    """CLAUDE.local.md holds limited-mode markers that do not pair up."""


def git_env(env=None):
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def find_root(explicit):
    """Return the project root: --root, else the git toplevel of cwd, else cwd."""
    if explicit:
        return Path(explicit).resolve()
    try:
        top = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], capture_output=True,
            text=True, check=True, timeout=10, env=git_env()).stdout.strip()
        if top:
            return Path(top)
    except (OSError, subprocess.SubprocessError):
        pass
    print(f"limited-mode: not inside a git checkout; using {Path.cwd()}", file=sys.stderr)
    return Path.cwd()


def main_checkout(explicit):
    """Return --root, else the main checkout every linked worktree nests under.

    Only a common dir named `.git` sits inside its checkout; submodules and separate git dirs
    keep it elsewhere, so they fall back to the current toplevel.
    """
    if explicit:
        return Path(explicit).resolve()
    try:
        common = subprocess.run(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            capture_output=True, text=True, check=True, timeout=10,
            env=git_env()).stdout.strip()
        if common and Path(common).name == ".git":
            return Path(common).parent
    except (OSError, subprocess.SubprocessError):
        pass
    return find_root(None)


def transcript_path(cwd, session_id):
    """Claude's transcript for this session: found by id, else derived from the cwd."""
    projects = Path.home() / ".claude" / "projects"
    found = sorted(projects.glob(f"*/{session_id}.jsonl"))
    if found:
        return str(found[0])
    slug = re.sub(r"[^A-Za-z0-9]", "-", cwd)
    return str(projects / slug / f"{session_id}.jsonl")


def hook_commands(hooks_file, event):
    """Every command registered for the event; matchers are ignored."""
    try:
        spec = json.loads(Path(hooks_file).read_text(encoding="utf-8"))
        entries = spec["hooks"].get(event, [])
        commands = []
        for entry in entries:
            for hook in entry.get("hooks", []):
                if hook.get("type") == "command" and hook.get("command"):
                    commands.append(hook["command"])
    except (OSError, ValueError, KeyError, AttributeError, TypeError) as error:
        raise CheckinError(f"hooks.json malformed: {hooks_file}: {error}", code=2) from error
    return commands


def reply_lines(stdout):
    """Text a hook reply contributes: context, system message, block reason, or the raw reply."""
    text = stdout.strip()
    if not text:
        return []
    try:
        data = json.loads(text)
    except ValueError:
        return [text]
    if not isinstance(data, dict):
        return [text]
    specific = data.get("hookSpecificOutput")
    specific = specific if isinstance(specific, dict) else {}
    found = [specific.get("additionalContext"), data.get("systemMessage")]
    if data.get("reason"):
        prefix = "BLOCK: " if data.get("decision") == "block" else ""
        found.append(prefix + str(data["reason"]))
    bus = specific.get("aiBadgerBus")
    if isinstance(bus, dict) and bus.get("error"):
        found.append(f"message bus error: {json.dumps(bus)}")
    lines = [str(item) for item in found if item]
    if not lines and data and set(data) - {"hookSpecificOutput"}:
        lines.append(text)
    return lines


def run_command(command, payload, cwd, env):
    """Run one hook command; return True when it exited 0 in time."""
    try:
        result = subprocess.run(
            command, shell=True, input=payload, capture_output=True, text=True,
            cwd=cwd, env=env, timeout=TIMEOUT_SECONDS, check=False)
    except subprocess.TimeoutExpired:
        print(f"limited-mode: command timed out after {TIMEOUT_SECONDS}s: {command[:80]}",
              file=sys.stderr)
        return False
    for line in reply_lines(result.stdout):
        print(line)
    if result.returncode == 0:
        return True
    print(f"limited-mode: command exit {result.returncode}: {command[:80]}", file=sys.stderr)
    tail = result.stderr.strip().splitlines()[-STDERR_TAIL_LINES:]
    for line in tail:
        print(f"  {line}", file=sys.stderr)
    return False


def checkin(args):
    """Replay the hook commands for the session start, or the stop with --end."""
    if os.environ.get("CLAUDE_CODE_CHILD_SESSION"):
        print("limited-mode: NOT checked in - a subagent must not check in "
              "(it would consume the parent's mail).", file=sys.stderr)
        return 0
    session_id = args.session or os.environ.get("CLAUDE_CODE_SESSION_ID", "")
    if not session_id:
        raise CheckinError("no session id: pass --session or run inside Claude Code, "
                           "which sets CLAUDE_CODE_SESSION_ID")
    root = find_root(args.root)
    hooks_file = Path(args.hooks) if args.hooks else root / ".ai-badger" / "hooks" / "hooks.json"
    if not hooks_file.is_file():
        raise CheckinError(f"no hooks.json at {hooks_file}; re-scaffold with welcome-ai-badger")
    event = "Stop" if args.end else "SessionStart"
    commands = hook_commands(hooks_file, event)
    if not commands:
        raise CheckinError(f"no {event} hooks registered in {hooks_file}")
    cwd = os.getcwd()
    payload = json.dumps({
        "hook_event_name": event,
        "session_id": session_id,
        "cwd": cwd,
        "transcript_path": transcript_path(cwd, session_id),
        "source": "resume",
    })
    env = dict(os.environ, CLAUDE_PROJECT_DIR=str(root))
    failed = sum(not run_command(command, payload, cwd, env) for command in commands)
    print(f"limited-mode checkin: {len(commands) - failed} of {len(commands)} {event} "
          f"commands succeeded")
    return 1 if failed else 0


def split_block(text):
    """Return (before, after) around the marked block, or None when there is none."""
    begins, ends = text.count(BEGIN), text.count(END)
    if begins == ends == 0:
        return None
    start = text.find(BEGIN)
    stop = text.find(END, start)
    if begins != 1 or ends != 1 or stop == -1:
        raise MarkerError(f"{begins} BEGIN and {ends} END limited-mode marker(s), out of order "
                          f"or unpaired; fix {LOCAL_FILE} by hand")
    return text[:start], text[stop + len(END):]


def turn_on(args):
    """Write the limited-mode block, replacing any earlier one in place."""
    path = main_checkout(args.root) / LOCAL_FILE
    block = f"{BEGIN}\n{BLOCK_TEXT}{END}\n"
    existing = path.read_text(encoding="utf-8") if path.exists() else ""
    parts = split_block(existing)
    if parts:
        before, after = parts
        path.write_text(before + block.rstrip("\n") + after, encoding="utf-8")
    else:
        separator = "\n" if existing and not existing.endswith("\n\n") else ""
        path.write_text(existing + separator + block, encoding="utf-8")
    print(f"limited mode on: {path}")
    return 0


def turn_off(args):
    """Remove the block; delete the file when nothing else is left."""
    path = main_checkout(args.root) / LOCAL_FILE
    if not path.exists():
        return 0
    parts = split_block(path.read_text(encoding="utf-8"))
    if not parts:
        print(f"limited mode already off: no block in {path}")
        return 0
    rest = (parts[0].rstrip("\n") + "\n\n" + parts[1].lstrip("\n")).strip("\n")
    if rest.strip():
        path.write_text(rest + "\n", encoding="utf-8")
    else:
        path.unlink()
    print("limited mode off")
    return 0


def build_parser():
    """Command-line interface."""
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("checkin", help="replay the project's hook commands")
    check.add_argument("--end", action="store_true", help="replay Stop instead of SessionStart")
    check.add_argument("--session", help="session id for the hook payload")
    check.add_argument("--hooks", help="path to hooks.json")
    check.add_argument("--root", help="project root")
    check.set_defaults(run=checkin)
    for name, run, helptext in (("on", turn_on, "write the CLAUDE.local.md block"),
                                ("off", turn_off, "remove the CLAUDE.local.md block")):
        item = sub.add_parser(name, help=helptext)
        item.add_argument("--root", help="project root")
        item.set_defaults(run=run)
    return parser


def main(argv=None):
    """Entry point; a refused check-in or marker problem prints to stderr and exits non-zero."""
    args = build_parser().parse_args(argv)
    try:
        return args.run(args)
    except CheckinError as error:
        print(f"limited-mode: {error}", file=sys.stderr)
        return error.code
    except (MarkerError, OSError, UnicodeDecodeError) as error:
        print(f"limited-mode: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
