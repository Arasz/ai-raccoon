"""Hook wiring, one of the scaffold's collaborators.

Reads hooks-manifest.json, generates project-specific hooks.json under
.ai-badger/hooks/, and merges hook registrations into .claude/settings.json.
"""
from __future__ import annotations

import re

from typing import Any, Dict, List, Optional, Set

from scaffold_context import ScaffoldContext

# Hook commands must name their script through this placeholder, never an absolute path:
# every checkout of a project (worktree, second clone, framework cache) spells the same
# script differently, and a dedupe keyed on the literal command never recognises them.
PROJECT_DIR_VAR = "${CLAUDE_PROJECT_DIR}"

# The layouts an ai-badger hook script can be reached through; the text after the marker
# (skill/scripts/hook.py) is the script's checkout-independent identity.
_SKILL_PATH_MARKERS = (
    "/.ai-badger/skills/",
    "/features/common/skills/",
    "/features/claude/skills/",
)


def skill_script_id(command: str) -> Optional[str]:
    """The skill-relative script path a command runs, or None if it is not ours.

    First occurrence, cut at the first closer: a guarded command repeats the path — and its
    skip message quotes it in prose — so the last occurrence is not reliably a clean path.
    """
    for marker in _SKILL_PATH_MARKERS:
        idx = command.find(marker)
        if idx == -1:
            continue
        rest = command[idx + len(marker):]
        for closer in ('"', "'", " "):
            rest = rest.split(closer, 1)[0]
        return rest or None
    return None


def project_script(command: str) -> Optional[str]:
    """The project-relative script a rewritten command runs, or None if it names none."""
    marker = PROJECT_DIR_VAR + "/"
    idx = command.rfind(marker)
    if idx == -1:
        return None
    rest = command[idx + len(marker):]
    for closer in ('"', "'", " "):
        rest = rest.split(closer, 1)[0]
    return rest or None


# The only characters a scaffolded script path may carry into a shell string (F4).
_SHELL_SAFE_PATH = re.compile(r"[\w./-]+")


def guarded(command: str) -> str:
    """`command`, still able to run — or saying it skipped — when its script is missing.

    In a git worktree session ${CLAUDE_PROJECT_DIR} resolves to the main checkout, which may
    not carry the script; a missing file BLOCKS the call a PreToolUse hook gates. Fall back
    to the session cwd's copy, and report the skip rather than going silently inert (F1).
    """
    script = project_script(command)
    if not script or not _SHELL_SAFE_PATH.fullmatch(script):
        return command
    relative = command.replace(f"{PROJECT_DIR_VAR}/", "")
    return (f'if [ -f "{PROJECT_DIR_VAR}/{script}" ]; then {command}; '
            f'elif [ -f "{script}" ]; then {relative}; '
            f'else echo \'{{"systemMessage": "ai-badger: {script} not found - hook skipped"}}\'; '
            f'fi')


def declined_skill(command: str, declined: Set[str]) -> Optional[str]:
    """The declined skill a framework hook command belongs to, or None."""
    script_id = skill_script_id(command)
    if not script_id:
        return None
    name = script_id.split("/")[0]
    return name if name in declined else None


def drop_declined(existing_hooks: Dict[str, Any], declined: Set[str]) -> List[str]:
    """Remove framework hooks belonging to a declined skill; return the script ids dropped.

    A hook wired by an earlier run outlives the skill's discovery link — settings.json is
    merged into, not rewritten — so declining a skill has to reach in here as well.
    """
    dropped: List[str] = []
    for event, event_hooks in list(existing_hooks.items()):
        kept_entries = []
        for entry in event_hooks:
            hooks = entry.get("hooks", [])
            kept = []
            for hook in hooks:
                command = hook.get("command", "")
                if declined_skill(command, declined):
                    dropped.append(skill_script_id(command))
                else:
                    kept.append(hook)
            if not kept and hooks:
                continue
            pruned = dict(entry)
            pruned["hooks"] = kept
            kept_entries.append(pruned)
        existing_hooks[event] = kept_entries
    return dropped


def _hook_key(command: str) -> str:
    """Dedupe key: script identity for framework hooks, the literal command otherwise."""
    return skill_script_id(command) or command


