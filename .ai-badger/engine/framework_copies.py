"""Every tree on this machine that claims to be ai-badger, and the one copy we may delete.

Ownership decides: `~/.ai-badger/framework` is written by `badger_lib._clone_pinned`, so ai-badger
may prune it; `~/.claude/plugins/cache/` is Claude Code's and is only ever read (#109, and the
0.19.0 invariant that no command destroys state it did not create).

Stdlib only, and deliberately free of `badger_lib`: a SessionStart hook imports this module on
every session and must not pay for 1000 lines it never calls. (Until 0.93.0 the stated reason
was `badger_lib`'s unguarded `jsonschema` import, which is now deferred.)
"""
from __future__ import annotations

import os
import shutil
from pathlib import Path
from typing import Iterator, List, NamedTuple, Optional, Tuple

HOME_CACHE_PARTS = (".ai-badger", "framework")
PLUGIN_CACHE_PARTS = (".claude", "plugins", "cache", "ai-badger", "ai-badger")

# Owners. Only what ai-badger itself created may ever be removed by ai-badger.
AI_BADGER = "ai-badger"
CLAUDE_CODE = "claude-code"
CHECKOUT = "checkout"

OWNER_LABELS = {
    AI_BADGER: "ai-badger's own cache, which nothing updates in place",
    CLAUDE_CODE: "Claude Code's plugin cache, which ai-badger only ever reads",
    CHECKOUT: "a framework checkout",
}

PRUNE_COMMAND = "den-refresh --prune-cache"

# prune_home_cache statuses.
ABSENT = "absent"
REPORTED = "reported"
REMOVED = "removed"
REFUSED = "refused"
FAILED = "failed"


class Copy(NamedTuple):
    """One tree claiming to be ai-badger: where it is, what it says it is, and whose it is."""

    path: Path
    version: Optional[str]
    owner: str
    running: bool

    @property
    def prunable(self) -> bool:
        """Only our own cache, and never the tree currently answering as the framework root."""
        return self.owner == AI_BADGER and not self.running


class Prune(NamedTuple):
    """The outcome of a prune request: what was found and what was (not) done about it."""

    status: str
    path: Path
    version: Optional[str]
    detail: str


def is_framework_root(path: Path) -> bool:
    """A framework root holds schemas/, features/ and engine/badger_lib.py.

    Restated rather than imported from badger_lib, which pulls in jsonschema; ADR-0009's one
    predicate is pinned to this copy by test_the_copy_predicate_agrees_with_badger_lib.
    """
    return ((path / "schemas").is_dir() and (path / "features").is_dir()
            and (path / "engine" / "badger_lib.py").is_file())


def home_cache(home: Optional[Path] = None) -> Path:
    """`~/.ai-badger/framework` — the clone ai-badger makes when it has no other root."""
    return Path(home or Path.home()).joinpath(*HOME_CACHE_PARTS)


def plugin_cache(home: Optional[Path] = None) -> Path:
    """`~/.claude/plugins/cache/ai-badger/ai-badger` — Claude Code's, one directory per version."""
    return Path(home or Path.home()).joinpath(*PLUGIN_CACHE_PARTS)


def read_version(root: Path) -> Optional[str]:
    """The VERSION a tree reports, or None when it has none or cannot be read."""
    try:
        return (root / "VERSION").read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def _candidates(home: Optional[Path]) -> Iterator[Tuple[Path, str]]:
    """Every path that could hold a framework tree, with the owner of the place it sits in."""
    yield home_cache(home), AI_BADGER
    try:
        children = sorted(plugin_cache(home).iterdir())
    except OSError:
        children = []
    for child in children:
        yield child, CLAUDE_CODE


def discover(running_root: Optional[Path] = None, home: Optional[Path] = None) -> List[Copy]:
    """Every framework tree on this machine, the running one first. Never raises."""
    running = Path(running_root).resolve() if running_root else None
    seen = set()
    copies: List[Copy] = []
    for path, owner in _candidates(home):
        if not is_framework_root(path):
            continue
        resolved = path.resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        copies.append(Copy(path, read_version(path), owner, resolved == running))

    if running and running not in seen and is_framework_root(running):
        copies.insert(0, Copy(running, read_version(running), CHECKOUT, True))
    copies.sort(key=lambda c: (not c.running, str(c.path)))
    return copies


