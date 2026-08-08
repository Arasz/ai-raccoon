#!/usr/bin/env python3
"""Pull framework updates into an already-scaffolded project.

Orchestrates drift detection + re-scaffold: checks what changed upstream,
re-scaffolds using the project's existing config.json, and reports the result.

MECHANICAL ONLY — no LLM. The agent's role is to present the report and help
the user review the diff.

Usage: refresh.py --target <dir> --root <framework>
Exit codes: 0 = up to date or changes applied, 1 = drift found but re-scaffold
            could not run (reserved), 2 = usage error (missing config/manifest).
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional


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


def _load_script(relpath: str, base: Path):
    """Import an ai-badger script by repo-relative path: `base` first, then the resolved root.

    A mock framework passed as `base` need not carry every script, so the root this module
    already bootstrapped from is the fallback — never a second root search.
    """
    candidates = [base] if base == FRAMEWORK_ROOT else [base, FRAMEWORK_ROOT]
    for cand in candidates:
        path = cand / relpath
        if path.exists():
            name = "aib_" + path.stem
            spec = importlib.util.spec_from_file_location(name, path)
            module = importlib.util.module_from_spec(spec)
            sys.modules[name] = module
            spec.loader.exec_module(module)
            return module
    raise FileNotFoundError(f"could not find {relpath} in {candidates}")


FRAMEWORK_ROOT = _bootstrap_lib()
sys.path.insert(0, str(Path(__file__).resolve().parent))
import badger_lib as bl  # pylint: disable=wrong-import-position
import framework_copies as fc  # pylint: disable=wrong-import-position
import skill_usage as su  # pylint: disable=wrong-import-position


def _nested_checkouts(directory: str, names: List[str]) -> set:
    """Names in `directory` that hold their own `.git` — a second checkout, not our state.

    `.ai-badger/worktrees/<taskId>` is a live git worktree; copying it would duplicate a whole
    repository, and a copy taken while a session writes into it can fail mid-way.
    """
    parent = Path(directory)
    return {name for name in names if (parent / name / ".git").exists()}


def check_breaking_and_backup(root: Path, target: Path) -> Dict[str, Any]:
    """Back up .ai-badger/ before any re-scaffold, and report whether the jump is breaking.

    The backup is unconditional: a routine refresh rewrites the same files a breaking one
    does, and a re-scaffold that raises partway leaves no other recovery path (F-25).

    Returns {"isBreaking": bool, "backupPath": str|None}.
    """
    aib = target / ".ai-badger"
    config = bl.load_json(aib / "config.json")
    from_version = config.get("frameworkVersion", "0.0.0")
    to_version = (root / "VERSION").read_text(encoding="utf-8").strip()

    is_breaking = bl.is_breaking_transition(from_version, to_version, root)

    # Back up .ai-badger/ to .ai-badger.bckp/
    import shutil
    bckp = target / bl.BACKUP_DIR_NAME
    if bckp.exists():
        shutil.rmtree(bckp)
    shutil.copytree(aib, bckp, ignore=_nested_checkouts)
    return {"isBreaking": is_breaking, "backupPath": str(bckp)}


def check_prerequisites(target: Path) -> Optional[str]:
    """Verify target has config.json and manifest.json; return error message or None."""
    aib = target / ".ai-badger"
    if not (aib / "config.json").exists():
        return f"no .ai-badger/config.json at {aib} — project not scaffolded by ai-badger"
    if not (aib / "manifest.json").exists():
        return f"no .ai-badger/manifest.json at {aib} — project was never fully scaffolded"
    return None


def run_drift(root: Path, manifest: Dict[str, Any],
              stacks: Optional[List[str]] = None,
              target: Optional[Path] = None,
              delivering: Optional[List[str]] = None) -> Dict[str, Any]:
    """Run drift comparison against the framework's current content."""
    drift_mod = _load_script("features/common/skills/welcome-ai-badger/scripts/drift.py", root)
    return drift_mod.compare(root, manifest, stacks=stacks, target=target,
                             delivering=delivering)


def re_scaffold(root: Path, target: Path, config: Dict[str, Any],
                manifest: Dict[str, Any],
                generated_at: Optional[str] = None) -> Dict[str, Any]:
    """Re-run scaffold.py with the existing config.json.

    The skill list is the manifest's own skills first — so extension-bearing ones keep their
    extensions — unioned with the catalog's default-scope skills, so a skill added upstream
    after this project was scaffolded actually arrives (#104). Manifest absence is not opt-out;
    `config.exclude` is, and the Scaffolder applies it — `refreshedSkills` reports what it
    delivered, not what was offered.
    """
    scaffold_mod = _load_script("features/common/skills/welcome-ai-badger/scripts/scaffold.py", root)

    skill_names = list(dict.fromkeys(
        bl.scaffolded_skill_names(manifest)
        + bl.default_skills_in(root / "features" / "common" / "skills")
    ))

    scaf = scaffold_mod.Scaffolder(
        root=root, target=target, config=config,
        skills=skill_names, install=False,
    )
    result = scaf.run(generated_at=generated_at)
    return {
        "entries": len(result["manifest"]["entries"]),
        "notes": result["notes"],
        "pluginCommands": result["pluginCommands"],
        "refreshedSkills": scaf.skills,
    }


