"""The .ai-badger/skills/ tree, one of the scaffold's collaborators.

Copies every requested skill directory in with its extensions, keeps the project-owned files
already inside it (#15), and republishes the result in the Hermes namespace (ADR-0003).
"""
from __future__ import annotations

import os
import shutil
from pathlib import Path
from typing import Any, Dict, List

from _shared import _test_ignore, PROJECT_LOCAL_FILE, _within
from scaffold_context import ScaffoldContext

# Files a skill ships once and the project owns thereafter (see #15), by skill name.
SEED_ONCE_SKILL_FILES: Dict[str, List[str]] = {
    "prompt-markers": ["markers-context.json"],
}


def project_owned_names(skill_name: str) -> List[str]:
    """Every name inside a delivered skill directory the project owns, not the framework.

    One list for both kinds: `project-local.md`, which any skill may carry and no catalog
    ships, and the per-skill seed-once files. The manifest records it so `generated_file_guard`
    stops refusing what the scaffold preserves and `scaffold_freshness_guard` accepts.
    """
    return [PROJECT_LOCAL_FILE] + SEED_ONCE_SKILL_FILES.get(skill_name, [])

# Hermes-authored skills live under one directory the namespace links whole.
LEARNED_SKILLS_DIR = "learned"
HERMES_HOME_ENV = "HERMES_HOME"


def hermes_skills_root() -> Path:
    """Hermes's skills root: $HERMES_HOME/skills when set, else ~/.hermes/skills.

    Same resolution `ai_badger_hooks.hermes_skills_root` uses, so a redirected Hermes home
    keeps the namespace and the hooks that read it pointing at one place.
    """
    override = os.environ.get(HERMES_HOME_ENV, "").strip()
    base = Path(override).expanduser() if override else Path.home() / ".hermes"
    return base / "skills"


def _owns_link(entry: Path, skills_root: Path) -> bool:
    """True if *entry* is a symlink resolving inside *skills_root* — i.e. ai-badger placed it."""
    if not entry.is_symlink():
        return False
    try:
        entry.resolve().relative_to(skills_root.resolve())
    except (ValueError, OSError):
        return False
    return True


def relink_hermes_skills(target: Path, config: Dict[str, Any],
                         skills: List[str]) -> Dict[str, List[str]]:
    """Rebuild ~/.hermes/skills/<project>/ so it links exactly *skills* plus learned/.

    Only symlinks resolving into <target>/.ai-badger/skills/ are removed; every other entry
    is left exactly as found (docs/adr/0003-hermes-skill-discovery-via-namespaced-symlinks.md).
    An empty *skills* is not evidence the project stopped wanting them (#129), so it leaves
    the namespace untouched. Returns {"created": [...], "removed": [...]}.
    """
    import badger_lib as bl

    project_name = config.get("project", {}).get("name", "unknown")
    skills_root = target / ".ai-badger" / "skills"
    hermes_skills = hermes_skills_root()
    namespace_dir = hermes_skills / project_name
    if not _within(hermes_skills, namespace_dir):
        raise ValueError(
            f"project name {project_name!r} does not resolve to a directory inside "
            f"{hermes_skills} — refusing to create it"
        )
    no_op: Dict[str, List[str]] = {"created": [], "removed": []}
    if namespace_dir.is_symlink() and not _owns_link(namespace_dir, skills_root):
        return no_op
    if not skills:
        return no_op

    # Declined skills are filtered here too: den-refresh re-links from the names on disk,
    # where an excluded skill's copy is deliberately left behind.
    declined = bl.exclusions(config)["skills"]
    wanted = [n for n in dict.fromkeys(skills)
              if n not in declined and (skills_root / n).is_dir()]
    if (skills_root / LEARNED_SKILLS_DIR).is_dir() and LEARNED_SKILLS_DIR not in wanted:
        wanted.append(LEARNED_SKILLS_DIR)

    removed: List[str] = []
    if namespace_dir.is_symlink():
        namespace_dir.unlink()
    elif namespace_dir.is_dir():
        for entry in sorted(namespace_dir.iterdir()):
            if _owns_link(entry, skills_root):
                if entry.name not in wanted:
                    removed.append(entry.name)
                entry.unlink()
    if not wanted:
        return {"created": [], "removed": removed}

    namespace_dir.mkdir(parents=True, exist_ok=True)
    # Resolve both ends before computing the relative link, so a symlinked home or project
    # path does not produce a link with the wrong number of `..` segments.
    link_base = namespace_dir.resolve()
    skills_base = skills_root.resolve()
    created: List[str] = []
    for name in wanted:
        link = namespace_dir / name
        if link.is_symlink():
            link.unlink()
        elif link.exists():
            continue  # foreign real entry — never clobber
        link.symlink_to(os.path.relpath(skills_base / name, link_base))
        created.append(name)
    return {"created": created, "removed": removed}


