#!/usr/bin/env python3
"""Plugin-provided SessionStart hook: Tier 1 drift notice (ADR-0001 decision 5, #24).

Registered via `hooks/hooks.json` at the framework repo root, so this fires automatically for
every consumer who installs the ai-badger plugin -- running from the *plugin's own* on-disk
copy. That is load-bearing: `$CLAUDE_PLUGIN_ROOT` is a command-string placeholder the CLI
substitutes into a plugin-provided `hooks.json`'s `command` field, not a session-wide
environment variable (N plugins load per session, so there is no single value one could read
from `os.environ`). A hook registered by a *consumer's own* `.claude/settings.json` -- which is
what the previous, dead implementation on `session_start_hook.py` assumed -- never has it set.

Reads the same SessionStart stdin payload as `session_start_hook.py` (see that script's
docstring for the exact JSON shape) and emits the same `hookSpecificOutput`/`additionalContext`
shape on stdout, but only when the plugin's `VERSION` and the target project's
`.ai-badger/manifest.json` `frameworkVersion` differ. The comparison itself is shared with
`session_start_hook.py`'s prior implementation via `drift_notice.scaffold_drift_notice` --
see that module's docstring for why it was extracted rather than duplicated or imported whole.

Must never crash and must never print anything unconditionally: silent (empty stdout, exit 0)
on a version match, an unscaffolded project, a plugin root that cannot be located, or any read
error. A hook that breaks SessionStart or nags unconditionally defeats its own purpose.
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, Optional

sys.path.insert(0, str(Path(__file__).resolve().parent))
import drift_notice  # pylint: disable=wrong-import-position

try:
    import debug_log  # pylint: disable=wrong-import-position
except ImportError:  # pragma: no cover - a missing logger must never break a hook
    debug_log = None

COMPONENT = "drift_notice_hook"

# The hook payload, kept so every record can name the project it came from. An unattributed
# record pools into every project's analysis; see `call-behaviorist`.
_PAYLOAD: Dict[str, Any] = {}


def _debug(event: str, **fields) -> None:
    """Record that this hook ran. Silent when debug is off or the logger is unavailable."""
    if debug_log is None:
        return
    root = fields.pop("project", None) or resolve_project_root(_PAYLOAD)
    debug_log.log_event(COMPONENT, event, project=str(root) if root else None, **fields)


def _bootstrap_lib() -> Path:
    """Put the framework's engine/ and tooling/ on sys.path and return its root.

    One predicate, shared with badger_lib.is_framework_root: schemas/ + features/ +
    engine/badger_lib.py. Ordered inputs: --root, an ancestor walk, $AI_BADGER, the root
    recorded in a .ai-badger/manifest.json above this file, then ~/.ai-badger/framework
    (ADR-0009). Duplicated verbatim in every entry point because locating badger_lib is
    what it is for.
    """

    def is_root(path):
        return ((path / "schemas").is_dir() and (path / "features").is_dir()
                and (path / "engine" / "badger_lib.py").is_file())

    def argv_root():
        # sys.argv is ours only when this file is the program being run; these modules are
        # also imported into hosts whose own --root means something else entirely.
        try:
            if not sys.argv or Path(sys.argv[0]).resolve() != Path(__file__).resolve():
                return None
        except (OSError, ValueError):
            return None
        argv = sys.argv[1:]
        for i, arg in enumerate(argv):
            if arg == "--root" and i + 1 < len(argv):
                return argv[i + 1]
            if arg.startswith("--root="):
                return arg.split("=", 1)[1]
        return None

    def checked(value, source):
        root = Path(value).expanduser()
        if not is_root(root):
            raise RuntimeError(
                f"{source} is {root}, which is not an ai-badger framework root "
                f"(no schemas/ + features/ + engine/badger_lib.py)"
            )
        return root

    def manifests(start):
        # Above this file only. A working directory belongs to whatever repo the user
        # opened, and no repo may steer the sys.path of a hook that runs on session start.
        for anc in [start, *start.parents]:
            manifest = (anc / "manifest.json" if anc.name == ".ai-badger"
                        else anc / ".ai-badger" / "manifest.json")
            if not manifest.is_file():
                continue
            try:
                data = json.loads(manifest.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                yield manifest, data

    def recorded(start):
        for manifest, data in manifests(start):
            value = data.get("frameworkRoot")
            if not value:
                continue
            candidate = Path(value).expanduser()
            if not candidate.is_absolute():
                candidate = manifest.parent.parent / candidate
            if is_root(candidate):
                return candidate.resolve()
        return None

    def warn_on_cache_skew(root, start):
        # The cache is last in the order and never updated in place, so its engine can be
        # many releases behind the caller. Say so; never break a session over it.
        if root.resolve() != cache.resolve():
            return
        try:
            have = (cache / "VERSION").read_text(encoding="utf-8").strip()
        except OSError:
            return
        want = next((d.get("frameworkVersion") for _, d in manifests(start)
                     if d.get("frameworkVersion")), None)
        if have and want and have != want:
            print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
                  f"by {want}. The cache is never updated in place — remove it, or pass "
                  f"--root <framework checkout>.", file=sys.stderr)

    here = Path(__file__).resolve()
    cache = Path.home() / ".ai-badger" / "framework"
    value = argv_root()
    if value:
        root = checked(value, "--root")
    else:
        root = next((anc for anc in [here, *here.parents] if is_root(anc)), None)
        if root is None and os.environ.get("AI_BADGER"):
            root = checked(os.environ["AI_BADGER"], "$AI_BADGER")
        root = root or recorded(here) or (cache if is_root(cache) else None)
    if root is None:
        raise RuntimeError(
            f"could not locate the ai-badger framework: none above {here.parent}, no "
            f"$AI_BADGER, no frameworkRoot in a .ai-badger/manifest.json above it, and no "
            f"cache at {cache} — pass --root <framework> or clone "
            f"https://github.com/Arasz/ai-badger"
        )
    warn_on_cache_skew(root, here)
    sys.path.insert(0, str(root / "tooling"))
    sys.path.insert(0, str(root / "engine"))
    return root.resolve()


try:
    FRAMEWORK_ROOT: Optional[Path] = _bootstrap_lib()
except RuntimeError:  # a hook degrades to silence; it never breaks a session
    FRAMEWORK_ROOT = None

try:  # engine/framework_copies.py; a root older than 0.39.0 ships none, and that is not an error
    import framework_copies  # pylint: disable=wrong-import-position
except ImportError:
    framework_copies = None


def resolve_project_root(payload: Dict[str, Any]) -> Optional[Path]:
    """`CLAUDE_PROJECT_DIR` is present in a hook's environment (unlike `CLAUDE_PLUGIN_ROOT`) and
    is authoritative when set. Fall back to the SessionStart payload's own `cwd` field."""
    env_root = os.environ.get("CLAUDE_PROJECT_DIR")
    if env_root:
        return Path(env_root)
    cwd = payload.get("cwd")
    if cwd:
        return Path(cwd)
    return None