def relink_hermes_skills(root: Path, target: Path, config: Dict[str, Any]) -> Dict[str, List[str]]:
    """Re-link the project's skills into ~/.hermes/skills/<project>/ after a refresh.

    Reads the skill names from disk so added skills are linked and removed ones are dropped;
    a no-op unless hermes is a configured agent. Returns {"created": [...], "removed": [...]}.
    """
    no_op: Dict[str, List[str]] = {"created": [], "removed": []}
    if "hermes" not in config.get("agents", []):
        return no_op
    skills_dir = target / ".ai-badger" / "skills"
    if not skills_dir.is_dir():
        return no_op
    names = sorted(p.name for p in skills_dir.iterdir() if p.is_dir())
    scaffold_mod = _load_script(
        "features/common/skills/welcome-ai-badger/scripts/scaffold.py", root
    )
    return scaffold_mod.relink_hermes_skills(target, config, names)


def delivered_skills(manifest: Dict[str, Any],
                     scaffold_result: Optional[Dict[str, Any]]) -> List[str]:
    """The skills this project holds right now: what the re-scaffold delivered, else the manifest."""
    if scaffold_result:
        return list(scaffold_result.get("refreshedSkills") or [])
    return bl.scaffolded_skill_names(manifest)


def report_framework_copies(root: Path, prune: bool) -> Optional[Dict[str, Any]]:
    """Name every tree claiming to be ai-badger, and act on the one cache we created.

    Reporting is unconditional; deletion happens only when `prune` was asked for, and only for
    `~/.ai-badger/framework` — Claude Code owns its plugin cache and ai-badger never removes
    another tool's state (#109, 0.19.0).
    """
    copies = fc.discover(running_root=root)
    cache = fc.prune_home_cache(running_root=root, execute=prune)
    competing = [{"path": str(c.path), "version": c.version, "owner": c.owner,
                  "running": c.running, "prunable": c.prunable}
                 for c in copies if not c.running]
    if not competing and cache.status == fc.ABSENT:
        return None
    report: Dict[str, Any] = {"running": str(root), "competing": competing}
    if cache.status != fc.ABSENT:
        report["cache"] = {"status": cache.status, "path": str(cache.path),
                           "version": cache.version, "detail": cache.detail}
    return report


def report_hermes_namespaces(root: Path, prune: bool) -> List[Dict[str, Any]]:
    """Name every ~/.hermes/skills/<project>/ whose project is gone, and act only when asked.

    `report_framework_copies`' shape, for the same reason: a namespace can dangle because a
    drive is unmounted, so the default reports and deletes nothing. A directory ai-badger did
    not create is never listed here and never removed, flag or no flag.
    """
    scaffold_mod = _load_script(
        "features/common/skills/welcome-ai-badger/scripts/scaffold.py", root
    )
    return [{"path": str(n.path), "links": n.links, "kept": n.kept, "target": n.target,
             "status": n.status, "detail": n.detail}
            for n in scaffold_mod.prune_namespaces(execute=prune)]


