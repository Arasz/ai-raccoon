"""Template rendering, one of the scaffold's collaborators.

Computes document slots for CLAUDE.md/HERMES.md/delegation.md templates, renders
template files, and assembles agent discovery documents.
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import Dict, List, Optional, Sequence

from _shared import MANAGED_HEADER, _MANAGED_PREFIX
from scaffold_context import ScaffoldContext

# A frontmatter key: at the start of its line, so an indented continuation is not one.
_KEY_RE = re.compile(r"([A-Za-z][A-Za-z0-9_-]*):")

# The lane a persona that names no `model:` runs in — whatever model the session already uses.
SESSION_DEFAULT_LANE = "session default"

DELEGATION_TEMPLATE = "delegation.md.tmpl"


def frontmatter_fields(text: str) -> Dict[str, str]:
    """Each top-level frontmatter key mapped to its value, a folded block joined into one line.

    Line-based rather than YAML-parsed: pyyaml is optional here (ADR-0002).
    """
    if not text.startswith("---\n"):
        return {}
    block = text[4:].split("\n---", 1)[0]
    fields: Dict[str, str] = {}
    key = None
    for line in block.splitlines():
        match = _KEY_RE.match(line)
        if match:
            key = match.group(1)
            fields[key] = line[match.end():].strip().lstrip(">|").strip()
        elif key and line.strip():
            fields[key] = (fields[key] + " " + line.strip()).strip()
    return fields


def first_sentence(text: str) -> str:
    """The first sentence of *text*, markdown emphasis and the closing full stop removed."""
    plain = text.replace("*", "").replace("`", "").strip()
    return plain.split(". ", 1)[0].rstrip(".").strip()


def prose_only(text: str) -> str:
    """*text* with its headings and HTML comments dropped, the rest joined into one line."""
    kept = [line.strip() for line in text.splitlines()
            if line.strip() and not line.lstrip().startswith(("#", "<!--"))]
    return " ".join(kept)


# How far into a file the managed marker may sit: line 1, or just below a frontmatter block.
# Bounded so a document that merely quotes the banner deep in its body is not read as managed.
_MANAGED_SCAN_LINES = 10


def _frontmatter_end(body: str) -> int:
    """Offset just past the closing fence of `body`'s frontmatter, or -1 when it has none.

    Only a fence at offset 0 counts — a `---` further down is a thematic break or code (#241).
    """
    for newline in ("\r\n", "\n"):
        fence = "---" + newline
        if not body.startswith(fence):
            continue
        end = body.find(newline + fence, len(fence) - len(newline))
        return -1 if end == -1 else end + len(newline) + len(fence)
    return -1


def _with_managed_header(body: str, name: str) -> str:
    """`body` with the managed banner on line 1, or below its frontmatter when it opens with one.

    VS Code and Copilot parse an instruction file's frontmatter only at line 1 (#241).
    """
    header = MANAGED_HEADER.format(name=name)
    close = _frontmatter_end(body)
    if close == -1:
        return header + body
    newline = "\r\n" if body.startswith("---\r\n") else "\n"
    return body[:close] + newline + header + body[close:]


def _is_managed(text: str) -> bool:
    """True when `text` carries the managed marker at line 1 or just below its frontmatter."""
    head = text.lstrip().splitlines()[:_MANAGED_SCAN_LINES]
    return any(line.startswith(_MANAGED_PREFIX) for line in head)


class TemplateRendering:
    """Computes the document slots and renders the templates every agent file is made of."""

    def __init__(self, ctx: ScaffoldContext):
        self.ctx = ctx

    @staticmethod
    def _render_instruction_blocks(entries: list) -> str:
        """Join each entry's `instructions` into one slot value, or '' when none has any."""
        blocks = [instr for instr in
                  ((e.get("instructions") or "").strip() for e in entries) if instr]
        return "\n\n".join(blocks) + "\n\n" if blocks else ""

    def compute_doc_slots(self, invariants: List[str], instr_paths: List[Path],
                            source_of_truth: str = "CLAUDE.md") -> Dict[str, str]:
        """Compute the template slots shared by CLAUDE.md and HERMES.md assembly.

        `source_of_truth` is the .ai-badger/ file this render is the copy of — every agent
        renders the same template, so the self-reference cannot be hardcoded (F-08).
        """
        project = self.ctx.config.get("project", {})
        commands = self.ctx.config.get("commands", {})
        routing = self.ctx.config.get("personaRouting", [])

        inv_md = "\n\n".join(invariants) if invariants else "_None yet._"
        cmd_md = "\n".join(f"- `{k}`: `{v}`" for k, v in commands.items()) or "_None configured._"
        route_md = "\n".join(f"- {r['work']} → `{r['agent']}`" for r in routing) or (
            "_None configured — work is not dispatched to a persona. Add entries to "
            "`personaRouting` in `.ai-badger/config.json` to route it._"
        )
        instr_md = "\n".join(
            f"- `{p.name}` → `.ai-badger/instructions/{p.name}`" for p in instr_paths
        ) or "_None._"
        return {
            "PROJECT_NAME": project.get("name", ""),
            "PROJECT_SUMMARY": project.get("summary", ""),
            "PROJECT_DOMAIN": project.get("domain", ""),
            "STACKS": ", ".join(self.ctx.config.get("stacks", [])),
            "INVARIANTS": inv_md,
            "COMMANDS": cmd_md,
            "PERSONA_ROUTING": route_md,
            "PATH_INSTRUCTIONS": instr_md,
            "MCP_INSTRUCTIONS": self._render_instruction_blocks(self.ctx.mcp_described),
            "FRAMEWORK_VERSION": self.ctx.index["frameworkVersion"],
            "SOURCE_OF_TRUTH": source_of_truth,
        }

    def _render_template(self, tmpl_name: str, slots: Dict[str, str]) -> str:
        """Render a template file from features/common/templates/ with the given slots."""
        tmpl_path = self.ctx.root / "features" / "common" / "templates" / tmpl_name
        if tmpl_path.exists():
            doc = tmpl_path.read_text(encoding="utf-8")
            for k, v in slots.items():
                doc = doc.replace("{{" + k + "}}", str(v))
            return doc
        # fallback minimal doc if template missing
        return (f"# {slots['PROJECT_NAME']}\n\n{slots['PROJECT_SUMMARY']}\n\n"
                f"## Invariants\n\n{slots['INVARIANTS']}\n\n## Commands\n\n{slots['COMMANDS']}\n")

    def assemble_instructions_doc(self, invariants: List[str], instr_paths: List[Path]) -> str:
        """Render the CLAUDE.md.tmpl template with this config's project/commands/invariants."""
        return self._render_template("CLAUDE.md.tmpl",
                                     self.compute_doc_slots(invariants, instr_paths))

    def assemble_hermes_doc(self, invariants: List[str], instr_paths: List[Path]) -> str:
        """Render the HERMES.md.tmpl template with this config's project/commands/invariants."""
        return self._render_template(
            "HERMES.md.tmpl",
            self.compute_doc_slots(invariants, instr_paths, source_of_truth="HERMES.md"))

    # -- the delegation map ---------------------------------------------------------

    def _persona_lines(self) -> str:
        """One line per persona in `.ai-badger/agents/`: what it is for, and the lane it runs in.

        Read from the scaffolded copies rather than the catalog, so a persona this project
        added by hand is on the map too.
        """
        lines: List[str] = []
        for path in sorted((self.ctx.aib / "agents").glob("*.md")):
            fields = frontmatter_fields(path.read_text(encoding="utf-8", errors="ignore"))
            name = fields.get("name") or path.stem
            summary = first_sentence(fields.get("description", ""))
            lane = fields.get("model") or SESSION_DEFAULT_LANE
            lines.append(f"- `{name}`" + (f" — {summary}." if summary else " —")
                         + f" Lane: {lane}.")
        return "\n".join(lines) or "_None scaffolded._"

    def _mcp_server_lines(self, names: Sequence[str]) -> str:
        """One line per named server, blurbed from the `server.md` its catalog entry ships."""
        blurbs = {entry.get("name"): first_sentence(prose_only(entry.get("instructions") or ""))
                  for entry in self.ctx.mcp_described}
        lines = [f"- `{name}`" + (f" — {blurbs.get(name)}" if blurbs.get(name) else "")
                 for name in sorted(names)]
        return "\n".join(lines) or "_None indexed._"

    def write_delegation_map(self, invariants: List[str], instr_paths: List[Path],
                               mcp_servers: Sequence[str]) -> None:
        """Write `.ai-badger/delegation.md`: who this project can delegate to, and through what.

        Called once the personas are scaffolded — their frontmatter is what names each lane.
        """
        src = self.ctx.root / "features" / "common" / "templates" / DELEGATION_TEMPLATE
        if not src.is_file():
            self.ctx.notes.append(f"common/templates/{DELEGATION_TEMPLATE} missing — skipped")
            return
        slots = self.compute_doc_slots(invariants, instr_paths, source_of_truth="delegation.md")
        slots["PERSONAS"] = self._persona_lines()
        slots["MCP_SERVERS"] = self._mcp_server_lines(mcp_servers)
        dest = self.ctx.aib / "delegation.md"
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(self._render_template(DELEGATION_TEMPLATE, slots), encoding="utf-8")
        self.ctx.record_template(src, dest)

    # -- agent-discovery copies -----------------------------------------------------

    def render_template_file(self, source: Path, instr_paths: List[Path],
                               invariants: List[str], source_of_truth: str = "CLAUDE.md") -> str:
        """Render a .tmpl file with the standard scaffold slots."""
        tmpl = source.read_text(encoding="utf-8")
        slots = self.compute_doc_slots(invariants, instr_paths, source_of_truth)
        for k, v in slots.items():
            tmpl = tmpl.replace("{{" + k + "}}", str(v))
        return tmpl

    def carried_body(self, dest: Path, body: str) -> Optional[str]:
        """`body` plus dest's preserved regions, or None when dest's markers are malformed."""
        from _shared import carry_keep_regions, MalformedKeepRegion  # pylint: disable=import-outside-toplevel

        if not dest.exists():
            return body
        rel = dest.relative_to(self.ctx.target).as_posix()
        try:
            carried = carry_keep_regions(dest.read_text(encoding="utf-8", errors="ignore"), body)
        except MalformedKeepRegion as exc:
            self.ctx.notes.append(
                f"{rel} left untouched — {exc}; fix the markers and re-run to refresh it"
            )
            return None
        if carried != body:
            self.ctx.notes.append(f"carried preserved regions into {rel}")
        return carried

    def copy_with_header(self, dest: Path, name: str, body: str) -> None:
        """Write body to dest with managed header, preserving hand-authored files."""
        if (not self.ctx.overwrite and dest.exists()
                and not _is_managed(dest.read_text(encoding="utf-8", errors="ignore"))):
            self.ctx.notes.append(
                f"preserved hand-authored {dest.relative_to(self.ctx.target).as_posix()} "
                "(source written to .ai-badger/; pass --overwrite-agent-files to replace)"
            )
            return
        carried = self.carried_body(dest, body)
        if carried is None:
            return
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(_with_managed_header(carried, name), encoding="utf-8")
