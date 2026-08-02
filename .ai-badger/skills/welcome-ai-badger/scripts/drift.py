#!/usr/bin/env python3
"""Report how a scaffolded project has diverged from the framework catalog.

Tier 2 of ADR-0001 decision 5: the expensive per-entry hash walk, run explicitly.
Tier 1 (the cheap version comparison) lives in the task skill's SessionStart hook,
because welcome-ai-badger is plugin-only and is not scaffolded into a project.

Newly-added catalog items are found by `detect_new_items`, which walks index.json for the
stacks a re-scaffold would actually deliver (`badger_lib.resolve_stacks`, so the always-on
`common` stack is included).

Directory-valued entries (every skill) are compared source-to-source, against the
`sourceHash` scaffold recorded — the scaffolded copy is rendered output and answers only
whether the project edited it, which is reported apart as "locallyModified" (#110). A
manifest written before `sourceHash` existed is reported as "skipped": it has no recorded
source to compare against.

Known limitation, accepted rather than solved: an upstream rename reads as "removed",
because a manifest entry's source is a path with no forwarding record.

Exit code 0 == no drift, 1 == drift found, 2 == usage error. Skipped and locally-modified
entries alone do not count as drift.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union


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
import badger_lib as bl  # pylint: disable=wrong-import-position


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


def detect_new_items(root: Path, manifest: Dict[str, Any],
                     stacks: Union[str, List[str], None] = None,
                     exclude: Optional[Dict[str, Any]] = None) -> List[Dict[str, Any]]:
    """Find catalog items in index.json that are not in the manifest.

    `stacks` is what a re-scaffold would deliver — `badger_lib.resolve_stacks(config)`, not
    `config.stacks`, which never names the always-on `common` stack. A bare string is one
    stack name, not five letters. `exclude` is the project's own `config.exclude`: a declined
    item is absent from the manifest by design and must not be reported as new, or every
    refresh nags to install what the project said no to. Returns {name, feature, stack, path}
    per unscaffolded item.
    """
    index_path = root / "index.json"
    if not index_path.exists():
        return []

    try:
        index = bl.load_json(index_path)
    except (ValueError, OSError):
        return []

    # Build set of (stack, feature, name) from manifest
    manifest_keys = set()
    for entry in manifest.get("entries", []):
        manifest_keys.add((entry.get("stack"), entry.get("feature"), entry.get("name")))

    check_stacks = {stacks} if isinstance(stacks, str) else set(stacks or ())
    declined = exclude or {}
    new_items: List[Dict[str, Any]] = []

    for stack_name, stack_data in index.get("stacks", {}).items():
        if stack_name not in check_stacks:
            continue
        # Feature types the registry marks as reportable (skips index "meta")
        for feature in bl.DRIFT_NEW_FEATURES:
            items = stack_data.get(feature, [])
            for item in items:
                if item.get("name") in (declined.get(feature) or ()):
                    continue
                # An optIn skill is absent from the manifest until a project names it in
                # config.include, so reporting it as new would nag on every refresh and
                # never clear — a re-scaffold cannot deliver what nobody asked for.
                if item.get("scope") == bl.SKILL_SCOPE_OPT_IN:
                    continue
                key = (stack_name, feature, item.get("name"))
                if key not in manifest_keys:
                    new_items.append({
                        "name": item["name"],
                        "feature": feature,
                        "stack": stack_name,
                        "path": item.get("path", ""),
                    })

    return sorted(new_items, key=lambda x: (x["stack"], x["feature"], x["name"]))


def detect_new_stacks(target: Path, root: Path,
                      config_stacks: Optional[List[str]] = None,
                      ignore: Optional[List[str]] = None) -> List[str]:
    """Find stacks that have detection signals in the target but are missing from config.

    Reuses detect.py's detection logic against the framework index, then returns
    only stacks that are in the index but not in the project's current config.
    Stacks in the ignore list are excluded from results.

    `config_stacks` must be `badger_lib.delivering_stacks(config)`, not `config.get("stacks")`
    alone — an agent name is a catalog stack too (see `is_orphaned`), and a caller that passes
    `config.stacks` reports every configured agent as a false-positive new stack (#134).
    """
    index_path = root / "index.json"
    if not index_path.exists():
        return []

    try:
        index = bl.load_json(index_path)
    except (ValueError, OSError):
        return []

    # Load detect.py and run stack detection against the target
    detect_mod = _load_script(
        "features/common/skills/welcome-ai-badger/scripts/detect.py", root
    )
    detected = detect_mod.detect_stacks(target, index)
    detected = detect_mod.expand_requires(detected, index)

    # Filter to stacks known by the index but missing from config
    known = set(index.get("stacks", {}).keys())
    current = set(config_stacks or [])
    ignored = set(ignore or [])
    return [s for s in detected if s in known and s not in current and s not in ignored]


def project_exclusions(target: Optional[Path]) -> Dict[str, Any]:
    """What a scaffolded project's own config.json declines; empty when there is none to read.

    Read from the target rather than passed in, so every caller of `compare` reports the
    project's declarations without having to know they exist.
    """
    if target is None:
        return {}
    config_path = target / ".ai-badger" / "config.json"
    if not config_path.is_file():
        return {}
    try:
        return bl.exclusions(bl.load_json(config_path))
    except (ValueError, OSError):
        return {}


def _within(parent: Path, candidate: Path) -> bool:
    """True when `candidate` resolves to `parent` itself or something inside it."""
    try:
        candidate.resolve().relative_to(parent.resolve())
    except (ValueError, OSError):
        return False
    return True


def _project_copy(target: Optional[Path], entry: Dict[str, Any], source_rel: str,
                  notes: List[str]) -> Optional[Path]:
    """The project's own copy of a directory entry, or None when there is none to hash.

    `Path("/a") / "/etc"` is `/etc`: a crafted manifest target must never steer the hasher
    out of the project (security I1), so a target that escapes is refused and noted.
    """
    if target is None:
        return None
    target_rel = entry.get("target")
    if not target_rel:
        return None
    target_path = target / target_rel
    if not _within(target, target_path):
        notes.append(
            f"manifest entry {source_rel}: target resolves outside the project — "
            f"the project's copy was not hashed"
        )
        return None
    return target_path if target_path.is_dir() else None


def _differs(fingerprint: Dict[str, Any], entry_hash: Optional[str],
             meta: Optional[Dict[str, Any]], compare_dir_count: bool = True) -> bool:
    """True when a directory fingerprint disagrees with a recorded hash + structural meta.

    `compare_dir_count` is False once another entry owns part of the tree: the recorded count
    was taken before that entry wrote, so the two describe different trees and the number is
    unanswerable rather than wrong (#230). Nothing is lost — an added directory holding a file
    moves `file_count` and `content_hash` anyway, and git cannot deliver an empty one.
    """
    if meta and fingerprint["file_count"] != meta.get("file_count"):
        return True
    if meta and compare_dir_count and fingerprint["dir_count"] != meta.get("dir_count"):
        return True
    return fingerprint["content_hash"] != entry_hash


def _dir_entry_verdict(source: Path, entry: Dict[str, Any], project_copy: Optional[Path],
                       owned_elsewhere: Optional[List[str]] = None
                       ) -> Tuple[Optional[bool], bool]:
    """(has the framework moved ahead?, has the project edited its copy?) for a skill.

    The first is None when the manifest predates `sourceHash`: nothing recorded what the
    framework looked like, so the question is unanswerable rather than answered "no" (#110).

    `owned_elsewhere` names paths inside the project's copy that other manifest entries
    wrote. Only the local question excludes them: the framework source never received them,
    and hashing them would answer "did the project edit this?" with the adjustments the
    scaffolder itself ran (#224).
    """
    exclude = bl.SKILL_EXCLUDE_PATTERNS + ["extensions"]
    source_hash = entry.get("sourceHash")
    if source_hash is None:
        if project_copy is not None:
            return None, False
        # Nothing scaffolded to hash, so the recorded hash is read as the source's own.
        return _differs(bl.dir_content_hash(source, exclude=bl.SKILL_EXCLUDE_PATTERNS),
                        entry.get("hash"), entry.get("dirMeta")), False
    moved = _differs(bl.dir_content_hash(source, exclude=exclude), source_hash,
                     entry.get("sourceMeta"))
    edited = project_copy is not None and _differs(
        bl.dir_content_hash(project_copy, exclude=exclude, exclude_rel=owned_elsewhere),
        entry.get("hash"), entry.get("dirMeta"), compare_dir_count=not owned_elsewhere)
    return moved, edited


def _version_drift(root: Path, manifest: Dict[str, Any]) -> Optional[Dict[str, str]]:
    """Report a frameworkVersion difference; None when it matches or cannot be read.

    Version-only movement is real drift: it is rendered into manifest.json and the
    `Scaffolded by ai-badger X` stamp of every generated agent file (#110).
    """
    scaffolded = manifest.get("frameworkVersion")
    try:
        current = (root / "VERSION").read_text(encoding="utf-8").strip()
    except OSError:
        return None
    if not scaffolded or not current or scaffolded == current:
        return None
    return {"scaffolded": scaffolded, "current": current}


def _config_drift(target: Optional[Path],
                  manifest: Dict[str, Any]) -> Optional[Dict[str, Optional[str]]]:
    """Report a config.json that no longer matches the one the scaffold was built from.

    Unreadable or unparseable is not drift: the `except` below is what makes a missing config
    safe, and `is_file()` only spares it the exception.
    """
    if target is None:
        return None
    config_path = target / ".ai-badger" / "config.json"
    if not config_path.is_file():
        return None
    try:
        current = bl.config_hash(bl.load_json(config_path))
    except (ValueError, OSError):
        return None
    recorded = manifest.get("configHash")
    if recorded == current:
        return None
    return {"recorded": recorded, "current": current}


def _mcp_index_migration_note(target: Optional[Path]) -> Optional[str]:
    """Note a not-yet-migrated legacy mcp-tools.yaml — never converted here (issue #145).

    `.ai-badger/mcp-tools.yaml`/`.json` is project-owned, same precedent as a config edit
    being drift rather than silence (0.46.0): silently rewriting it during a refresh is
    exactly what the seed-once machinery exists to prevent. Silent once the JSON file
    exists, whichever produced it.
    """
    if target is None:
        return None
    aib = target / ".ai-badger"
    if (aib / "mcp-tools.json").exists() or not (aib / "mcp-tools.yaml").exists():
        return None
    return (
        "mcp-tools.yaml is a legacy MCP tool index — run `mcp-index migrate` (or any "
        "mcp-index write command) to convert it to mcp-tools.json. den-refresh does not "
        "convert it: the index is project-owned."
    )


def compare(root: Path, manifest: Dict[str, Any],
            stacks: Optional[List[str]] = None,
            target: Optional[Path] = None,
            delivering: Optional[List[str]] = None) -> Dict[str, Any]:
    """Diff an already-parsed manifest against the framework's current catalog content.

    Framework drift — `changed`, `removed`, `versionChanged` — is always measured against
    the framework: directory entries (skills) re-hash the source and compare it to the
    entry's `sourceHash`, never to the project's own copy (#110). The scaffolded copy
    answers a different question and is reported separately as `locallyModified`.

    When ``stacks`` is provided, also detects new items via index.json — minus whatever the
    project's own config.json declines in ``exclude``.

    ``delivering`` is `badger_lib.delivering_stacks(config)`: every stack the project draws
    from, agents included. It drives ``orphaned`` only, and is separate from ``stacks`` because
    an agent's catalog items are delivered without ever being new. Without it nothing is
    reported as orphaned — a config edit cannot be inferred from the framework alone.
    """
    changed: List[str] = []
    removed: List[str] = []
    orphaned: List[str] = []
    skipped: List[str] = []
    locally_modified: List[str] = []
    notes: List[str] = []
    invalid = 0
    for entry in manifest.get("entries", []):
        source_rel = entry.get("source")
        entry_hash = entry.get("hash")
        if source_rel is None or entry_hash is None:
            invalid += 1
            continue
        if delivering is not None and bl.is_orphaned(entry, delivering):
            # Leaving, so upstream drift on it is noise — the re-scaffold prunes it.
            orphaned.append(source_rel)
            continue
        source = root / source_rel
        if not source.exists():
            removed.append(source_rel)
            continue
        if source.is_dir():
            try:
                moved, edited = _dir_entry_verdict(
                    source, entry, _project_copy(target, entry, source_rel, notes),
                    bl.nested_entry_targets(manifest.get("entries", []),
                                            entry.get("target") or ""),
                )
            except (ValueError, OSError):
                skipped.append(source_rel)
                continue
            if moved is None:
                skipped.append(source_rel)
                notes.append(
                    f"manifest entry {source_rel}: recorded before source hashing — "
                    f"re-scaffold to make it comparable to the framework"
                )
            elif moved:
                changed.append(source_rel)
            if edited:
                locally_modified.append(source_rel)
            continue
        if bl.sha256_file(source) != entry_hash:
            changed.append(source_rel)
    mcp_note = _mcp_index_migration_note(target)
    if mcp_note:
        notes.append(mcp_note)
    # De-duplicated: adjustments record one entry per written file, all naming one source
    # script, so a single upstream edit would otherwise print the same line ten times.
    result = {
        "changed": sorted(set(changed)),
        "removed": sorted(set(removed)),
        "orphaned": sorted(set(orphaned)),
        "skipped": sorted(set(skipped)),
        "locallyModified": sorted(set(locally_modified)),
        "versionChanged": _version_drift(root, manifest),
        "configChanged": _config_drift(target, manifest),
        "invalid": invalid,
        "notes": notes,
    }
    if stacks is not None:
        result["newItems"] = detect_new_items(root, manifest, stacks,
                                              exclude=project_exclusions(target))
    return result


def main(argv: Optional[List[str]] = None) -> int:
    """Print drift between a scaffolded target and the framework catalog; return an exit code."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", help="framework repo root (default: autodetect)")
    parser.add_argument("--target", required=True, help="scaffolded project root")
    args = parser.parse_args(argv)

    root = Path(args.root).resolve() if args.root else bl.find_root()
    target = Path(args.target).resolve()
    manifest_path = target / ".ai-badger" / "manifest.json"
    if not manifest_path.exists():
        print(f"no manifest at {manifest_path} — is this a scaffolded project?")
        return 2

    try:
        manifest = bl.load_json(manifest_path)
    except (ValueError, OSError) as exc:
        print(f"could not read manifest at {manifest_path}: {exc}")
        return 2

    scaffold_version = manifest.get("frameworkVersion", "?")
    current_version = (root / "VERSION").read_text(encoding="utf-8").strip()
    print(f"scaffolded from {scaffold_version}; framework here is {current_version}")

    # Read config for stacks (needed for new items detection)
    config_path = target / ".ai-badger" / "config.json"
    stacks = []
    if config_path.exists():
        try:
            config = bl.load_json(config_path)
            stacks = bl.resolve_stacks(config)
        except (ValueError, OSError):
            pass

    result = compare(root, manifest, stacks=stacks if stacks else None, target=target)
    for label in ("changed", "removed"):
        for path in result[label]:
            print(f"  {label:8} {path}")
    if result.get("newItems"):
        for item in result["newItems"]:
            print(f"  new      {item['stack']}/{item['feature']}/{item['name']}")
    if result["locallyModified"]:
        print("locally modified: this project edited its own copy — a re-scaffold overwrites "
              "these; move anything worth keeping into project-local.md first")
        for path in result["locallyModified"]:
            print(f"  local    {path}")
    if result["skipped"]:
        print("skipped entries are directory-valued and predate source hashing: nothing "
              "records what the framework looked like, so they cannot be compared to it")
        for path in result["skipped"]:
            print(f"  skipped  {path}")
    if result["invalid"]:
        n = result["invalid"]
        print(f"  invalid  {n} manifest entr{'y' if n == 1 else 'ies'} missing source/hash "
              "— not checked")

    if result["versionChanged"]:
        print(f"  version  {scaffold_version} → {current_version} is stamped into "
              f"manifest.json and every generated agent file")

    if result["configChanged"]:
        if result["configChanged"]["recorded"] is None:
            print("  config   this scaffold predates the recorded config hash — one "
                  "re-scaffold records a baseline")
        else:
            print("  config   .ai-badger/config.json declares something this scaffold does "
                  "not have — re-scaffold to apply it")

    has_drift = bool(result["changed"] or result["removed"] or result.get("newItems")
                     or result["versionChanged"] or result["configChanged"])
    if not has_drift:
        if result["skipped"]:
            n = len(result["skipped"])
            entry_word = "y was" if n == 1 else "ies were"
            print(f"no drift among the entries that could be compared — "
                  f"{n} skipped entr{entry_word} not checked")
        else:
            print("no drift — every scaffolded item matches the framework's current content")
        return 0
    print("re-scaffold with welcome-ai-badger to pick these up; review the diff before committing")
    return 1


if __name__ == "__main__":
    sys.exit(main())
