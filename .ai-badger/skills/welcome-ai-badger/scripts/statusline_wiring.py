"""Statusline capture wiring, one of the scaffold's collaborators.

Points ``.claude/settings.json`` statusLine at ai-badger's capture wrapper and records the
renderer it displaces, so the user's own status line keeps rendering behind the capture.
Opt-in via ``config.statusLineCapture.enabled``: a project-level statusLine overrides the
user's own, so it is never wired unasked, and turning the flag back off restores the
recorded renderer rather than leaving the status bar routed through ai-badger.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any, Dict, Optional, Tuple

import config_guard as cg
from hook_wiring import PROJECT_DIR_VAR, skill_script_id
from scaffold_context import ScaffoldContext

CAPTURE_SCRIPT = "task/scripts/statusline_capture.py"

# Machine-local, and deliberately not the committed settings.json: the delegate is a path
# personal to one machine, while the wired command must stay portable across checkouts.
DELEGATE_RECORD = Path(".ai-badger") / "task-tracking" / "statusline-delegate.json"


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

        record_path = self.ctx.target / DELEGATE_RECORD
        record, record_note = cg.read_json_mapping(record_path)
        if record is None:
            self.ctx.notes.append(f"{record_note} (statusline capture left wired — the renderer "
                              "it displaced cannot be read back)")
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

    def _record_delegate(self, command: Optional[str]) -> None:
        """Record the renderer the wrapper delegates to; ``None`` means capture only."""
        cg.write_json_with_backup(self.ctx.target / DELEGATE_RECORD, {"command": command})