def _prune(event_hooks: List[Any], superseded: set) -> List[Any]:
    """Drop framework hooks that an incoming command replaces, and their own repeats.

    Hooks the framework does not own are copied through untouched.
    """
    kept_entries = []
    seen = set()
    for entry in event_hooks:
        hooks = entry.get("hooks", [])
        kept = []
        for hook in hooks:
            script_id = skill_script_id(hook.get("command", ""))
            if script_id is None:
                kept.append(hook)
                continue
            if script_id in superseded or script_id in seen:
                continue
            seen.add(script_id)
            kept.append(hook)
        if not kept and hooks:
            continue
        pruned = dict(entry)
        pruned["hooks"] = kept
        kept_entries.append(pruned)
    return kept_entries


def select_hooks(source_event_hooks, script):
    """Commands belonging to *script* — never a whole event's worth for an unnamed one.

    A manifest entry naming no script cannot tell which registered command is its own, so
    it gets none here and resolves its own script by convention instead (see `wire()`).
    """
    if not script:
        return []
    selected = []
    for entry in source_event_hooks:
        matching = [h for h in entry.get("hooks", [])
                    if h.get("command", "").rstrip('"').endswith(script)]
        if matching:
            picked = dict(entry)
            picked["hooks"] = matching
            selected.append(picked)
    return selected


def merge_hooks(existing_hooks: Dict[str, Any], new_hooks: Dict[str, Any]) -> None:
    """Merge *new_hooks* into *existing_hooks* in-place, deduplicating by script identity.

    A framework hook is identified by the skill-relative script it runs, so the same
    script wired from another checkout collapses instead of accumulating; anything else
    is matched on its literal command and left alone.
    """
    for event, hook_entries in new_hooks.items():
        superseded = {
            script_id
            for entry in hook_entries
            for script_id in [skill_script_id(h.get("command", ""))
                              for h in entry.get("hooks", [])]
            if script_id is not None
        }
        existing_event_hooks = _prune(existing_hooks.get(event, []), superseded)
        registered_cmds = {
            _hook_key(h.get("command", ""))
            for entry in existing_event_hooks
            for h in entry.get("hooks", [])
        }
        for new_entry in hook_entries:
            new_hs = [
                h for h in new_entry.get("hooks", [])
                if _hook_key(h.get("command", "")) not in registered_cmds
            ]
            if new_hs:
                filtered = dict(new_entry)
                filtered["hooks"] = new_hs
                existing_event_hooks.append(filtered)
                registered_cmds.update(_hook_key(h.get("command", "")) for h in new_hs)
        existing_hooks[event] = existing_event_hooks


