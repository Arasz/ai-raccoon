"""The once-per-project bus identity (D2): .ai-badger/project-id.

Minted at scaffold time so the message bus can resolve "where there is .ai-badger,
there is a project" without any external registry. One uuid4 per directory — a
worktree is its own project. Existing ids are never regenerated.

The same module carries the small ai-raccoon CLI client (ai-raccoon 1.57.0's
`project id get/register` verbs) and the one flow both mint sites share: a missing or
blank id file reuses the id the local bank already knows by name, and a minted uuid4 is
registered so the memory hook and the gate can read and write the bank. Scaffold and
den-refresh both call ensure_project_id.
"""
from __future__ import annotations

import os
import re
import shlex
import shutil
import subprocess
import tempfile
import uuid
from pathlib import Path
from typing import Dict, List, NamedTuple, Optional, Tuple

# AI_BADGER_RACCOON_REGISTER=0 disables every ai-raccoon call (only the literal '0').
REGISTER_SWITCH = "AI_BADGER_RACCOON_REGISTER"

# --- exit codes begin ---
# The ai-raccoon 1.57.0 project-id contract (ADR-0107, src/AiRaccoon/ErrorCode.cs; the
# `project id get/register` verbs land with PR #846). A specific code is matched before
# any band; 18 means "no such name" on get and "the id is retired" on register.
EXIT_OK = 0                   # registered <id>, or already registered <canonical>
EXIT_NOT_GUID = 10            # Usage: argv does not fit the grammar
EXIT_UNPARSEABLE = 11         # argv unrecognised: a binary older than 1.57.0, or changed verb
EXIT_PROJECT_UNKNOWN = 18     # get: no such name; register: the id is retired
EXIT_PROJECT_AMBIGUOUS = 19   # get: several projects share the name
KEY_BAND = range(20, 30)      # Key: ai-raccoon cannot open its bank
SERVER_BAND = range(30, 70)   # Bank/Port/Server/Reach: unreachable, start failed, too old
REACH_BAND = range(60, 70)    # Reach: the server is not running and would not start
TIMEOUT_CODE = 95             # Internal.Timeout

# A cold CLI can start its own server (Reach.StartFailed/AutoStartUnsupported exist), so
# the 15 s a warm call wants is not enough for the first registration after boot.
RACCOON_TIMEOUT_SECONDS = 40
# --- exit codes end ---

# One stored id: a uuid, or a legacy raw-text token ai-raccoon printed as stored.
ID_TOKEN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}")

_MAX_OUTPUT_BYTES = 4096

_REFUSAL = ("ai-raccoon refuses this project's memory reads and writes "
            "(project-not-registered) until it is resolved")


class LookupResult(NamedTuple):
    """One `project id get --name` outcome: kind, the id on a hit, one warning line."""
    kind: str
    id: Optional[str]
    note: Optional[str]


# badger_lib.GIT_LOCATION_ENV, repeated because this bootstrap script ships into projects
# that have no framework checkout to import it from. tests/test_git_invocation.py discovers
# and pins every standalone copy of the stripper.
GIT_LOCATION_ENV = ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
                    "GIT_PREFIX", "GIT_NAMESPACE", "GIT_CEILING_DIRECTORIES")

# Keeps a hung git from delaying the scaffold it is only answering a name for.
_GIT_TIMEOUT_SECONDS = 2


def git_env(env: Optional[Dict[str, str]] = None) -> Dict[str, str]:
    """`env` (default `os.environ`) minus every variable that pins git to another repository."""
    out = dict(os.environ if env is None else env)
    for name in GIT_LOCATION_ENV:
        out.pop(name, None)
    return out


def _git_common_dir(target: Path, env: Optional[Dict[str, str]] = None) -> Optional[str]:
    """`git rev-parse --git-common-dir` for `target`; None on any failure."""
    try:
        done = subprocess.run(
            ["git", "rev-parse", "--path-format=absolute", "--git-common-dir"],
            cwd=str(target), capture_output=True, text=True,
            timeout=_GIT_TIMEOUT_SECONDS, check=False, env=git_env(env),
        )
    except (OSError, subprocess.SubprocessError):
        return None
    if done.returncode != 0:
        return None
    common = done.stdout.strip()
    return common or None


def project_name(target: Path, env: Optional[Dict[str, str]] = None) -> str:
    """The name ai-raccoon knows this project by.

    `git rev-parse --git-common-dir` collapses a linked worktree to its main checkout's
    basename, so den-refresh inside `.ai-badger/worktrees/<task>` asks for the repo's name,
    never the worktree's. Outside git, the on-disk basename answers — recovered from
    `os.listdir` when only the spelling's case differs — else the resolved path's name.
    """
    common = _git_common_dir(target, env)
    if common:
        return Path(common).parent.name
    try:
        entries = os.listdir(target.parent)
    except OSError:
        entries = []
    wanted = target.name.casefold()
    for entry in entries:
        if entry.casefold() == wanted:
            return entry
    return target.resolve().name