def competing_copies_notice(drifted: bool) -> Optional[str]:
    """Name every tree claiming to be ai-badger, when saying so explains what the user sees.

    Attached to a drift notice (which can be printed once per tree, at a different version
    each time), and emitted on its own when our own unused `~/.ai-badger/framework` disagrees
    with the tree that is running. Claude Code's plugin cache keeping older versions is not
    ours to nag about. Never raises: a hook degrades to silence (#109).
    """
    if framework_copies is None or FRAMEWORK_ROOT is None:
        return None
    try:
        copies = framework_copies.discover(running_root=FRAMEWORK_ROOT)
        if not drifted and not framework_copies.idle_cache_disagrees(copies):
            return None
        return framework_copies.competing_copies_notice(copies)
    except OSError:
        return None


def main() -> int:
    """Read the SessionStart payload from stdin; print a drift notice iff versions differ."""
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0
    if not isinstance(payload, dict):
        return 0
    _PAYLOAD.update(payload)

    project_root = resolve_project_root(payload)
    if FRAMEWORK_ROOT is None or project_root is None:
        _debug("skip", reason="no_root" if project_root is None else "no_framework")
        return 0

    notice = drift_notice.scaffold_drift_notice(project_root, str(FRAMEWORK_ROOT))
    parts = [part for part in (notice, competing_copies_notice(bool(notice))) if part]
    if not parts:
        _debug("skip", reason="no_drift")
    else:
        _debug("fire", project=str(project_root))
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "SessionStart",
                "additionalContext": "\n".join(parts),
            }
        }))
    return 0


if __name__ == "__main__":
    sys.exit(main())