def idle_home_cache(copies: List[Copy]) -> Optional[Copy]:
    """Our own cache when it exists and is not what is running — the copy nothing refreshes."""
    return next((c for c in copies if c.owner == AI_BADGER and not c.running), None)


def idle_cache_disagrees(copies: List[Copy]) -> bool:
    """True when that idle cache reports a different version than the tree that is running."""
    idle = idle_home_cache(copies)
    running = next((c for c in copies if c.running), None)
    return bool(idle and running and idle.version and running.version
                and idle.version != running.version)


def _describe(copy: Copy) -> str:
    line = f"  - {copy.version or '?'}  {copy.path}  — {OWNER_LABELS[copy.owner]}"
    if copy.running:
        return line + "; this session is running from it"
    if copy.prunable:
        return line + f"; ai-badger created it and can remove it: {PRUNE_COMMAND}"
    return line


def competing_copies_notice(copies: List[Copy]) -> Optional[str]:
    """Name the path and version of every competing tree, or None when only one exists.

    Two drift notices at two versions read as a framework versioning bug; the real message is
    that two installs are answering (#109).
    """
    if len(copies) < 2:
        return None
    header = (f"[ai-badger] {len(copies)} trees on this machine claim to be ai-badger, so this "
              f"notice can appear once per tree. The versions below differ because the copies "
              f"do, not because the framework does:")
    return "\n".join([header, *(_describe(c) for c in copies)])


def _escaping_symlink(cache: Path) -> Optional[Path]:
    """The first link under `cache` whose target leaves it, or None. Refusal beats resolution."""
    inside = cache.resolve()
    for path in cache.rglob("*"):
        if not path.is_symlink():
            continue
        target = Path(os.path.realpath(str(path)))
        if target != inside and inside not in target.parents:
            return path
    return None


def _refusal(cache: Path, home: Path, running_root: Optional[Path]) -> Optional[str]:
    """Why this directory is not demonstrably the cache we created, or None when it is."""
    if cache.is_symlink():
        return (f"{cache} is a symlink to {os.readlink(str(cache))}; ai-badger removes only the "
                f"directory it cloned, never a link out of it")
    expected = Path(home).resolve().joinpath(*HOME_CACHE_PARTS)
    if cache.resolve() != expected:
        return f"{cache} resolves to {cache.resolve()}, which is not {expected}"
    if not is_framework_root(cache):
        return (f"{cache} is not an ai-badger framework root (no schemas/ + features/ + "
                f"engine/badger_lib.py) — ai-badger did not create it and will not remove it")
    if running_root and Path(running_root).resolve() == cache.resolve():
        return (f"{cache} is the framework root this process is running from; pass "
                f"--root <framework checkout> and retry")
    escaping = _escaping_symlink(cache)
    if escaping:
        return f"{cache} holds a symlink that leaves it ({escaping}); refusing to delete through it"
    return None


def prune_home_cache(home: Optional[Path] = None, running_root: Optional[Path] = None,
                     execute: bool = False) -> Prune:
    """Report `~/.ai-badger/framework`, and remove it only when `execute` was asked for.

    Deleting from a user's home during an ordinary refresh is surprising and hard to reverse,
    so the default detects and reports. Anything not demonstrably the cache we created is
    refused (#109).
    """
    cache = home_cache(home)
    if not cache.exists() and not cache.is_symlink():
        return Prune(ABSENT, cache, None, f"no framework cache at {cache}")

    version = read_version(cache)
    refusal = _refusal(cache, home or Path.home(), running_root)
    if refusal:
        return Prune(REFUSED, cache, version, refusal)
    if not execute:
        return Prune(REPORTED, cache, version,
                     f"{cache} is version {version or '?'} and nothing updates it in place; "
                     f"remove it with: {PRUNE_COMMAND}")
    try:
        shutil.rmtree(cache)
    except OSError as exc:
        return Prune(FAILED, cache, version, f"could not remove {cache}: {exc}")
    return Prune(REMOVED, cache, version, f"removed {cache} (was version {version or '?'})")