def _write_id(aib: Path, project_id: str) -> Optional[str]:
    """Write `<aib>/project-id` (id + newline); a note instead of a crash on OSError."""
    try:
        aib.mkdir(parents=True, exist_ok=True)
        (aib / "project-id").write_text(f"{project_id}\n", encoding="utf-8")
    except OSError as exc:
        return f"could not write {aib / 'project-id'}: {exc}"
    return None


def _is_guid(value: str) -> bool:
    try:
        uuid.UUID(value)
    except (AttributeError, TypeError, ValueError):
        return False
    return True


def ensure_project_id(aib: Path, name: str, *, raccoon: bool,
                      env: Optional[Dict[str, str]] = None
                      ) -> Tuple[Optional[str], List[str]]:
    """The one project-id flow: read, reuse-or-mint, optionally register.

    A present non-blank file is the identity and is never rewritten; when `raccoon` is
    true and it holds a guid it costs exactly one `register` (register folds aliases, so
    no `check` is needed). A missing or blank file: with `raccoon` false, mint a uuid4
    silently; otherwise ask `get --name`, reuse a hit, and on not-found mint and register.
    Every other outcome mints and keeps the warning. Returns (id or None, notes).
    """
    values = dict(os.environ) if env is None else dict(env)
    path = aib / "project-id"
    try:
        current = path.read_text(encoding="utf-8").strip()
    except FileNotFoundError:
        current = ""
    except OSError as exc:
        return None, [f"could not read {path}: {exc}"]
    if current:
        notes = []
        if raccoon and _is_guid(current):
            note = register_project_id(current, name, env=values)
            if note:
                notes.append(note)
        return current, notes
    if not raccoon:
        minted = str(uuid.uuid4())
        note = _write_id(aib, minted)
        return minted, [note] if note else []
    result = lookup_project_id(name, env=values)
    if result.kind == "hit" and result.id:
        note = _write_id(aib, result.id)
        return result.id, [note] if note else []
    minted = str(uuid.uuid4())
    notes = [note for note in (result.note,) if note]
    written = _write_id(aib, minted)
    if written:
        notes.append(written)
    if result.kind == "not-found":
        # The register step is one call site so a future `check` verb or its removal is a
        # one-line change here.
        note = register_project_id(minted, name, env=values)
        if note:
            notes.append(note)
    return minted, notes


def registration_enabled(env: Optional[Dict[str, str]] = None) -> bool:
    """False only when AI_BADGER_RACCOON_REGISTER is exactly '0'. One check, both verbs."""
    values = os.environ if env is None else env
    return values.get(REGISTER_SWITCH) != "0"


def find_raccoon(env: Dict[str, str]) -> Optional[str]:
    """`ai-raccoon` on the absolute PATH entries, else `<HOME>/.dotnet/tools/ai-raccoon`.

    A twin of memory_context.find_executable: the bootstrap scaffolder ships without a
    framework checkout and must not depend on a hook module.
    tests/test_raccoon_cli_client.py::test_find_raccoon_twin_* pins the two copies.
    """
    search_path = os.pathsep.join(
        entry for entry in (env.get("PATH") or "").split(os.pathsep) if os.path.isabs(entry))
    if search_path:
        found = shutil.which("ai-raccoon", path=search_path)
        if found:
            return os.path.abspath(found)
    home = env.get("HOME")
    if home:
        fallback = Path(home) / ".dotnet" / "tools" / "ai-raccoon"
        if fallback.is_file() and os.access(fallback, os.X_OK):
            return os.path.abspath(fallback)
    return None


def _read_capped(handle) -> str:
    """At most 4 KiB of a temp file, decoded tolerantly."""
    return handle.read(_MAX_OUTPUT_BYTES).decode("utf-8", errors="replace")


def _run(exe: str, args: List[str], env: Dict[str, str],
         timeout: float):
    """Run `exe --quiet <args>`; (code or None, stdout, stderr, timed_out).

    Temp files, not pipes: a `serve` the CLI starts cannot hold a pipe open, and no
    output reaches the caller's stdout. The OpenRouter key is not part of this call.
    """
    child_env = dict(env)
    child_env.pop("OPENROUTER_API_KEY", None)
    with tempfile.TemporaryFile() as out, tempfile.TemporaryFile() as err:
        try:
            done = subprocess.run(
                [exe, "--quiet", *args], shell=False, stdin=subprocess.DEVNULL,
                stdout=out, stderr=err, timeout=timeout, env=child_env, check=False,
            )
        except subprocess.TimeoutExpired:
            return None, "", "", True
        except OSError:
            return None, "", "", False
        out.seek(0)
        err.seek(0)
        return done.returncode, _read_capped(out), _read_capped(err), False


