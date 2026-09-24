#!/usr/bin/env python3
"""PreToolUse hook: git's own storage is written by git, never by hand.

The incident: a `.git/config` was truncated to 296 bytes by an agent editing it by hand, losing
`remote.origin.fetch` and the `[branch]` tracking sections. Measured: with no fetch refspec,
`git fetch origin` prints `* branch HEAD -> FETCH_HEAD` and `refs/remotes/origin/*` silently
stops moving — fetch still reports success. That silence is why this is refused at edit time
rather than detected later. `git config` / `git remote` / `git config --unset` are the repair
route: git rewrites those files atomically and completely.

`AI_BADGER_ALLOW_GIT_DIR_EDITS=1` is a human-only escape hatch because it is read from THIS
process's environment. The harness spawns the hook; an agent's inline `VAR=1 <cmd>` prefix sets
the variable in the tool's own process, which is a different process that this one never sees.
Only a human exporting it in the session's environment can disarm the gate.

Fails open on the Claude arm — an exception or a dead hook never blocks the tool. On the
Hermes arm this module also fails open per call, but Hermes itself fails CLOSED on a
pre_tool_call timeout, so a slow scan (an oversized payload) would block the tool: the
MAX_COMMAND bound on every Hermes path exists for that reason, not only for lexer cost.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

EDIT_TOOLS = ("Edit", "Write", "MultiEdit", "NotebookEdit")
BASH_TOOLS = ("bash", "terminal")
OVERRIDE_ENV = "AI_BADGER_ALLOW_GIT_DIR_EDITS"

# A git dir is recognisable by what git puts in it, whatever it is called.
GIT_DIR_MARKERS = ("HEAD", "objects", "refs")
# The structural probe stats the filesystem, so the walk is bounded rather than unbounded.
# The bound is also a correctness floor: a bare repo's `refs/remotes/origin/a/b/c/d/e/ref` sits
# 9 directories above the git dir (test_b26 pins this), so lowering it opens a silent blind spot.
MAX_ANCESTORS = 12

ENV_ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")
# The lexer is linear, but a payload-sized command would still outrun the hook timeout.
MAX_COMMAND = 100_000


def _load_shell_parser() -> Optional[Any]:
    """The shell_parser.py copy delivered beside this file; None leaves only the Bash half
    inert, never the Edit half."""
    try:
        spec = importlib.util.spec_from_file_location(
            "shell_parser", Path(__file__).resolve().parent / "shell_parser.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    except (OSError, ImportError, AttributeError):
        return None
    return module


shell_parser = _load_shell_parser()

# Programs that write a file named in their arguments, mapped to the flags that make them do so
# (an empty tuple: always). Completeness is an explicit non-goal — an unlisted command is
# allowed, so add rows as incidents teach us new ones. A short flag counts anywhere in a
# combined cluster (`-pi`, `-Ei`) up to the first letter that takes a value (VALUE_LETTERS).
# `chmod`/`chown`/`touch` are deliberately absent: none can lose the config's contents, and each
# refused an ordinary command (`chmod -R 700 .git`) for no protective gain.
MUTATORS: Dict[str, Tuple[str, ...]] = {
    "tee": (), "truncate": (), "dd": (), "rm": (),
    "cp": (), "mv": (), "ln": (), "install": (),
    "sed": ("-i", "--in-place"), "perl": ("-i",),
}
# Short options whose value follows in the same cluster: `perl -Mstrict` is not `-i`.
VALUE_LETTERS = {"sed": "efl", "perl": "0CdDFiIlmMVxeE"}
# Programs whose write lands in the LAST non-flag argument, or in the `-t DIR` they name; every
# other path is a source to be read. Scanning their sources refused `cp .git/config
# /tmp/backup` — the backup a human takes before a repair — and called it a write target.
DEST_ONLY = frozenset(("cp", "mv", "ln", "install"))
TARGET_DIRECTORY = "--target-directory"
# Interpreters whose write hides inside a code string, where no argv path ever appears.
INTERPRETERS = frozenset(("python", "python3", "perl", "ruby", "node", "awk"))
CODE_FLAG_LETTERS = frozenset("ce")
# Path-shaped words inside a code string. Deliberately narrow: it must not match whole sentences.
CODE_WORD = re.compile(r"[\w.~/+-]+")
# A code string is only a write when it also carries one of these. Without it the scanner refused
# `python3 -c "print(open('.git/HEAD').read())"` — a read — and called it a write.
CODE_WRITE_HINT = re.compile(
    r"""["'][waxr]\+?["']|\bw\+?["']|>>?|\b(?:write\w*|truncate|unlink|remove|rename|
        replace|rmtree|mkdir|makedirs|dump|copyfile|copy2?|move|chmod|symlink|link|touch|
        create|save|flush|print\s*\(\s*[^)]*,\s*file\s*=)\b|\*\*\*\s*
        (?:Update|Add|Delete|Move)\s+File""",
    re.VERBOSE,
)

DENY_REASON = (
    "Git internals guard: {detail}. That is git's own storage, and a hand write there loses "
    "whatever it does not copy forward — a `.git/config` truncated this way dropped "
    "`remote.origin.fetch`, after which `git fetch` kept reporting success while "
    "`refs/remotes/origin/*` silently stopped moving. Use git's own writers, which rewrite the "
    "file atomically: `git config <key> <value>`, `git remote set-url` / `git remote add`, "
    "`git config --unset <key>`. A human can export {override}=1 in the session environment "
    "for a deliberate manual repair."
)


# --------------------------------------------------------------------------------------------
# The protected-path rule: path-shaped, never program-shaped.
# --------------------------------------------------------------------------------------------

def normalized(path: str, cwd: Optional[str] = None) -> str:
    """*path* with `~` expanded, resolved against *cwd* (or the process cwd) and normalised."""
    expanded = os.path.expanduser(path)
    if not os.path.isabs(expanded):
        expanded = os.path.join(cwd or os.getcwd(), expanded)
    return os.path.normpath(expanded)


def global_config_paths() -> Tuple[str, ...]:
    """The user-level config files git reads, per its own lookup order."""
    home = os.path.expanduser("~")
    xdg = os.environ.get("XDG_CONFIG_HOME") or os.path.join(home, ".config")
    return (os.path.normpath(os.path.join(home, ".gitconfig")),
            os.path.normpath(os.path.join(xdg, "git", "config")),
            os.path.normpath(os.path.join(home, ".config", "git", "config")))


def git_dir_above(path: str) -> bool:
    """True when a bounded walk up from *path* finds a directory holding all GIT_DIR_MARKERS."""
    current = os.path.dirname(path)
    for _ in range(MAX_ANCESTORS):
        if not current or current == os.path.dirname(current):
            return False
        if all(os.path.exists(os.path.join(current, m)) for m in GIT_DIR_MARKERS):
            return True
        current = os.path.dirname(current)
    return False


def is_protected(path: str, cwd: Optional[str] = None) -> bool:
    """True when writing *path* would be a hand write into a git dir or a user git config,
    judged both as written and after symlinks resolve (`ln -s .git/config cfg` makes `cfg`
    the config)."""
    if not isinstance(path, str) or not path.strip():
        return False
    target = normalized(path, cwd)
    if _protected_target(target):
        return True
    real = os.path.realpath(target)
    return real != target and _protected_target(real)


def _protected_target(target: str) -> bool:
    """is_protected for an absolute, normalised path, with no symlink resolution."""
    parts = target.split(os.sep)
    if ".git" in parts[:-1]:
        return True
    if parts[-1] == ".git":  # the pointer FILE a linked worktree or submodule uses
        return True
    if target in global_config_paths():
        return True
    # Only paths with no literal `.git` component reach the filesystem — bare repos and
    # `git init --separate-git-dir`. The component tests above already settled the rest, and
    # skipping the probe keeps the common case free of stat calls.
    return ".git" not in parts and git_dir_above(target)


# --------------------------------------------------------------------------------------------
# Bash: what the command would write.
# --------------------------------------------------------------------------------------------

def has_code_flag(args: List[str]) -> bool:
    """True when an interpreter is being handed a program on the command line (`-c`/`-e`)."""
    return any(a.startswith("-") and not a.startswith("--") and
               CODE_FLAG_LETTERS & set(a[1:]) for a in args)


def has_mutating_flag(program: str, args: List[str], flags: Tuple[str, ...]) -> bool:
    """True when *args* carry one of *flags*: a long form (`--in-place`, `--in-place=.bak`)
    or a short letter anywhere in a cluster (`-i`, `-i.bak`, `-pi`, `-Ei`)."""
    letters = shell_parser.short_flags(args, stop=VALUE_LETTERS.get(program, ""))
    return any(flag[1:] in letters if not flag.startswith("--")
               else any(arg == flag or arg.startswith(flag + "=") for arg in args)
               for flag in flags)


def destinations(args: List[str]) -> List[str]:
    """Where `cp`/`mv`/`ln`/`install` write: each source's name inside a `-t DIR` /
    `--target-directory=DIR`, else the last non-flag argument."""
    directory = None
    sources: List[str] = []
    words = iter(args)
    for arg in words:
        if arg.startswith(TARGET_DIRECTORY):
            directory = arg.partition("=")[2] or next(words, None)
        elif arg.startswith("-") and not arg.startswith("--") and "t" in arg[1:]:
            directory = arg[arg.index("t", 1) + 1:] or next(words, None)
        elif not arg.startswith("-"):
            sources.append(arg)
    if directory is None:
        return sources[-1:]
    return [os.path.join(directory, os.path.basename(s.rstrip("/"))) for s in sources] or \
        [directory]


def mutator_target(program: str, args: List[str], cwd: Optional[str]) -> Optional[str]:
    """The protected path *program* would write, or None when it writes none."""
    flags = MUTATORS.get(program)
    if flags is None:
        return None
    if flags and not has_mutating_flag(program, args, flags):
        return None
    arguments = destinations(args) if program in DEST_ONLY else args
    for arg in arguments:
        candidate = arg.split("=", 1)[1] if ENV_ASSIGN.match(arg) else arg  # dd's `of=<path>`
        if is_protected(candidate, cwd):
            return candidate
    return None


def code_target(args: List[str], cwd: Optional[str]) -> Optional[str]:
    """The protected path an interpreter one-liner WRITES, or None.

    A code string counts as a write only when it also carries a CODE_WRITE_HINT token, so a
    pure read (`print(open('.git/HEAD').read())`) is allowed and the deny message never calls
    a read a write.
    """
    joined = " ".join(args)
    if not CODE_WRITE_HINT.search(joined):
        return None
    for arg in args:
        for word in CODE_WORD.findall(arg):
            if is_protected(word, cwd):
                return word
    return None


def chdir_target(command: Any, cwd: Optional[str]) -> Optional[str]:
    """Where a bare `cd <dir>` moves the shell, so a later relative write resolves there."""
    if command.program != "cd":
        return None
    args = [a for a in command.args if not a.startswith("-")]
    return normalized(args[0], cwd) if len(args) == 1 else None


def segment_violation(command: Any, cwd: Optional[str]) -> Optional[str]:
    """Why this simple command writes a git dir, or None — an unlisted program is allowed."""
    program, args = command.program, list(command.args)
    if program == "git":
        if args and args[0] == "config" and any(a in ("--edit", "-e") for a in args[1:]):
            # `git config --edit` opens the raw config file in an editor -- the same
            # whole-file rewrite the invariant and the repair playbook both name as a trap.
            # Every other git invocation stays allowed: git's own writes are the repair route.
            return "git config --edit opens the raw config file in an editor"
        return None  # git's own writes are atomic and intentional; they are the repair route
    target = mutator_target(program, args, cwd)
    if target is not None:
        return f"`{program}` would write {target}"
    if program in INTERPRETERS and has_code_flag(args):
        target = code_target(args, cwd)
        if target is not None:
            return f"a `{program}` one-liner writes {target}"
    return None


def find_violation(command: str, cwd: Optional[str] = None) -> Optional[str]:
    """Why *command* hand-writes a git dir, or None when it does not.

    Each simple command resolves relative paths against the directory its scope has `cd`-ed
    to: a subshell or substitution starts where its parent stood, and its own `cd` ends with it.
    """
    if not isinstance(command, str) or len(command) > MAX_COMMAND or shell_parser is None:
        return None
    cursors: Dict[Tuple[int, ...], Optional[str]] = {(): cwd}
    for simple in shell_parser.parse(command):
        if simple.scope not in cursors:
            parent = simple.scope[:-1]
            while parent not in cursors:
                parent = parent[:-1]
            cursors[simple.scope] = cursors[parent]
        cursor = cursors[simple.scope]
        for op, target in simple.redirects:
            if ">" in op and is_protected(target, cursor):
                return f"a shell redirect would overwrite {target}"
        moved = chdir_target(simple, cursor)
        if moved is not None:
            cursors[simple.scope] = moved
            continue
        reason = segment_violation(simple, cursor)
        if reason:
            return reason
    return None


# --------------------------------------------------------------------------------------------
# The hook.
# --------------------------------------------------------------------------------------------

def edit_violation(tool_name: str, tool_input: Dict[str, Any],
                   cwd: Optional[str]) -> Optional[str]:
    """Why this edit-shaped call writes a git dir, or None."""
    if tool_name not in EDIT_TOOLS:
        return None
    path = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not isinstance(path, str) or not path.strip() or not is_protected(path, cwd):
        return None
    return f"{tool_name} targets {path}"


def bash_violation(tool_name: str, tool_input: Dict[str, Any],
                   cwd: Optional[str]) -> Optional[str]:
    """Why this Bash call writes a git dir, or None."""
    if str(tool_name).lower() not in BASH_TOOLS:
        return None
    command = tool_input.get("command")
    if not isinstance(command, str) or not command.strip():
        return None
    return find_violation(command, cwd)


# --------------------------------------------------------------------------------------------
# Hermes: the same rule, reached under Hermes' own tool names.
# --------------------------------------------------------------------------------------------

# Hermes tool -> the argument naming a path it would write, per the tool schemas in
# hermes-agent/tools/file_tools.py. `terminal` needs no row: its name is already in
# BASH_TOOLS and its argument is already `command`.
HERMES_PATH_ARGS = {"write_file": "path", "patch": "path"}
# Hermes tool -> an argument whose string hides the write, with no argv path anywhere: a
# Python program (`execute_code`) or a V4A patch body naming its files (`patch`, mode=patch).
HERMES_CODE_ARGS = {"execute_code": "code", "patch": "patch"}
HERMES_WORKDIR_ARG = "workdir"


def hermes_violation(tool_name: str, tool_input: Dict[str, Any],
                     cwd: Optional[str]) -> Optional[str]:
    """Why this Hermes call hand-writes a git dir, or None. Translates names, not the rule."""
    reason = bash_violation(tool_name, tool_input, cwd)
    if reason is not None:
        return reason
    path = tool_input.get(HERMES_PATH_ARGS.get(tool_name, ""))
    if isinstance(path, str) and path.strip() and is_protected(path, cwd):
        return f"{tool_name} targets {path}"
    code = tool_input.get(HERMES_CODE_ARGS.get(tool_name, ""))
    if isinstance(code, str) and code.strip() and len(code) <= MAX_COMMAND:
        # Hermes fails CLOSED on a pre_tool_call timeout; the lexer is superlinear, so the
        # payload gets the same bound find_violation applies to shell commands.
        target = code_target([code], cwd)
        if target is not None:
            return f"a `{tool_name}` payload writes {target}"
    return None


def hermes_decision(tool_name: str = "", args: Optional[Dict[str, Any]] = None,
                    **_kwargs: Any) -> Optional[Dict[str, str]]:
    """A Hermes pre_tool_call block decision, or None to allow. Fails open on everything."""
    try:
        if os.environ.get(OVERRIDE_ENV, "").strip():
            return None
        tool_input = args if isinstance(args, dict) else {}
        workdir = tool_input.get(HERMES_WORKDIR_ARG)
        detail = hermes_violation(
            str(tool_name or ""), tool_input,
            workdir if isinstance(workdir, str) and workdir.strip() else None)
        if detail is None:
            return None
        return {"action": "block",
                "message": DENY_REASON.format(detail=detail, override=OVERRIDE_ENV)}
    except Exception:  # pylint: disable=broad-exception-caught
        return None


def decide(payload: Dict[str, Any]) -> int:
    """Print a deny decision iff the call would hand-write a git dir. Always returns 0."""
    if not isinstance(payload, dict):
        return 0
    if os.environ.get(OVERRIDE_ENV, "").strip():
        return 0
    tool_name = payload.get("tool_name") or payload.get("toolName") or ""
    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    if not isinstance(tool_input, dict):
        return 0
    cwd = payload.get("cwd") if isinstance(payload.get("cwd"), str) else None
    detail = edit_violation(tool_name, tool_input, cwd) or \
        bash_violation(tool_name, tool_input, cwd)
    if detail is None:
        return 0
    print(json.dumps({"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "deny",
        "permissionDecisionReason": DENY_REASON.format(detail=detail, override=OVERRIDE_ENV),
    }}))
    return 0


def main() -> int:
    """Read the hook payload from stdin. Fails open: a broken guard must never brick editing."""
    try:
        return decide(json.load(sys.stdin))
    except Exception:  # pylint: disable=broad-exception-caught
        return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
