"""Statusline capture wiring, one of the scaffold's collaborators.

Points ``.claude/settings.json`` statusLine at ai-badger's capture wrapper and records the
renderer it displaces, so the user's own status line keeps rendering behind the capture.
Opt-in via ``config.statusLineCapture.enabled``: a project-level statusLine overrides the
user's own, so it is never wired unasked, and turning the flag back off restores the
recorded renderer rather than leaving the status bar routed through ai-badger.
"""
from __future__ import annotations

import importlib.util
import os
import sqlite3
from contextlib import closing
from pathlib import Path
from typing import Any, Dict, Optional, Tuple

import config_guard as cg
from hook_wiring import PROJECT_DIR_VAR, skill_script_id
from scaffold_context import ScaffoldContext

CAPTURE_SCRIPT = "task/scripts/statusline_capture.py"

# Machine-local, and deliberately not the committed settings.json: the delegate is a path
# personal to one machine, while the wired command must stay portable across checkouts.
DELEGATE_RECORD = Path(".ai-badger") / "task-tracking" / "statusline-delegate.json"


def _badger_store():
    """The store module (ADR-0024) vendored beside the task skill's tracker_lib, or None.

    The task skill's vendored copy ships wherever the capture script is scaffolded, so the
    relative hop from this module's directory finds it in the framework checkout and in a
    scaffolded project alike. None means the task skill never shipped and the legacy
    delegate file stays the surface (the capture's dual-read still merges it, D5a).
    """
    try:
        import badger_store  # pylint: disable=import-outside-toplevel
        return badger_store
    except ImportError:
        pass
    path = Path(__file__).resolve().parents[1] / "task" / "scripts" / "badger_store.py"
    if not path.is_file():
        return None
    spec = importlib.util.spec_from_file_location("statusline_wiring_badger_store", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _statusline_families(store_mod, tracking: Path) -> dict:
    """The statusline store families (ADR-0024): one table, two KV rows — 'state', 'delegate'.

    Mirrors tracker_lib._task_families()'s statusline entries; the row keys are the schema
    ruling, not local convention.
    """
    return {
        "statusline": store_mod.Family(
            table="statusline", db="tracking",
            legacy_path=lambda: tracking / "statusline-state.json",
            legacy_kind="kvdoc", row_key="state"),
        "statusline_delegate": store_mod.Family(
            table="statusline", db="tracking",
            legacy_path=lambda: tracking / "statusline-delegate.json",
            legacy_kind="kvdoc", row_key="delegate"),
    }


def _open_delegate_store(store_mod, tracking: Path):
    """The tracking store over *tracking* with the statusline families registered."""
    os.environ[store_mod.TRACKING_ROOT_ENV] = str(tracking)
    return store_mod.open_tracking(families=_statusline_families(store_mod, tracking))


def capture_command(aib_rel: str) -> str:
    """The portable statusLine command running the capture wrapper from *aib_rel*."""
    return f'python3 "{PROJECT_DIR_VAR}/{aib_rel}/skills/{CAPTURE_SCRIPT}"'


def is_capture_command(command: str) -> bool:
    """Whether *command* already runs ai-badger's capture wrapper, from any checkout."""
    return skill_script_id(command) == CAPTURE_SCRIPT


def read_statusline(settings: Dict[str, Any], path: Path) -> Tuple[Optional[Dict[str, Any]], str]:
    """Return ``(entry, refusal)`` for the statusLine already in *settings*.

    ``entry`` is ``{}`` when there is none, or ``None`` when the key holds something that
    is not a mapping — which the caller must refuse rather than overwrite.
    """
    entry = settings.get("statusLine")
    if entry is None:
        return {}, ""
    if not isinstance(entry, dict):
        return None, cg.refusal(path, "statusLine is not a mapping")
    return entry, ""


class StatusLineWiring:
    """Points .claude/settings.json statusLine at ai-badger's capture wrapper, or restores it."""

    def __init__(self, ctx: ScaffoldContext):
        self.ctx = ctx

    def _capture_enabled(self) -> bool:
        return bool(self.ctx.config.get("statusLineCapture", {}).get("enabled")) \
            and "claude" in self.ctx.config.get("agents", [])

    def _user_statusline_command(self) -> Optional[str]:
        """The command from ``~/.claude/settings.json`` statusLine, when it has one."""
        settings, note = cg.read_json_mapping(Path.home() / ".claude" / "settings.json")
        if settings is None:
            self.ctx.notes.append(f"{note} (no delegate read from it — a user-level status "
                              f"line, if any, will stop rendering)")
            return None
        entry = settings.get("statusLine")
        return entry.get("command") if isinstance(entry, dict) else None

    def wire(self) -> None:
        """Wire the capture wrapper into ``.claude/settings.json``, preserving the renderer.

        Never chains the wrapper to itself: an entry already running the capture keeps the
        delegate recorded by the run that displaced the original renderer.
        """
        if not self._capture_enabled():
            self.unwire()
            return
        if not (self.ctx.aib / "skills" / CAPTURE_SCRIPT).is_file():
            self.ctx.notes.append("statusline capture not wired — the 'task' skill, which owns "
                              "the capture script, is not scaffolded")
            return

        settings_path = self.ctx.target / ".claude" / "settings.json"
        settings, note = cg.read_json_mapping(settings_path)
        if settings is None:
            self.ctx.notes.append(f"{note} (statusline capture not wired)")
            return

        existing, refusal = read_statusline(settings, settings_path)
        if existing is None:
            self.ctx.notes.append(f"{refusal} (statusline capture not wired)")
            return

        current = existing.get("command", "")
        if not is_capture_command(current):
            self._record_delegate(current or self._user_statusline_command())

        entry = dict(existing)
        entry["type"] = "command"
        entry["command"] = capture_command(self.ctx.aib.relative_to(self.ctx.target).as_posix())
        settings["statusLine"] = entry
        cg.write_json_with_backup(settings_path, settings)
        self.ctx.record_generated_config(settings_path, ".claude/settings.json")
        self.ctx.notes.append("wired statusline capture into .claude/settings.json")

    def unwire(self) -> None:
        """Restore the displaced renderer, or drop ``statusLine`` when there was none.

        Only ever touches a ``statusLine`` running ai-badger's own wrapper: a renderer the
        scaffolder never displaced is not the scaffolder's to remove.
        """
        settings_path = self.ctx.target / ".claude" / "settings.json"
        if not settings_path.exists():
            return
        settings, note = cg.read_json_mapping(settings_path)
        if settings is None:
            self.ctx.notes.append(f"{note} (statusline capture not unwired)")
            return

        existing, _ = read_statusline(settings, settings_path)
        if not existing or not is_capture_command(existing.get("command", "")):
            return

        record = self._read_delegate()
        if record is None:
            self.ctx.notes.append("statusline capture left wired — the renderer "
                              "it displaced cannot be read back")
            return

        delegate = record.get("command")
        if delegate:
            entry = dict(existing)
            entry["type"] = "command"
            entry["command"] = delegate
            settings["statusLine"] = entry
        else:
            del settings["statusLine"]
        cg.write_json_with_backup(settings_path, settings)
        self.ctx.notes.append("unwired statusline capture from .claude/settings.json")

    def _read_delegate(self) -> Optional[Dict[str, Any]]:
        """The delegate record: the store row, else the legacy file, else None.

        The first delegate write migrates the file into the store and renames it, so the
        store row is the surface from then on; a project the store never reached still has
        the file. Broken reads return None — unwire then leaves the capture wired rather
        than dropping a renderer it cannot read back.
        """
        store_mod = _badger_store()
        tracking = self.ctx.target / DELEGATE_RECORD.parent
        if store_mod is not None:
            try:
                with closing(_open_delegate_store(store_mod, tracking)) as store:
                    record = store.kv_get("statusline", "delegate", {})
                if isinstance(record, dict):
                    return record
            except (OSError, sqlite3.Error):
                pass
        record, _note = cg.read_json_mapping(self.ctx.target / DELEGATE_RECORD)
        return record

    def _record_delegate(self, command: Optional[str]) -> None:
        """Record the renderer the wrapper delegates to; ``None`` means capture only."""
        record = {"command": command}
        store_mod = _badger_store()
        tracking = self.ctx.target / DELEGATE_RECORD.parent
        if store_mod is None:
            cg.write_json_with_backup(self.ctx.target / DELEGATE_RECORD, record)
            return
        with closing(_open_delegate_store(store_mod, tracking)) as store:
            store.kv_set("statusline", "delegate", record)
