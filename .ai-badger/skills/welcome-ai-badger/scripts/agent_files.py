"""Agent file scaffolding, one of the scaffold's collaborators.

Applies scaffolding.json to write agent discovery files (CLAUDE.md, copilot,
hermes, .github/instructions/*) based on each agent's feature directory.
"""
from __future__ import annotations

import shutil
from pathlib import Path
from typing import Dict, List

from scaffold_context import ScaffoldContext
from template_rendering import TemplateRendering


# Known non-standard agent file locations that may coexist with the standard ones.
_NONSTANDARD_AGENT_FILES: Dict[str, List[str]] = {
    ".github/copilot-instructions.md": ["COPILOT_INSTRUCTIONS.md"],
}


class AgentFiles:
    """Writes each configured agent's discovery files from its scaffolding.json."""

    def __init__(self, ctx: ScaffoldContext, rendering: TemplateRendering):
        self.ctx = ctx
        self.rendering = rendering

    def _apply_scaffolding(self, agent_name: str, instructions_doc: str,  # pylint: disable=too-many-statements
                            instr_paths: List[Path], invariants: List[str]) -> None:
        """Apply features/<agent>/scaffolding.json to write agent files."""
        import badger_lib as bl

        scaffolding_path = self.ctx.root / "features" / agent_name / "scaffolding.json"
        if not scaffolding_path.is_file():
            self.ctx.notes.append(f"no scaffolding.json for agent '{agent_name}' — skipped")
            return

        scaffolding = bl.load_json(scaffolding_path)
        schema = bl.load_json(self.ctx.root / "schemas" / "scaffolding.schema.json")
        errors = bl.validate(scaffolding, schema)
        if errors:
            self.ctx.notes.append(
                f"scaffolding.json for '{agent_name}' is invalid — skipping: {errors}"
            )
            return

        feature_dir = self.ctx.root / "features" / agent_name
        for file_entry in scaffolding["files"]:
            source = feature_dir / file_entry["source"]
            target = self.ctx.target / file_entry["target"]
            managed = file_entry.get("managed", True)
            seed_once = file_entry.get("seedOnce", False)
            is_template = file_entry.get("template", False)
            also_target = file_entry.get("alsoTarget")
            aib_copy = file_entry.get("aibCopy")
            instructions_scoped = file_entry.get("instructionsScoped", False)

            if not source.exists():
                self.ctx.notes.append(
                    f"scaffolding source '{file_entry['source']}' for '{agent_name}' "
                    f"not found at {source} — skipping"
                )
                continue

            if file_entry["source"].startswith("templates/"):
                self.ctx.record_template(source, target)

            # Determine the body content
            source_of_truth = aib_copy or file_entry["target"]
            if is_template:
                body = self.rendering.render_template_file(source, instr_paths, invariants,
                                                  source_of_truth)
            else:
                body = source.read_text(encoding="utf-8")

            # Write source-of-truth copy under .ai-badger/. It carries preserved regions on the
            # same terms as the discovery copies — it is the file the project is told to edit.
            if aib_copy:
                aib_dest = self.ctx.aib / aib_copy
                carried = self.rendering.carried_body(aib_dest, body)
                if carried is not None:
                    aib_dest.parent.mkdir(parents=True, exist_ok=True)
                    aib_dest.write_text(carried, encoding="utf-8")

            # Seed-once: skip if target already exists
            if seed_once and target.exists():
                self.ctx.notes.append(
                    f"preserved seed-once {file_entry['target']} for '{agent_name}'"
                )
                continue

            # Write the primary target. The header must name the .ai-badger/ copy it is
            # generated from — its own target path is where edits get discarded (F-08).
            content = body
            if managed:
                self.rendering.copy_with_header(target, source_of_truth, content)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                if is_template:
                    target.write_text(content, encoding="utf-8")
                else:
                    shutil.copyfile(source, target)

            # Write alsoTarget (e.g. .hermes.md alias)
            if also_target:
                also_dest = self.ctx.target / also_target
                if managed:
                    self.rendering.copy_with_header(also_dest, source_of_truth, content)
                else:
                    also_dest.parent.mkdir(parents=True, exist_ok=True)
                    if is_template:
                        also_dest.write_text(content, encoding="utf-8")
                    else:
                        shutil.copyfile(source, also_dest)

            # Write per-instruction scoped copies (copilot's .github/instructions/)
            if instructions_scoped:
                for p in instr_paths:
                    self.rendering.copy_with_header(
                        self.ctx.target / ".github" / "instructions" / p.name,
                        f"instructions/{p.name}",
                        p.read_text(encoding="utf-8")
                    )

            self._detect_nonstandard_agent_files(file_entry["target"])

    def _detect_nonstandard_agent_files(self, target_rel: str) -> None:
        """Warn if non-standard equivalents of an agent file exist in the project."""
        for alt in _NONSTANDARD_AGENT_FILES.get(target_rel, []):
            alt_path = self.ctx.target / alt
            if alt_path.exists():
                self.ctx.notes.append(
                    f"non-standard agent file '{alt}' found — '{target_rel}' was also "
                    "written; consider removing the non-standard file to avoid confusion"
                )

    def write_agent_files(self, instructions_doc: str, instr_paths: List[Path],
                           invariants: List[str]) -> None:
        """Write agent discovery files using scaffolding.json from each agent's feature dir.

        Each agent in config.agents must have a features/<agent>/scaffolding.json that
        declares what files to write. No hardcoded fallback — all agents are data-driven.
        """
        agents = self.ctx.config.get("agents", [])
        for agent_name in agents:
            self._apply_scaffolding(agent_name, instructions_doc, instr_paths, invariants)