def main(argv: Optional[List[str]] = None) -> int:
    """CLI entry point: check drift, re-scaffold if needed, print JSON report."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", help="framework repo root (default: autodetect)")
    parser.add_argument("--target", required=True, help="scaffolded project root")
    parser.add_argument("--generated-at", default=None,
                        help="ISO timestamp for manifest (default: none)")
    parser.add_argument("--prune-cache", action="store_true",
                        help="Delete ~/.ai-badger/framework, the clone ai-badger makes when it "
                             "has no other root and never updates in place. Default reports it "
                             "and deletes nothing. Claude Code's plugin cache is never touched.")
    parser.add_argument("--prune-namespaces", action="store_true",
                        help="Delete every ~/.hermes/skills/<project>/ whose whole target tree "
                             "is gone. Default reports them and deletes nothing. A directory "
                             "ai-badger did not create is never removed.")
    parser.add_argument("--force", action="store_true",
                        help="Re-scaffold even when no drift signal fired. The documented "
                             "recovery path for a scaffold/config disagreement no signal "
                             "models yet — stays inside den-refresh instead of calling "
                             "scaffold.py by hand.")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve() if args.root else bl.find_root()
    target = Path(args.target).resolve()

    # 1. Check prerequisites
    err = check_prerequisites(target)
    if err:
        print(json.dumps({"error": err}))
        return 2

    # 2. Read existing config
    config_path = target / ".ai-badger" / "config.json"
    try:
        config = bl.load_json(config_path)
    except (ValueError, OSError) as exc:
        print(json.dumps({"error": f"could not read config at {config_path}: {exc}"}))
        return 2

    # 3. Validate config against schema
    errors = bl.validate_file(config_path, root / "schemas" / "config.schema.json")
    if errors:
        print(json.dumps({
            "error": "config.json is INVALID — fix before refreshing",
            "validationErrors": errors,
        }))
        return 2

    # 4. Read manifest (needed by both drift and re-scaffold)
    manifest_path = target / ".ai-badger" / "manifest.json"
    try:
        manifest = bl.load_json(manifest_path)
    except (ValueError, OSError) as exc:
        print(json.dumps({"error": f"could not read manifest at {manifest_path}: {exc}"}))
        return 2

    # 5. Check for breaking version transition
    breaking_result = check_breaking_and_backup(root, target)

    # 6. Check drift
    scaffold_version = config.get("frameworkVersion", "?")
    current_version = (root / "VERSION").read_text(encoding="utf-8").strip()

    drift_result = run_drift(root, manifest, stacks=bl.resolve_stacks(config), target=target,
                             delivering=bl.delivering_stacks(config))

    # 6b. Detect new stacks not in config (respecting stack-ignore.json)
    drift_mod = _load_script("features/common/skills/welcome-ai-badger/scripts/drift.py", root)
    stack_ignore_path = target / ".ai-badger" / "stack-ignore.json"
    stack_ignore: List[str] = []
    if stack_ignore_path.exists():
        try:
            ignore_data = bl.load_json(stack_ignore_path)
            stack_ignore = ignore_data.get("ignore", [])
        except (ValueError, OSError):
            pass
    new_stacks = drift_mod.detect_new_stacks(
        target, root, config_stacks=bl.delivering_stacks(config), ignore=stack_ignore
    )

    # newStacks is report-only (#134): a re-scaffold runs the *same* config and cannot
    # deliver a stack the config does not name, so it must not gate the re-scaffold.
    has_drift = bool(drift_result.get("changed") or drift_result.get("removed")
                     or drift_result.get("orphaned") or drift_result.get("newItems")
                     or drift_result.get("versionChanged") or drift_result.get("configChanged"))

    # 7. Re-scaffold if drift detected, a breaking change forces it, or --force was asked for
    scaffold_result = None
    if has_drift or breaking_result["isBreaking"] or args.force:
        scaffold_result = re_scaffold(root, target, config, manifest,
                                       generated_at=args.generated_at)

    # 7b. Re-link the hermes namespace so added/removed skills propagate (#58, ADR 0003)
    hermes_links = relink_hermes_skills(root, target, config)

    # The version stamp follows the re-scaffold; it never leads it. Advancing it without one
    # made the next report read the stamp this run wrote and call a stale scaffold green,
    # while manifest.json and the agent files stayed on the old version (#110). A re-scaffold
    # writes the stamp itself, so there is nothing left to sync here.
    if scaffold_result is None and scaffold_version != current_version:
        report_note = (f"config.json still says {scaffold_version}; no re-scaffold ran, so "
                       f"the generated files were not rewritten for {current_version}")
    else:
        report_note = None

    # 8. Report
    report = {
        "frameworkVersion": {
            "scaffolded": scaffold_version,
            "current": current_version,
            "manifest": manifest.get("frameworkVersion"),
        },
        "breakingChange": breaking_result,
        "drift": {
            "changed": drift_result.get("changed", []),
            "removed": drift_result.get("removed", []),
            "orphaned": drift_result.get("orphaned", []),
            "skipped": drift_result.get("skipped", []),
            "locallyModified": drift_result.get("locallyModified", []),
            "versionChanged": drift_result.get("versionChanged"),
            "configChanged": drift_result.get("configChanged"),
            "invalid": drift_result.get("invalid", 0),
            "newItems": drift_result.get("newItems", []),
        },
        "newStacks": new_stacks,
        # Derived, never recomputed: a second copy of the gate condition can disagree with
        # the gate, and did — reporting a re-scaffold that never ran.
        "reScaffolded": scaffold_result is not None,
    }
    if args.force:
        report["forced"] = True
    if report_note:
        report["note"] = report_note
    if scaffold_result:
        report["scaffold"] = scaffold_result
    if hermes_links["created"] or hermes_links["removed"]:
        report["hermesSkillLinks"] = hermes_links
    # Report-only, like newStacks: the listing budget a never-invoked skill spends is real, but
    # config.json is project-owned and a refresh does not rewrite it (#172).
    delivered = delivered_skills(manifest, scaffold_result)
    usage = su.report(target, delivered)
    if usage:
        report["skillUsage"] = usage
    # Report-only too: an optIn skill is never drift, so this listing is the only channel
    # telling a project one exists and naming the config edit that adds it.
    offered = bl.available_opt_in(root, delivered)
    if offered:
        report["availableOptIn"] = offered
    copies = report_framework_copies(root, args.prune_cache)
    if copies:
        report["frameworkCopies"] = copies
    # Nothing else will ever reach these: an orphaned namespace's project is gone, so no
    # den-refresh can run there again. Present only when one exists.
    namespaces = report_hermes_namespaces(root, args.prune_namespaces)
    if namespaces:
        report["hermesNamespaces"] = namespaces

    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())