class HookWiring:
    """Generates .ai-badger/hooks/hooks.json and merges its registrations into settings.json."""

    def __init__(self, ctx: ScaffoldContext):
        self.ctx = ctx

    def wire(self) -> None:
        """Wire framework hooks into .claude/settings.json for Claude Code projects.

        Reads hooks-manifest.json, generates a project-specific hooks.json under
        .ai-badger/hooks/ with paths rewritten to the scaffolded .ai-badger/skills/
        directory, and merges hook registrations into .claude/settings.json.
        """
        import badger_lib as bl
        import config_guard as cg

        if "claude" not in self.ctx.config.get("agents", []):
            return

        manifest_path = self.ctx.root / "features" / "common" / "hooks" / "hooks-manifest.json"
        if not manifest_path.exists():
            return

        try:
            manifest = bl.load_json(manifest_path)
        except (ValueError, OSError):
            return

        # Source hooks.json (framework plugin version)
        source_hooks_path = self.ctx.root / "features" / "common" / "hooks" / "hooks.json"
        source_hooks = {}
        if source_hooks_path.exists():
            try:
                source_hooks = bl.load_json(source_hooks_path)
            except (ValueError, OSError):
                pass

        # Build the target hooks.json by rewriting paths
        target_hooks: Dict[str, Any] = {"hooks": {}}
        settings_hooks: Dict[str, Any] = {}

        for hook in manifest.get("hooks", []):
            claude_entry = hook.get("agents", {}).get("claude")
            # "plugin-hooks-json" hooks are registered by the plugin's own hooks/hooks.json
            # and rely on ${CLAUDE_PLUGIN_ROOT}, which is never set for a hook a consumer
            # registers itself — wiring them here would only look like the feature works.
            if not claude_entry or claude_entry.get("type") != "hooks-json":
                continue

            event = claude_entry.get("event")
            if not event:
                continue

            # Get this hook's own command from the source hooks.json
            source_event_hooks = select_hooks(
                source_hooks.get("hooks", {}).get(event, []), claude_entry.get("script"))
            if not source_event_hooks:
                # Generate a default hook entry for skills not in the framework hooks.json
                hook_name = hook.get("name", "")
                if hook_name in self.ctx.excluded["skills"]:
                    self.ctx.notes.append(
                        f"hook '{hook_name}': skill '{hook_name}' is declined in "
                        f"config.exclude — not wired"
                    )
                    continue
                skill_hook_script = self.ctx.aib / "skills" / hook_name / "scripts"
                script_name = claude_entry.get("script")
                if script_name:
                    named = skill_hook_script / script_name
                    hook_scripts = [named] if named.is_file() else []
                else:
                    hook_scripts = (sorted(skill_hook_script.glob("*_hook.py"))
                                     if skill_hook_script.exists() else [])
                    if len(hook_scripts) > 1:
                        names = ", ".join(p.name for p in hook_scripts)
                        self.ctx.notes.append(
                            f"hook '{hook_name}': manifest names no script and "
                            f"{skill_hook_script} has more than one candidate ({names}) — "
                            f"refusing to guess, not wired"
                        )
                        continue
                if not hook_scripts:
                    self.ctx.notes.append(f"hook '{hook_name}': no hook script found — skipped")
                    continue
                script_path = hook_scripts[0]
                rel_path = script_path.relative_to(self.ctx.target).as_posix()
                rewritten = [{
                    "hooks": [{
                        "type": "command",
                        "command": guarded(f'python3 "{PROJECT_DIR_VAR}/{rel_path}"')
                    }]
                }]
            else:
                # Rewrite paths from framework to scaffolded project
                aib_rel = self.ctx.aib.relative_to(self.ctx.target).as_posix()
                rewritten = []
                for entry in source_event_hooks:
                    new_entry = dict(entry)
                    new_hooks_list = []
                    for h in entry.get("hooks", []):
                        new_h = dict(h)
                        cmd = h.get("command", "")
                        cmd = cmd.replace(
                            "${CLAUDE_PLUGIN_ROOT}/features/common/skills/",
                            f"{PROJECT_DIR_VAR}/{aib_rel}/skills/"
                        )
                        # A rewritten path is only a claim about the project: a declined
                        # skill's copy may still sit on disk, and a partial scaffold leaves
                        # the script absent. Wiring either is worse than wiring nothing.
                        skipped = declined_skill(cmd, self.ctx.excluded["skills"])
                        if skipped:
                            self.ctx.notes.append(
                                f"hook '{hook.get('name', '')}': skill '{skipped}' is "
                                f"declined in config.exclude — not wired"
                            )
                            continue
                        script = project_script(cmd)
                        if script and not (self.ctx.target / script).is_file():
                            self.ctx.notes.append(
                                f"hook '{hook.get('name', '')}': {script} is not scaffolded "
                                f"— skipped"
                            )
                            continue
                        new_h["command"] = guarded(cmd)
                        new_hooks_list.append(new_h)
                    if not new_hooks_list:
                        continue
                    new_entry["hooks"] = new_hooks_list
                    rewritten.append(new_entry)
                if not rewritten:
                    continue

            # Accumulate: two manifest entries may register for the same event.
            target_hooks["hooks"].setdefault(event, []).extend(rewritten)
            settings_hooks.setdefault(event, []).extend(rewritten)

        settings_path = self.ctx.target / ".claude" / "settings.json"
        # Nothing to wire and nothing a previous run could have wired for a declined skill.
        if not target_hooks["hooks"] and not (
                self.ctx.excluded["skills"] and settings_path.exists()):
            return

        if target_hooks["hooks"]:
            hooks_dir = self.ctx.aib / "hooks"
            hooks_dir.mkdir(parents=True, exist_ok=True)
            bl.dump_json(hooks_dir / "hooks.json", target_hooks)

        # Merge into .claude/settings.json — never over an unreadable file
        settings, note = cg.read_json_mapping(settings_path)
        if settings is None:
            self.ctx.notes.append(f"{note} (hooks not wired)")
            return

        existing_hooks = settings.get("hooks", {})
        unwired = drop_declined(existing_hooks, self.ctx.excluded["skills"])
        merge_hooks(existing_hooks, settings_hooks)

        settings["hooks"] = existing_hooks
        cg.write_json_with_backup(settings_path, settings)
        self.ctx.record_generated_config(settings_path, ".claude/settings.json")
        for script_id in unwired:
            self.ctx.notes.append(
                f"unwired {script_id} from .claude/settings.json — declined in config.exclude"
            )
        if settings_hooks:
            self.ctx.notes.append(f"wired {len(settings_hooks)} hook(s) into .claude/settings.json")