class SkillDelivery:
    """Fills .ai-badger/skills/ and points the agents that resolve skills elsewhere at it."""

    def __init__(self, ctx: ScaffoldContext, extensions):
        self.ctx = ctx
        self.extensions = extensions

    def discover_stack_local(self) -> None:
        """Add each configured stack's stack-local skills to the delivery list.

        The universal defaults arrive from the caller; a stack-local skill (auto-wm from
        claude) is discovered here, minus what config.exclude declines. The common catalog is
        skipped: its skills ship by their declared `scope:`, so walking it here would deliver
        every optIn skill to every project (ADR-0018).
        """
        import badger_lib as bl

        for stack in self.ctx.stacks:
            if stack in bl.DEFAULT_COMMON_STACKS:
                continue
            for name in bl.stack_local_skills(self.ctx.root / "features" / stack / "skills"):
                if name not in self.ctx.skills and name not in self.ctx.excluded["skills"]:
                    self.ctx.skills.append(name)

    # -- delivery --------------------------------------------------------------------
    def scaffold_skills(self) -> None:
        """Copy each requested skill directory into .ai-badger/skills/, with its extensions."""
        import badger_lib as bl

        for skill_name in self.ctx.skills:
            item, item_stack = bl.find_skill_in_stacks(self.ctx.index, self.ctx.stacks, skill_name)
            if item is None:
                self.ctx.notes.append(
                    f"skill '{skill_name}' not in any configured stack — skipped")
                continue
            src = self.ctx.root / item["path"]
            dest = self.ctx.aib / "skills" / skill_name
            stashed = self._stash_seed_once_files(skill_name, dest)
            if dest.exists():
                shutil.rmtree(dest)
            shutil.copytree(src, dest, ignore=_test_ignore)
            self._restore_seed_once_files(skill_name, dest, stashed)
            self.extensions.prune_inline_extensions(skill_name, dest)
            self.extensions.merge_extensions(skill_name, dest)
            self.extensions.append_project_local(skill_name, dest)
            # hash includes embedded extensions
            self.ctx.record("skills", item_stack, skill_name, src, dest,
                            projectOwned=project_owned_names(skill_name))
            # emit per-file entries for extension content so feed-badger can
            # detect user edits to extension files (#65)
            ext_dir = dest / "extensions"
            if ext_dir.is_dir():
                for f in sorted(ext_dir.rglob("*")):
                    if f.is_file():
                        ext_src = src / "extensions" / f.relative_to(ext_dir)
                        self.ctx.record(
                            "skills", item_stack,
                            f"{skill_name}/extensions/{f.relative_to(ext_dir).as_posix()}",
                            ext_src if ext_src.exists() else f, f)

    # -- seed-once (framework writes once, project owns thereafter; see #15) ----------
    def project_owned_files(self, dest: Path, skill_name: str) -> List[str]:
        """Files inside a delivered skill directory the project owns, not the framework.

        What the prune consults before removing a superseded skill tree: the framework never
        wrote these and cannot put them back (#243).
        """
        return [name for name in project_owned_names(skill_name) if (dest / name).exists()]

    def _stash_seed_once_files(self, skill_name: str, dest: Path) -> Dict[str, bytes]:
        """Read the current content of any seed-once files inside a skill dir before it is
        rmtree'd, so they can be restored after the fresh copytree. Empty on first scaffold
        (dest doesn't exist yet) or when --reset-seed-files is requested."""
        if self.ctx.reset_seed_files:
            return {}
        stashed: Dict[str, bytes] = {}
        for relpath in SEED_ONCE_SKILL_FILES.get(skill_name, []):
            p = dest / relpath
            if p.exists():
                stashed[relpath] = p.read_bytes()
        # Also stash project-local.md (generic: any skill may carry one)
        pl = dest / PROJECT_LOCAL_FILE
        if pl.exists():
            stashed[PROJECT_LOCAL_FILE] = pl.read_bytes()
        return stashed

    def _restore_seed_once_files(self, skill_name: str, dest: Path,
                                 stashed: Dict[str, bytes]) -> None:
        """Write back stashed seed-once file content after the skill dir's fresh copytree."""
        for relpath, content in stashed.items():
            p = dest / relpath
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_bytes(content)
            self.ctx.notes.append(
                f"preserved seed-once .ai-badger/skills/{skill_name}/{relpath} "
                "(already existed; not re-seeded; pass --reset-seed-files to reset)"
            )

    # -- Hermes skill discovery ------------------------------------------------------
    def symlink_hermes_skills(self) -> None:
        """Link this project's skills into ~/.hermes/skills/<project>/ when hermes is an agent.

        Hermes resolves skills from ~/.hermes/skills/ plus skills.external_dirs only; the
        per-project namespace directory avoids the cross-project name collisions that made
        external_dirs unusable (docs/adr/0003-hermes-skill-discovery-via-namespaced-symlinks.md).
        """
        if "hermes" not in self.ctx.config.get("agents", []):
            return
        try:
            links = relink_hermes_skills(self.ctx.target, self.ctx.config, self.ctx.skills)
        except ValueError as exc:
            # A refusal the user can act on: it names their project name as the cause.
            self.ctx.notes.append(f"hermes skill links skipped — {exc}")
            return
        if links["created"]:
            self.ctx.notes.append(f"hermes skill links: {', '.join(links['created'])}")
        if links["removed"]:
            self.ctx.notes.append(
                f"hermes skill links removed: {', '.join(links['removed'])} — no longer "
                f"delivered to this project"
            )
