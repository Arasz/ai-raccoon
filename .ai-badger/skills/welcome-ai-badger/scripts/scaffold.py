#!/usr/bin/env python3
"""Materialize a target repo's .ai-badger/ scaffold from a validated config.json.

MECHANICAL ONLY — no LLM, no network (except optional plugin installs, which are
skippable). The agent authors config.json; this script does everything else deterministically
and idempotently (safe to re-run; it rewrites managed files and refreshes the manifest).

Usage:
  scaffold.py --config <path/to/config.json> --target <target repo dir> [--root <framework>]
              [--skills task,prompt-markers] [--no-install] [--generated-at <iso>]
              [--overwrite-agent-files] [--reset-seed-files] [--execute]

  --overwrite-agent-files  replace hand-authored CLAUDE.md/copilot/hermes files
  --reset-seed-files       reseed SEED-ONCE files, discarding project-owned edits
  --execute                actually run skill install commands (default: print them)

An explicitly empty --skills means "the set already scaffolded", not "none": it is recovered
from <target>/.ai-badger/manifest.json rather than treated as an instruction to unlink every
discovery symlink (#129). Omitting --skills scaffolds the catalog defaults.

Outputs under <target>/.ai-badger/ plus copied agent-discovery files (CLAUDE.md, copilot,
hermes) per config.agents, and <target>/.ai-badger/manifest.json.
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

def _bootstrap_lib() -> Path:
    """Put the framework's engine/ and tooling/ on sys.path and return its root.

    One predicate, shared with badger_lib.is_framework_root: schemas/ + features/ +
    engine/badger_lib.py. Ordered inputs: --root, an ancestor walk, $AI_BADGER, the root
    recorded in a .ai-badger/manifest.json above this file, then ~/.ai-badger/framework
    (ADR-0009). Duplicated verbatim in every entry point because locating badger_lib is
    what it is for.
    """
    def is_root(path):
        return ((path / "schemas").is_dir() and (path / "features").is_dir()
                and (path / "engine" / "badger_lib.py").is_file())

    def argv_root():
        # sys.argv is ours only when this file is the program being run; these modules are
        # also imported into hosts whose own --root means something else entirely.
        try:
            if not sys.argv or Path(sys.argv[0]).resolve() != Path(__file__).resolve():
                return None
        except (OSError, ValueError):
            return None
        argv = sys.argv[1:]
        for i, arg in enumerate(argv):
            if arg == "--root" and i + 1 < len(argv):
                return argv[i + 1]
            if arg.startswith("--root="):
                return arg.split("=", 1)[1]
        return None

    def checked(value, source):
        root = Path(value).expanduser()
        if not is_root(root):
            raise RuntimeError(
                f"{source} is {root}, which is not an ai-badger framework root "
                f"(no schemas/ + features/ + engine/badger_lib.py)"
            )
        return root

    def manifests(start):
        # Above this file only. A working directory belongs to whatever repo the user
        # opened, and no repo may steer the sys.path of a hook that runs on session start.
        for anc in [start, *start.parents]:
            manifest = (anc / "manifest.json" if anc.name == ".ai-badger"
                        else anc / ".ai-badger" / "manifest.json")
            if not manifest.is_file():
                continue
            try:
                data = json.loads(manifest.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                yield manifest, data

    def recorded(start):
        for manifest, data in manifests(start):
            value = data.get("frameworkRoot")
            if not value:
                continue
            candidate = Path(value).expanduser()
            if not candidate.is_absolute():
                candidate = manifest.parent.parent / candidate
            if is_root(candidate):
                return candidate.resolve()
        return None

    def warn_on_cache_skew(root, start):
        # The cache is last in the order and never updated in place, so its engine can be
        # many releases behind the caller. Say so; never break a session over it.
        if root.resolve() != cache.resolve():
            return
        try:
            have = (cache / "VERSION").read_text(encoding="utf-8").strip()
        except OSError:
            return
        want = next((d.get("frameworkVersion") for _, d in manifests(start)
                     if d.get("frameworkVersion")), None)
        if have and want and have != want:
            print(f"ai-badger: {cache} is version {have}, but this project was scaffolded "
                  f"by {want}. The cache is never updated in place — remove it, or pass "
                  f"--root <framework checkout>.", file=sys.stderr)

    here = Path(__file__).resolve()
    cache = Path.home() / ".ai-badger" / "framework"
    value = argv_root()
    if value:
        root = checked(value, "--root")
    else:
        root = next((anc for anc in [here, *here.parents] if is_root(anc)), None)
        if root is None and os.environ.get("AI_BADGER"):
            root = checked(os.environ["AI_BADGER"], "$AI_BADGER")
        root = root or recorded(here) or (cache if is_root(cache) else None)
    if root is None:
        raise RuntimeError(
            f"could not locate the ai-badger framework: none above {here.parent}, no "
            f"$AI_BADGER, no frameworkRoot in a .ai-badger/manifest.json above it, and no "
            f"cache at {cache} — pass --root <framework> or clone "
            f"https://github.com/Arasz/ai-badger"
        )
    warn_on_cache_skew(root, here)
    sys.path.insert(0, str(root / "tooling"))
    sys.path.insert(0, str(root / "engine"))
    return root.resolve()


FRAMEWORK_ROOT = _bootstrap_lib()
import badger_lib as bl
import framework_copies as fc

# Ensure the script's directory is on sys.path so domain modules resolve
# when scaffold.py is loaded dynamically (e.g. via tests' load_script).
_SCRIPT_DIR = str(Path(__file__).resolve().parent)
if _SCRIPT_DIR not in sys.path:
    sys.path.insert(0, _SCRIPT_DIR)

from _shared import (  # noqa: E402 — re-exported for backward compatibility
    _test_ignore, PROJECT_LOCAL_FILE, MANAGED_HEADER, _MANAGED_PREFIX,
    cfg_get, requirement_met, _condition_met, _within,
)

# Read from each skill's own `scope:` frontmatter (ADR-0018), against the catalog
# scaffold_skills actually reads, so a default-scope skill shipped from another stack is
# not offered here and then reported as missing.
DEFAULT_SKILLS = bl.default_skills_in(FRAMEWORK_ROOT / "features" / "common" / "skills")


# ---------------------------------------------------------------------- index lookups
# feature_items and find_skill_in_stacks live in badger_lib — single source of truth.


def git_provenance(root: Path) -> Tuple[Optional[str], bool]:
    """Return (HEAD sha, working-tree-dirty) for root, or (None, False) when it is not a git repo.

    A plugin cache is a plain copy with no .git, so the commit is unknowable there and the
    version resolves to it instead (ADR-0001 decision 4). A copy cannot be dirty, so False
    is a fact rather than a missing value.
    """
    if not (root / ".git").exists():
        return None, False
    try:
        sha = subprocess.run(
            ["git", "-C", str(root), "rev-parse", "HEAD"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        status = subprocess.run(
            ["git", "-C", str(root), "status", "--porcelain"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None, False
    return (sha or None), bool(status)


# Progress marker for a run in flight. Present after a crash, absent after success:
# den-refresh and feed-badger read its absence as "never fully scaffolded" (F-25).
PARTIAL_MANIFEST = "manifest.json.partial"


def demote_headings(text: str, levels: int = 2) -> str:
    """Push ATX headings down `levels` so an embedded snippet keeps the host's outline.

    Fenced code is skipped — a `# comment` inside a block is not a heading.
    """
    out: List[str] = []
    fence = ""
    for line in text.splitlines(keepends=True):
        stripped = line.lstrip()
        if fence:
            if stripped.startswith(fence):
                fence = ""
            out.append(line)
            continue
        if stripped.startswith("```") or stripped.startswith("~~~"):
            fence = stripped[:3]
            out.append(line)
            continue
        hashes = len(stripped) - len(stripped.lstrip("#"))
        # An ATX heading needs a space after the hashes; `#5` is an issue reference.
        if 0 < hashes <= 6 and stripped[hashes:hashes + 1] == " ":
            out.append("#" * min(hashes + levels, 6) + line[line.index("#") + hashes:])
        else:
            out.append(line)
    return "".join(out)


# The shared context, the manifest's generated-config ledger, and the seven collaborators
from scaffold_context import ScaffoldContext  # noqa: E402
from generated_config import GeneratedConfigRecords  # noqa: E402
from hook_wiring import HookWiring, merge_hooks  # noqa: E402
from template_rendering import TemplateRendering, invariant_summary  # noqa: E402
from agent_files import AgentFiles  # noqa: E402
from extensions import Extensions  # noqa: E402
from mcp_tools import McpTools  # noqa: E402
from statusline_wiring import StatusLineWiring  # noqa: E402
# relink_hermes_skills is re-exported: den-refresh's refresh.py calls it on this module.
from skill_delivery import SkillDelivery, prune_namespaces, relink_hermes_skills  # noqa: E402
from skills_argv import resolve_requested_skills  # noqa: E402
from superseded_prune import SupersededPrune  # noqa: E402
from project_id import mint_project_id  # noqa: E402
from local_invariants import append_rendered  # noqa: E402
from gitignore_block import gitignore_managed_block, merge_gitignore, write_gitignore_block  # noqa


def _ctx_property(name: str) -> property:
    """A read/write `Scaffolder` attribute backed by the shared `ScaffoldContext`."""
    return property(lambda self: getattr(self.ctx, name),
                    lambda self, value: setattr(self.ctx, name, value))


class Scaffolder:
    """Materializes a target repo's .ai-badger/ scaffold from a validated config.json."""

    # Everything a collaborator reads or writes lives on self.ctx; these keep `s.notes`,
    # `s.target` and the rest addressable on the Scaffolder itself.
    root = _ctx_property("root")
    target = _ctx_property("target")
    aib = _ctx_property("aib")
    config = _ctx_property("config")
    index = _ctx_property("index")
    stacks = _ctx_property("stacks")
    skills = _ctx_property("skills")
    excluded = _ctx_property("excluded")
    overwrite = _ctx_property("overwrite")
    reset_seed_files = _ctx_property("reset_seed_files")
    notes = _ctx_property("notes")

    def __init__(self, root: Path, target: Path, config: Dict[str, Any],
                 skills: List[str], install: bool, overwrite: bool = False,
                 reset_seed_files: bool = False, execute: bool = False):
        # The enforcement point for config.exclude/include: consumers read self.skills or
        # self.items(), so welcome-ai-badger and den-refresh cannot disagree about either.
        aliases = bl.gateway_aliases(root)
        excluded = bl.exclusions(config, aliases)
        self.included = bl.inclusions(config)
        self.addable_skills = set(bl.opt_in_skills_in(root / "features" / "common" / "skills"))
        # Grouped skills install whole (#266), expanded before the addable filter; a stale
        # member name resolves to the gateway that absorbed it (ADR-0021). The composition is
        # the shared include-derived oracle (bl.include_derived_skill_names) so the guard's
        # expected set cannot drift from what the Scaffolder actually asks for (D1) — only the
        # include-derived block feeds delivery here: an explicit argv REPLACES the defaults
        # and must never gain them back (API-F2).
        asked_for = bl.include_derived_skill_names(config, aliases, self.addable_skills)
        offered = list(dict.fromkeys(list(skills) + asked_for))
        # Whether the delivered list is evidence of what the project wants: empty means
        # "unchanged" (#129), which discover_stack_local hides. See adjust_skills.may_prune.
        self.prune_discovery = bool(skills)
        index = bl.read_index(root)
        self.generated_config = GeneratedConfigRecords(target, index["frameworkVersion"])
        self.ctx = ScaffoldContext(
            root=root, target=target, aib=target / ".ai-badger", config=config,
            index=index, stacks=bl.resolve_stacks(config),
            skills=[s for s in offered if s not in excluded["skills"]],
            excluded=excluded, overwrite=overwrite, reset_seed_files=reset_seed_files,
            record_template=self.record_template, record=self.record,
            record_generated_config=self.generated_config.record,
        )
        self.extensions = Extensions(self.ctx)
        self.statusline = StatusLineWiring(self.ctx)
        self.hooks = HookWiring(self.ctx)
        self.mcp = McpTools(self.ctx)
        self.rendering = TemplateRendering(self.ctx)
        self.agent_files = AgentFiles(self.ctx, self.rendering)
        self.skill_delivery = SkillDelivery(self.ctx, self.extensions)
        self.superseded = SupersededPrune(self.ctx, self.skill_delivery)
        self.install = install
        self.execute = execute
        self.commit, self.dirty = git_provenance(root)
        self.entries: List[Dict[str, Any]] = []
        self._completed_steps: List[str] = []
        self._note_exclusions()
        self._note_inclusions()

    # -- exclusions -------------------------------------------------------------------
    def items(self, stack: str, feature: str) -> List[Dict[str, Any]]:
        """Index items for one stack's feature bucket, minus the ones config.exclude declines."""
        declined = self.excluded.get(feature, set())
        return [i for i in bl.feature_items(self.index, stack, feature)
                if i.get("name") not in declined]

    def _note_exclusions(self) -> None:
        """Report each exclusion: what it declined, and what it no longer matches.

        An exclusion naming a catalog item the framework has since dropped goes inert rather
        than fatal — refresh refuses on an invalid config, so a fatal one would turn an
        upstream deletion into a broken upgrade (research §4.2).
        """
        for feature in bl.EXCLUDABLE_FEATURES:
            declined = self.excluded[feature]
            if not declined:
                continue
            known = {i.get("name") for stack in self.stacks
                     for i in bl.feature_items(self.index, stack, feature)}
            singular = feature[:-1]
            for name in sorted(declined - known):
                self.notes.append(
                    f"exclusion '{name}' matches no catalog {singular} — safe to remove "
                    f"from config.json"
                )
            for name in sorted(declined & known):
                self.notes.append(f"declined {singular} '{name}' (config.exclude.{feature})")
        for name in sorted(self.excluded["skills"]):
            if (self.aib / "skills" / name).is_dir():
                self.notes.append(
                    f"declined skill '{name}': .ai-badger/skills/{name} left on disk — "
                    f"delete it by hand"
                )

    def _note_inclusions(self) -> None:
        """Report each inclusion: what it added, and what it could not add — never fatal."""
        self.notes.extend(bl.inclusion_notes(
            self.included["skills"], self.excluded["skills"], self.addable_skills,
            bl.default_skills_in(self.root / "features" / "common" / "skills"),
            aliases=bl.gateway_aliases(self.root)))

    # -- provenance -----------------------------------------------------------------
    def record(self, feature: str, stack: str, name: str, source: Path, target: Path,
               **extra: Any) -> None:
        """Append a manifest entry recording where a scaffolded item came from and went.

        Feature types the registry marks `hashes_source` record the framework source's hash
        rather than the written file's, because drift.compare re-hashes the source for file
        entries and any other choice can never match (ADR-0006). `extra` is merged in verbatim.
        """
        entry = {
            "feature": feature, "stack": stack, "name": name,
            "source": source.relative_to(self.root).as_posix(),
            "target": target.relative_to(self.target).as_posix(),
            "frameworkVersion": self.index["frameworkVersion"],
        }
        if source.is_dir():
            # Directory entry (skills): two hashes, two questions (#110). `hash` covers the
            # TARGET dir and answers "did this project edit its copy?"; `sourceHash` covers the
            # framework SOURCE and is the only one that can answer "has the framework moved
            # ahead?", because the target is rendered output the source is not comparable to.
            # Both exclude extensions/ (config-gated per project, with entries of their own);
            # `hash` also drops `projectOwned`, which the project edits and this run preserved.
            fingerprint = bl.dir_content_hash(
                target, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"],
                exclude_rel=extra.get("projectOwned"))
            entry["hash"] = fingerprint["content_hash"]
            entry["dirMeta"] = {
                "file_count": fingerprint["file_count"],
                "dir_count": fingerprint["dir_count"],
            }
            source_print = bl.dir_content_hash(
                source, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"]
            )
            entry["sourceHash"] = source_print["content_hash"]
            entry["sourceMeta"] = {
                "file_count": source_print["file_count"],
                "dir_count": source_print["dir_count"],
            }
        else:
            hash_from = source if bl.feature_type(feature).hashes_source else target
            entry["hash"] = bl.sha256_file(hash_from)
        self.entries.append({**entry, **extra})

    def copy_file(self, feature: str, stack: str, item: Dict[str, Any], dest_dir: Path) -> Path:
        """Copy one index item's source file into dest_dir and record its provenance."""
        src = self.root / item["path"]
        dest_dir.mkdir(parents=True, exist_ok=True)
        dest = dest_dir / src.name
        shutil.copyfile(src, dest)
        self.record(feature, stack, item["name"], src, dest)
        return dest

    def record_template(self, src: Path, dest: Path, seed_once: bool = False) -> None:
        """Record a template's provenance; `seed_once` marks one the scaffold never rewrites."""
        rel = src.relative_to(self.root / "features").parts
        self.record("templates", rel[0], Path(*rel[2:]).as_posix(), src, dest)
        self.entries[-1]["seedOnce"] = seed_once

    def generated_config_records(self) -> List[Dict[str, Any]]:
        """Every generated-but-not-owned config path the manifest should carry (#194)."""
        return self.generated_config.all_records(
            self._prior_manifest().get("generatedConfig", []))

    def _prior_manifest(self) -> Dict[str, Any]:
        """The manifest an earlier run left in place, or `{}` when there is none to read."""
        path = self.aib / "manifest.json"
        if not path.is_file():
            return {}
        try:
            return bl.load_json(path)
        except (ValueError, OSError):
            return {}

    # -- seed-once (framework writes once, project owns thereafter; see #15) --------
    def _seed_once_copy(self, src: Path, dest: Path, label: str) -> None:
        """Copy src to dest only on first scaffold. If dest already exists, it is project-owned
        and left untouched (--reset-seed-files overrides this and reseeds from src)."""
        if src.exists():
            self.record_template(src, dest, seed_once=True)
        if dest.exists() and not self.reset_seed_files:
            self.notes.append(f"preserved seed-once {label} (already exists; not re-seeded; "
                              "pass --reset-seed-files to reset)")
            return
        if src.exists():
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, dest)

    # -- features -------------------------------------------------------------------
    def scaffold_personas(self) -> None:
        """Copy every applicable stack's persona files into .ai-badger/agents/.

        Reads `bl.applicable_feature_items` — the same rule the Copilot agent delivery
        applies — so the two hosts deliver the same persona set (#210).
        """
        for stack, item in bl.applicable_feature_items(self.index, self.config, "personas"):
            self.copy_file("personas", stack, item, self.aib / "agents")

    def scaffold_instructions(self) -> List[Path]:
        """Copy every applicable stack's instruction files into .ai-badger/instructions/."""
        out: List[Path] = []
        for stack in self.stacks:
            for item in self.items(stack, "instructions"):
                out.append(self.copy_file("instructions", stack, item, self.aib / "instructions"))
        return out

    def collect_invariants(self) -> List[str]:
        """Copy invariant snippets and return their rendered markdown for CLAUDE.md."""
        rendered: List[str] = []
        delivered: Set[str] = set()
        for stack in self.stacks:
            for item in self.items(stack, "invariants"):
                dest = self.copy_file("invariants", stack, item, self.aib / "invariants")
                text = dest.read_text(encoding="utf-8").strip()
                rendered.append(invariant_summary(text, item["name"]))
                delivered.add(item["name"])
        append_rendered(rendered, self.aib / "invariants" / "local",
                        delivered, invariant_summary, self.notes)
        return rendered

    def scaffold_gitignore(self) -> None:
        """Merge the managed SQLite-artifact block into the target's .gitignore."""
        write_gitignore_block(self.ctx)

    def scaffold_agent_instructions(self) -> None:
        """Copy the agent-instructions schema/model template into .ai-badger/agent-instructions/."""
        tdir = self.root / "features" / "common" / "templates" / "agent-instructions"
        if not tdir.is_dir():
            self.notes.append("common/templates/agent-instructions missing — skipped")
            return
        out = self.aib / "agent-instructions"
        out.mkdir(parents=True, exist_ok=True)
        schema = tdir / "schema.json"
        if schema.exists():
            shutil.copyfile(schema, out / "schema.json")
            self.record_template(schema, out / "schema.json")
        model_tmpl = tdir / "model.template.json"
        self._seed_once_copy(model_tmpl, out / "model.json",
                              ".ai-badger/agent-instructions/model.json")

    def scaffold_templates(self) -> None:
        """Seed the shared state.json template into .ai-badger/ on first scaffold only. It is a
        live task index the project owns after that (see #15): a re-scaffold must not clobber it."""
        tdir = self.root / "features" / "common" / "templates"
        state = tdir / "state.json"
        self._seed_once_copy(state, self.aib / "state.json", ".ai-badger/state.json")

    # -- skill installation --------------------------------------------------------
    def install_plugins(self) -> List[str]:
        """Generate skill installation commands using the install_plugins library.

        Reads skills-source.json + skills.json per stack, resolves per-agent
        installation commands from plugins-instructions.json.
        """
        import install_plugins as ip_lib
        result = ip_lib.install_skills(self.root, self.config, dry_run=not self.install)

        # Provenance: copy skills-source.json + skills.json per stack
        for stack in self.stacks:
            for fname in ("skills-source.json", "skills.json"):
                src = self.root / "features" / stack / fname
                if src.exists():
                    dest_dir = self.aib / "skills-data" / stack
                    dest_dir.mkdir(parents=True, exist_ok=True)
                    dest = dest_dir / fname
                    shutil.copyfile(src, dest)
                    feature = "skills"
                    self.record(feature, stack, f"{stack}/{fname}", src, dest)

        cmds = result["commands"]
        if self.execute and cmds:
            for cmd in cmds:
                try:
                    proc = subprocess.run(
                        cmd, capture_output=True, text=True,
                        timeout=30, cwd=str(self.target), check=False,
                    )
                    shown = ip_lib.printable(cmd)
                    if proc.returncode == 0:
                        self.notes.append(f"executed: {shown}")
                    else:
                        self.notes.append(
                            f"command failed (exit {proc.returncode}): {shown}"
                            f"{': ' + proc.stderr.strip() if proc.stderr.strip() else ''}"
                        )
                except subprocess.TimeoutExpired:
                    self.notes.append(f"command timed out (30s): {ip_lib.printable(cmd)}")
                except OSError as exc:
                    self.notes.append(f"command error: {ip_lib.printable(cmd)} — {exc}")
        elif self.install and cmds:
            self.notes.append("skill auto-install requested but deferred to report "
                              "(run the commands below manually or via --execute)")
        for w in result.get("warnings", []):
            self.notes.append(f"skill install warning: {w}")
        return cmds

    # -- hooks ------------------------------------------------------------------------
    def wire_hooks(self) -> None:
        """Merge the framework's hook registrations into the project's .claude/settings.json."""
        self.hooks.wire()

    # -- Hermes skill discovery ---------------------------------------------------
    def symlink_hermes_skills(self) -> None:
        """Publish this project's skills in the Hermes namespace, through `skill_delivery`."""
        self.skill_delivery.symlink_hermes_skills()

    # -- dependency checking ---------------------------------------------------------
    def _check_dependencies(self) -> Dict[str, Any]:
        """Check and install feature dependencies from dependencies.json.

        Loads the dependency catalog, filters to scaffolded features, creates
        a Python venv if needed, and installs packages.
        """
        import dependency_check as dc_lib
        result = dc_lib.run_dependency_check(self.root, self.target, features=self.skills,
                                             allow_install=self.execute)
        if result["installed"]:
            self.notes.append(
                f"installed dependencies: {', '.join(result['installed'])}"
            )
        if result["errors"]:
            for err in result["errors"]:
                self.notes.append(f"dependency error: {err}")
        if result["hints"]:
            for hint in result["hints"]:
                self.notes.append(f"optional dependency: {hint}")
        # Report venv python path for MCP server commands
        venv_python = dc_lib.get_venv_python(self.target)
        if venv_python:
            self.notes.append(f"venv python: {venv_python}")
        return result

    def copy_engine_and_schemas(self) -> None:
        """Copy engine/ and schemas/ into .ai-badger/ for self-contained validation."""
        for sub in ("schemas", "engine"):
            src, dst = self.root / sub, self.aib / sub
            if src.is_dir():
                shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns(".*", "*.pyc", "__pycache__"))

    # -- adjustments ----------------------------------------------------------------
    def run_adjustments(self) -> None:
        """Run agent-specific adjustments declared in features/<agent>/adjustments/.

        Each adjustment is a Python script with an adjust(context) function that
        receives the framework root, config, and target directory, and returns
        {'applied': bool, 'files': list, 'notes': str}.
        """
        # An adjustment is loaded by path: what it cannot resolve itself travels in the context.
        declared_mcp = sorted(self.mcp.split_servers_by_scope(self.mcp.declared_servers())[0])
        for agent_name in self.config.get("agents", []):
            adj_path = self.root / "features" / agent_name / "adjustments" / "adjustment.json"
            if not adj_path.exists():
                continue

            try:
                adj_manifest = bl.load_json(adj_path)
            except (ValueError, OSError):
                continue

            for adj in adj_manifest.get("adjustments", []):
                script_name = adj.get("script")
                if not script_name:
                    continue

                script_path = adj_path.parent / script_name
                if not script_path.exists():
                    self.notes.append(
                        f"adjustment script '{script_name}' for '{agent_name}' not found — skipped"
                    )
                    continue

                try:
                    import importlib.util
                    spec = importlib.util.spec_from_file_location(
                        f"adj_{agent_name}_{script_name}", script_path
                    )
                    if spec is None or spec.loader is None:
                        self.notes.append(
                            f"adjustment '{script_name}' for '{agent_name}' — could not load module"
                        )
                        continue
                    mod = importlib.util.module_from_spec(spec)
                    spec.loader.exec_module(mod)

                    # Filter the *delivered* skills to those this agent's stacks own — by
                    # directory membership, not by scope, and over stacks derived from the
                    # config rather than named in a literal. Both narrowings delivered skills
                    # to .ai-badger/skills/ and linked them nowhere the agent looks, with the
                    # run still reporting success (#261).
                    agent_stacks = bl.discovery_stacks_for_agent(self.config, agent_name)
                    agent_skills = [s for s in self.skills
                                    if any(s in bl.catalog_skills_for_stack(self.root, st)
                                           for st in agent_stacks)]

                    context = {
                        "framework_root": self.root,
                        "config": self.config,
                        "feature_dir": self.root / "features" / agent_name / "adjustments",
                        "target_dir": self.aib,
                        "target": self.target,
                        # --no-install reaches the adjustments: user-global state
                        # (~/.hermes/plugins) is never written when it is set.
                        "install": self.install,
                        "skills": agent_skills,
                        "prune": self.prune_discovery,
                        "personas": [item for _stack, item in bl.applicable_feature_items(
                            self.index, self.config, "personas")],
                        "index": self.index,
                        "mcp_servers": declared_mcp,
                        "mcp_declarations": self.mcp.declarations_for_agent(agent_name),
                        "mcp_declined": self.mcp.declined_servers(),
                    }
                    result = mod.adjust(context)
                    if result.get("applied"):
                        self.notes.append(
                            f"adjustment '{adj.get('feature', script_name)}' for "
                            f"'{agent_name}': {result.get('notes', 'applied')}"
                        )
                        for f in result.get("files", []):
                            self.record("adjustments", agent_name,
                                        f"adjustments/{f}", script_path,
                                        self.target / f)
                    else:
                        self.notes.append(
                            f"adjustment '{adj.get('feature', script_name)}' for "
                            f"'{agent_name}': not applied — {result.get('notes', 'no reason')}"
                        )
                except Exception as exc:  # pylint: disable=broad-exception-caught
                    self.notes.append(
                        f"adjustment '{script_name}' for '{agent_name}' failed: {exc}"
                    )

    # -- orchestrate ----------------------------------------------------------------
    def _recorded_framework_root(self) -> str:
        """The pointer back to the framework this scaffold came from (ADR-0007).

        Relative when the framework is inside the target repo — a repo that scaffolds itself
        records `.`, which survives a clone; anywhere else only an absolute path can point.
        """
        root, target = self.root.resolve(), self.target.resolve()
        if root == target or target in root.parents:
            return os.path.relpath(str(root), str(target))
        return str(root)

    def _record_progress(self, step: str) -> None:
        """Append `step` to manifest.json.partial — the breadcrumb a crashed run leaves."""
        self._completed_steps.append(step)
        bl.dump_json(self.aib / PARTIAL_MANIFEST, {
            "note": "a scaffold run started and did not finish; steps below completed",
            "frameworkVersion": self.index["frameworkVersion"],
            "completedSteps": list(self._completed_steps),
        })

    def _outside_project(self, step: str, action) -> None:
        """Run a write that lands outside the project; a failure becomes a note, not a crash.

        The project scaffold must not be lost because ~/.claude or ~/.hermes is unwritable.
        """
        try:
            action()
        except Exception as exc:  # pylint: disable=broad-exception-caught
            self.notes.append(f"{step} failed ({type(exc).__name__}) — skipped; "
                              f"the project scaffold is unaffected")

    def run(self, generated_at: Optional[str] = None) -> Dict[str, Any]:
        """Run every scaffold step in order and return the manifest, plugin commands, and notes."""
        self.aib.mkdir(parents=True, exist_ok=True)
        mint_project_id(self.aib)
        self._completed_steps = []
        self._record_progress("start")
        self.superseded.prune(self._prior_manifest().get("entries", []))
        self.scaffold_personas()
        instr_paths = self.scaffold_instructions()
        invariants = self.collect_invariants()
        self._record_progress("personas-and-instructions")
        self.skill_delivery.discover_stack_local()
        self.skill_delivery.scaffold_skills()
        self._record_progress("skills")
        if self.install:  # links point at --target; a throwaway target leaves them dangling
            self._outside_project("hermes skill symlinks", self.symlink_hermes_skills)
        self.scaffold_agent_instructions()
        self.scaffold_templates()
        self.scaffold_gitignore()
        self.mcp.fill_mcp_described()
        self.rendering.write_delegation_map(invariants, instr_paths,
                                            self.mcp.project_server_names())
        doc = self.rendering.assemble_instructions_doc(invariants, instr_paths)
        self.agent_files.write_agent_files(doc, instr_paths, invariants)
        self._record_progress("agent-files")
        self.wire_hooks()
        self.statusline.wire()
        self.run_adjustments()
        self._record_progress("hooks")
        plugin_cmds = self.install_plugins()
        dep_result = self._check_dependencies()
        written_config = dict(self.config)
        written_config["frameworkVersion"] = self.index["frameworkVersion"]
        bl.dump_json(self.aib / "config.json", written_config)
        self.mcp.generate_mcp_json()
        self._record_progress("config-and-mcp")
        project_servers, user_servers = self.mcp.split_servers_by_scope(
            self.mcp.declared_servers())
        self.mcp.propose_claude_mcp_user(user_servers)
        self.mcp.generate_copilot_mcp_json(project_servers)
        self.copy_engine_and_schemas()

        manifest = {
            "$schema": "../schemas/manifest.schema.json",
            "frameworkVersion": self.index["frameworkVersion"],
            "frameworkCommit": self.commit,
            "frameworkDirty": self.dirty,
            "frameworkRoot": self._recorded_framework_root(),
            "generatedAt": generated_at,
            "agents": self.config.get("agents", []),
            "skillScope": self.config.get("skillScope", self.config.get("pluginScope", "default")),
            "pluginScope": self.config.get("skillScope", self.config.get("pluginScope", "default")),  # compat
            "configHash": bl.config_hash(written_config),
            # Read after every destination has written, and before the manifest replaces the
            # one `generated_config_records` carries earlier runs' records forward from (#194).
            "generatedConfig": self.generated_config_records(),
            "entries": self.entries,
        }
        bl.dump_json(self.aib / "manifest.json", manifest)
        (self.aib / PARTIAL_MANIFEST).unlink(missing_ok=True)
        return {
            "manifest": manifest,
            "pluginCommands": plugin_cmds,
            "dependencyResult": dep_result,
            "notes": self.notes,
            "availableOptIn": bl.available_opt_in(self.root, self.skills),
        }


