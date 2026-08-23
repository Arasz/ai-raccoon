"""Extension management, one of the scaffold's collaborators.

Parses, merges, and prunes skill extensions shipped at <skill>/extensions/<name>/ as they
land in .ai-badger/skills/. See ADR-0006 for why this is the only mechanism.
"""
from __future__ import annotations

import re
import shutil
from collections import defaultdict
from pathlib import Path

from scaffold_context import ScaffoldContext


_SECTION_RE = re.compile(r'^##\s+(.+)$')
_AT_MARKER_RE = re.compile(r'^@([a-z][a-z0-9-]*):\s*(.*)$')
_EXT_MARKER_RE = re.compile(r'<!--\s*EXT:([a-z][a-z0-9-]*)\s*-->')


class Extensions:
    """Parses, merges and prunes the extensions shipped inside a scaffolded skill."""

    def __init__(self, ctx: ScaffoldContext):
        self.ctx = ctx

    @staticmethod
    def _parse_extension_sections(ext_md: str):
        """Split an extension.md into sections, each targeting a marker or the append bucket.

        Returns list of {"marker": "name" | None, "header": "## Title", "body": "..."}.
        A header like "## @pre-takeoff: Title" targets the "pre-takeoff" marker.
        A plain "## Title" targets the append bucket.
        """
        sections = []
        current = None
        for line in ext_md.splitlines(keepends=True):
            m = _SECTION_RE.match(line.rstrip())
            if m and line.startswith('## '):
                raw_header = m.group(1)
                at_match = _AT_MARKER_RE.match(raw_header)
                if at_match:
                    marker = at_match.group(1)
                    title = at_match.group(2).strip()
                    header = f"## {title}"
                else:
                    marker = None
                    header = line.rstrip()
                if current is not None:
                    sections.append(current)
                current = {"marker": marker, "header": header, "body": ""}
                continue
            if current is not None:
                current["body"] += line
        if current is not None:
            sections.append(current)
        return sections

    def _collect_marker_sections(self, skill_dest):
        """Read all extension.md files and group their sections by target marker.

        Returns (by_marker: dict[str, list[str]], append_sections: list[str]).
        """
        by_marker = defaultdict(list)
        append_sections = []
        ext_base = skill_dest / "extensions"
        if not ext_base.is_dir():
            return dict(by_marker), append_sections
        for ext_dir in sorted(ext_base.iterdir()):
            if not ext_dir.is_dir():
                continue
            ext_md_path = ext_dir / "extension.md"
            if not ext_md_path.exists():
                continue
            text = ext_md_path.read_text(encoding="utf-8").strip()
            if not text:
                continue
            for sec in self._parse_extension_sections(text):
                rendered = sec["header"] + "\n" + sec["body"]
                rendered = rendered.strip()
                if not rendered:
                    continue
                if sec["marker"]:
                    by_marker[sec["marker"]].append(rendered)
                else:
                    append_sections.append(rendered)
        return dict(by_marker), append_sections

    def merge_extensions(self, skill_name: str, dest: Path) -> None:
        """Route extension sections into SKILL.md at <!-- EXT:name --> markers.

        Only activates when SKILL.md contains the MERGE_EXTENSIONS sentinel.
        Extension sections with @marker-name headers are inserted at the matching
        <!-- EXT:name --> position. Sections without @marker are appended at the end.
        Extensions/ directory is removed after merge.
        """
        ext_base = dest / "extensions"
        if not ext_base.is_dir():
            return
        skill_md = dest / "SKILL.md"
        if not skill_md.exists():
            return
        content = skill_md.read_text(encoding="utf-8")
        if "<!-- MERGE_EXTENSIONS -->" not in content:
            return
        content = content.replace("<!-- MERGE_EXTENSIONS -->\n", "")
        content = content.replace("<!-- MERGE_EXTENSIONS -->", "")

        by_marker, append_sections = self._collect_marker_sections(dest)

        # Insert at each marker position
        ext_count = 0
        for marker_name, sections in by_marker.items():
            marker_tag = f"<!-- EXT:{marker_name} -->"
            if marker_tag not in content:
                self.ctx.notes.append(
                    f"extension targets marker '{marker_name}' but SKILL.md has no "
                    f"<!-- EXT:{marker_name} --> — sections skipped"
                )
                continue
            insertion = "\n\n" + "\n\n".join(sections)
            content = content.replace(marker_tag, marker_tag + insertion)
            ext_count += len(sections)

        # Append untargeted sections at the end
        if append_sections:
            content = content.rstrip() + "\n\n" + "\n\n".join(append_sections) + "\n"
            ext_count += len(append_sections)

        # Remove EXT markers from output
        content = re.sub(r'\n?<!-- EXT:[a-z][a-z0-9-]* -->\n?', '', content)

        if ext_count:
            skill_md.write_text(content, encoding="utf-8")
            self.ctx.notes.append(
                f"merged {ext_count} extension section(s) into "
                f".ai-badger/skills/{skill_name}/SKILL.md"
            )
        # Remove extensions/ dir — content is now in SKILL.md
        shutil.rmtree(ext_base)

    def append_project_local(self, skill_name: str, dest: Path) -> None:
        """If project-local.md exists in the scaffolded skill dir, append its content to SKILL.md.

        This lets projects add project-specific checks (incident lessons, project conventions)
        that survive re-scaffolds while the framework content stays fresh.
        """
        pl = dest / "project-local.md"
        if not pl.exists():
            return
        skill_md = dest / "SKILL.md"
        if not skill_md.exists():
            return
        additions = pl.read_text(encoding="utf-8").strip()
        if not additions:
            return
        existing = skill_md.read_text(encoding="utf-8")
        skill_md.write_text(existing.rstrip() + "\n\n" + additions + "\n", encoding="utf-8")
        self.ctx.notes.append(
            f"appended project-local.md to .ai-badger/skills/{skill_name}/SKILL.md"
        )

    def prune_inline_extensions(self, skill_name: str, dest: Path) -> None:
        """Remove extensions shipped inside the skill directory whose requires aren't met.

        Extensions stored at <skill>/extensions/<ext>/ are copied by copytree before their
        activation conditions are checked.  This prunes any whose extension.json declares
        unmet requires, keeping the scaffolded output config-gated.
        """
        import badger_lib as bl
        from _shared import requirement_met  # pylint: disable=import-outside-toplevel

        ext_base = dest / "extensions"
        if not ext_base.is_dir():
            return
        for ext_dir in sorted(ext_base.iterdir()):
            if not ext_dir.is_dir():
                continue
            descriptor = ext_dir / "extension.json"
            if not descriptor.exists():
                continue
            reqs = bl.load_json(descriptor).get("requires", [])
            if all(requirement_met(self.ctx.config, r) for r in reqs):
                self.ctx.notes.append(
                    f"embedded extension '{ext_dir.name}' into skill "
                    f"'{skill_name}' (requirements met)"
                )
            else:
                shutil.rmtree(ext_dir)
                self.ctx.notes.append(
                    f"extension '{ext_dir.name}' for '{skill_name}' "
                    "skipped (config requirements not met)"
                )
