"""The --skills argv contract, one of the scaffold's collaborators.

Resolves the requested skill list: names the framework catalog does not know and the target
manifest does not record are refused, and so are quoting artifacts (D3, task
aib-scaffold-freshness-guard-blindspot-proof — a non-shell transport of the printed
`--skills ''` advice delivers the literal two-character name, which used to bypass recovery
and under-deliver silently, the ea17ae60 shape). A true-empty value keeps its #129 meaning:
recover the previously scaffolded set from the target's manifest.
"""
from __future__ import annotations

from pathlib import Path
from typing import List, Optional, Tuple


def resolve_requested_skills(root: Path, target: Path,
                             argv_value: str) -> Tuple[List[str], List[str], Optional[str]]:
    """The delivered skill list for one --skills value, its notes, and any argv refusal.

    Three outcomes, decided in this order. Quoting artifacts — quote characters or untrimmed
    whitespace are transport debris, never skill names — refuse outright. So do names neither
    the framework catalog nor the target manifest knows: a typo, or a gateway member the
    catalog absorbed (the hint names the gateway). A name the manifest still records is
    allowed even when the catalog dropped it: that is the catalog-drop flow.

    A true-empty value (nothing between the commas) means "unchanged", not "none" (#129): the
    previously scaffolded set is recovered from the target manifest. A fresh target has no
    manifest to recover from and scaffolds no skills — nothing to destroy. A manifest that
    cannot be parsed is reported, never read as "reused 0 skill(s)".

    Returns `(skills, notes, rejection)`: a non-empty rejection is rendered and caller prints
    it and exits 2; otherwise notes ride the scaffold's own note list.
    """
    skills = [s for s in argv_value.split(",") if s]
    cli_notes: List[str] = []
    artifacts = [s for s in skills if s != s.strip() or any(c in s for c in "\"'\\")]
    if artifacts:
        lines = ["SCAFFOLD ARGV INVALID — refusing --skills value(s): quote characters and "
                 "untrimmed whitespace are transport artifacts, not skill names."]
        lines += [f"    {name!r}" for name in artifacts]
        lines.append('    (to re-scaffold the previously scaffolded set, pass a true-empty '
                     'value through a shell: --skills "" )')
        return skills, cli_notes, "\n".join(lines) + "\n"
    if not skills:
        import badger_lib as bl  # pylint: disable=import-outside-toplevel

        manifest_path = target / ".ai-badger" / "manifest.json"
        if manifest_path.is_file():
            try:
                skills = bl.scaffolded_skill_names(bl.load_json(manifest_path))
                cli_notes.append(
                    f"--skills was empty — reused {len(skills)} skill(s) already scaffolded, "
                    f"from the manifest at {manifest_path}"
                )
            except (ValueError, OSError) as exc:
                skills = []
                cli_notes.append(f"--skills empty, manifest at {manifest_path} could not be read ({exc})")
        return skills, cli_notes, None

    import badger_lib as bl  # pylint: disable=import-outside-toplevel

    catalog = {d.name for stack_dir in root.glob("features/*/skills")
               for d in stack_dir.iterdir() if d.is_dir() and (d / "SKILL.md").is_file()}
    aliases = bl.gateway_aliases(root)
    manifested: List[str] = []
    manifest_path = target / ".ai-badger" / "manifest.json"
    if manifest_path.is_file():
        try:
            manifested = bl.scaffolded_skill_names(bl.load_json(manifest_path))
        except (ValueError, OSError):
            manifested = []
    unknown = [s for s in skills if s not in catalog and s not in manifested]
    if unknown:
        lines = ["SCAFFOLD ARGV INVALID — refusing --skills name(s) the framework catalog "
                 "does not ship and the target manifest does not record:"]
        for name in unknown:
            hint = f" — a gateway absorbed it; use '{aliases[name]}'" if name in aliases else ""
            lines.append(f"    {name!r}{hint}")
        return skills, cli_notes, "\n".join(lines) + "\n"
    return skills, cli_notes, None