def main(argv=None) -> int:
    """CLI entry point: validate config.json, then scaffold .ai-badger/ into --target."""
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--config", required=True)
    ap.add_argument("--target", required=True)
    ap.add_argument("--root")
    ap.add_argument("--skills", default=",".join(DEFAULT_SKILLS))
    ap.add_argument("--no-install", action="store_true")
    ap.add_argument("--overwrite-agent-files", action="store_true",
                    help="Overwrite existing hand-authored discovery files (CLAUDE.md, copilot, "
                         "hermes, .github/instructions/*). Default preserves any that lack the "
                         "ai-badger managed header.")
    ap.add_argument("--reset-seed-files", action="store_true",
                    help="Reseed SEED-ONCE files (.ai-badger/state.json, agent-instructions/"
                         "model.json, skills/prompt-markers/markers-context.json) from the "
                         "framework template, discarding any project-owned edits. Default "
                         "preserves them once they exist.")
    ap.add_argument("--execute", action="store_true",
                    help="Execute skill install commands instead of just printing them. "
                         "Commands run with 30s timeout per command. Default is advisory-only.")
    ap.add_argument("--generated-at", default=None,
                    help="ISO timestamp to stamp in manifest (orchestrator supplies; "
                         "scripts avoid clocks).")
    args = ap.parse_args(argv)

    root = Path(args.root).resolve() if args.root else bl.find_root()
    config_path = Path(args.config).resolve()
    target = Path(args.target).resolve()

    # validate config BEFORE doing anything
    errors = bl.validate_file(config_path, root / "schemas" / "config.schema.json")
    if errors:
        print("config.json is INVALID — aborting scaffold:")
        for e in errors:
            print(f"    - {e}")
        return 1

    config = bl.load_json(config_path)
    skills, cli_notes, rejection = resolve_requested_skills(root, target, args.skills)
    if rejection:
        print(rejection, end="")
        return 2
    scaf = Scaffolder(root, target, config, skills, install=not args.no_install,
                      overwrite=args.overwrite_agent_files,
                      reset_seed_files=args.reset_seed_files,
                      execute=args.execute)
    result = scaf.run(generated_at=args.generated_at)

    print(f"scaffolded {len(result['manifest']['entries'])} entries into {scaf.aib}")
    for n in cli_notes + result["notes"]:
        print(f"  note: {n}")
    if result["availableOptIn"]:
        print("  opt-in skills available (not installed):")
        for skill in result["availableOptIn"]:
            print(f"    - {skill['name']}: {skill['description']}\n      add it with: {skill['configEdit']}")
    if result["pluginCommands"]:
        import install_plugins as ip_lib  # pylint: disable=import-outside-toplevel
        print("  plugin setup commands (run per chosen scope):")
        for c in result["pluginCommands"]:
            print(f"    $ {ip_lib.printable(c)}")

    # Onboarding a repository never deletes anything in a home directory; it names what is
    # there, and den-refresh --prune-cache is the one command that acts on it (#109).
    copies = fc.competing_copies_notice(fc.discover(running_root=root))
    if copies:
        print(copies)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
