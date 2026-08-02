"""Guards for config files ai-badger merges into but does not own.

"Unreadable" is not "absent": a config that cannot be parsed into a mapping is never
rewritten from an empty dict, and a parseable one is backed up before it is modified.
"""
from __future__ import annotations

import json as _json
import os
import shutil
import tempfile
import time
from pathlib import Path
from typing import Any, Callable, Dict, Optional, Tuple


def read_mergeable_mapping(
    path: Path,
    parse: Callable[[str], Any],
    parse_errors: Tuple[type, ...],
) -> Tuple[Optional[Dict[str, Any]], str]:
    """Return ``(mapping, refusal)`` for a config file about to be merged into.

    ``mapping`` is the parsed dict — ``{}`` when the file is absent or blank — or
    ``None`` when the file exists but is not a readable mapping, in which case
    ``refusal`` is the note explaining what was skipped and why.
    """
    if not path.exists():
        return {}, ""
    try:
        raw = path.read_text(encoding="utf-8")
    except OSError as exc:
        return None, refusal(path, f"unreadable ({type(exc).__name__})")
    if not raw.strip():
        return {}, ""
    try:
        parsed = parse(raw)
    except parse_errors as exc:  # pylint: disable=catching-non-exception
        return None, refusal(path, f"unparseable ({type(exc).__name__})")
    if not isinstance(parsed, dict):
        return None, refusal(path, "top level is not a mapping")
    return parsed, ""


def read_json_mapping(path: Path) -> Tuple[Optional[Dict[str, Any]], str]:
    """``read_mergeable_mapping`` for a JSON config file."""
    return read_mergeable_mapping(path, _json.loads, (ValueError,))


def mapping_section(container: Dict[str, Any], *keys: str) -> Optional[Dict[str, Any]]:
    """Return the nested mapping at *keys*, creating missing levels.

    ``None`` when any level is already occupied by something that is not a mapping —
    the caller must then refuse the write rather than clobber it.
    """
    node = container
    for key in keys:
        node.setdefault(key, {})
        if not isinstance(node[key], dict):
            return None
        node = node[key]
    return node


def refusal(path: Path, reason: str) -> str:
    """The note recorded when a write is skipped to protect an existing file."""
    return f"refused to overwrite {path} — {reason}; update skipped"


def write_with_backup(path: Path, text: str) -> bool:
    """Write *text* atomically, backing up any existing file to ``<name>.bak-<ts>``.

    Returns ``False`` without touching the file when its content already matches.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        if _reads_as(path, text):
            return False
        shutil.copy2(str(path), str(_backup_path(path)))
    _atomic_write(path, text)
    return True


def remove_with_backup(path: Path) -> Path:
    """Delete *path*, leaving a ``<name>.bak-<ts>`` copy beside it; return the backup.

    Retiring a config file ai-badger wrote is still a destructive act on someone's repository,
    so it leaves the same safety copy a merge does.
    """
    backup = _backup_path(path)
    shutil.copy2(str(path), str(backup))
    path.unlink()
    return backup


def _backup_path(path: Path) -> Path:
    """The ``<name>.bak-<ts>`` sibling every destructive operation here copies to first."""
    return path.with_name(f"{path.name}.bak-{time.strftime('%Y%m%d-%H%M%S')}")


def write_json_with_backup(path: Path, data: Dict[str, Any]) -> bool:
    """``write_with_backup`` for pretty-printed, newline-terminated JSON."""
    return write_with_backup(path, _json.dumps(data, indent=2, ensure_ascii=False) + "\n")


def _reads_as(path: Path, text: str) -> bool:
    try:
        return path.read_text(encoding="utf-8") == text
    except OSError:
        return False


def _atomic_write(path: Path, text: str) -> None:
    mode = path.stat().st_mode & 0o7777 if path.exists() else None
    handle, tmp = tempfile.mkstemp(dir=str(path.parent), prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as fh:
            fh.write(text)
        if mode is not None:
            os.chmod(tmp, mode)
        os.replace(tmp, str(path))
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)