def _single_line(stdout: str) -> Optional[str]:
    """`get`'s stdout contract: exactly one line, LF or CRLF, holding one valid token."""
    for ending in ("\r\n", "\n"):
        if stdout.endswith(ending):
            line = stdout[:-len(ending)]
            break
    else:
        return None
    if "\n" in line or "\r" in line or not ID_TOKEN.fullmatch(line):
        return None
    return line


def _stderr_candidates(stderr: str) -> List[str]:
    """The candidate ids 19 lists on stderr, one per line under a header: match per line."""
    return [token for token in (raw.strip() for raw in stderr.splitlines())
            if ID_TOKEN.fullmatch(token)]


def _with_command(text: str, argv: Optional[List[str]]) -> str:
    if argv is None:
        return text
    return f"{text} (command: {shlex.join(['ai-raccoon', *argv])})"


def _timeout_note(timeout: float, argv: Optional[List[str]] = None) -> str:
    return _with_command(
        f"ai-raccoon did not answer within {timeout:g}s; {_REFUSAL}", argv)


def _unavailable_note(code: Optional[int], argv: Optional[List[str]] = None) -> str:
    if code == EXIT_UNPARSEABLE:
        stem = ("ai-raccoon did not recognise `project id ...` (exit 11): it is older than "
                "1.57.0, or the verb changed")
    elif code == TIMEOUT_CODE:
        stem = "ai-raccoon timed out inside its own process (exit 95)"
    elif code in KEY_BAND:
        stem = f"ai-raccoon could not open its bank (exit {code}: key)"
    elif code in SERVER_BAND:
        stem = f"ai-raccoon's server is unreachable or would not start (exit {code})"
    elif code is None:
        stem = "ai-raccoon could not be started"
    else:
        stem = f"ai-raccoon failed (exit {code})"
    return _with_command(f"{stem}; {_REFUSAL}", argv)


def _ambiguous_note(name: str, candidates: List[str]) -> str:
    if candidates:
        stem = (f"several projects are named '{name}' ({', '.join(candidates)}); the memory "
                "hook stays silent in this repo until a human resolves the conflict")
    else:
        stem = (f"several projects are named '{name}'; the memory hook stays silent in this "
                "repo until a human resolves the conflict")
    return f"{stem}; {_REFUSAL}"


def _malformed_note() -> str:
    return f"ai-raccoon's reply was not one valid project id; {_REFUSAL}"


def _retired_note(project_id: str, argv: Optional[List[str]] = None) -> str:
    return _with_command(
        f"project id {project_id} is retired: a human must resolve it before it can be "
        f"registered; {_REFUSAL}", argv)


def lookup_project_id(name: str, *, env: Optional[Dict[str, str]] = None,
                      timeout: float = RACCOON_TIMEOUT_SECONDS) -> LookupResult:
    """`ai-raccoon project id get --name <name>`: hit / not-found / ambiguous / a warning.

    'missing' (no executable) and 'off' (the switch) are silent: a repo that does not use
    ai-raccoon is not warned about it on every scaffold or den-refresh.
    """
    values = dict(os.environ) if env is None else dict(env)
    if not registration_enabled(values):
        return LookupResult("off", None, None)
    exe = find_raccoon(values)
    if exe is None:
        return LookupResult("missing", None, None)
    code, stdout, stderr, timed_out = _run(
        exe, ["project", "id", "get", "--name", name], values, timeout)
    if timed_out:
        return LookupResult("unavailable", None, _timeout_note(timeout))
    if code == EXIT_OK:
        token = _single_line(stdout)
        if token is None:
            return LookupResult("malformed", None, _malformed_note())
        return LookupResult("hit", token, None)
    if code == EXIT_PROJECT_UNKNOWN:
        return LookupResult("not-found", None, None)
    if code == EXIT_PROJECT_AMBIGUOUS:
        return LookupResult("ambiguous", None,
                            _ambiguous_note(name, _stderr_candidates(stderr)))
    return LookupResult("unavailable", None, _unavailable_note(code))


def register_project_id(project_id: str, name: str, *,
                        env: Optional[Dict[str, str]] = None,
                        timeout: float = RACCOON_TIMEOUT_SECONDS) -> Optional[str]:
    """`ai-raccoon project id register <id> --name <name>`: None on success, else a note.

    Register folds aliases: an id the bank already knows exits 0 with "already registered
    <canonical>" and creates nothing, which is why no separate `check` call is needed.
    """
    values = dict(os.environ) if env is None else dict(env)
    if not registration_enabled(values):
        return None
    exe = find_raccoon(values)
    if exe is None:
        return None
    args = ["project", "id", "register", str(project_id)]
    if name:
        args += ["--name", str(name)]
    code, _stdout, _stderr, timed_out = _run(exe, args, values, timeout)
    if timed_out:
        return _timeout_note(timeout, args)
    if code == EXIT_OK:
        return None
    if code == EXIT_PROJECT_UNKNOWN:
        return _retired_note(str(project_id), args)
    return _unavailable_note(code, args)
