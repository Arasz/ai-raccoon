#!/usr/bin/env python3
"""Find project additions that are candidates to contribute back to the framework.

Diffs the target repo's .ai-badger/ tree against its manifest.json (what the framework
originally placed). Emits candidates as JSON to stdout for the agent to classify
(agnostic/generalizable/project-specific), generalize, and place. MECHANICAL: no LLM.

A candidate is:
  - NEW: a managed-feature file present in .ai-badger/ but not recorded in the manifest.
  - CHANGED: a managed file whose current content hash differs from the manifest hash.

Usage: detect_additions.py [--target <dir>] [--root <framework>]
"""
from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path
from typing import Dict, List, Set, Tuple


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

# managed dirs inside .ai-badger that map to framework features
MANAGED = {
    "agents": "personas",
    "instructions": "instructions",
    "invariants": "invariants",
    "skills": "skills",
    "plugins": "plugins",
}

# provenance record for skills synced from Hermes (see learned-skills-sync plan §2.2)
LEARNED_MANIFEST_REL = Path("skills-data") / "hermes" / "learned.json"


def _load_learned_records(aib: Path) -> Dict[str, Dict]:
    """Map skill name -> its learned.json record; empty when absent or unreadable.

    Provenance is a bonus, never a precondition: a hand-broken learned.json must not
    take down candidate detection for the whole repo.
    """
    path = aib / LEARNED_MANIFEST_REL
    if not path.exists():
        return {}
    try:
        skills = bl.load_json(path).get("skills", [])
        return {rec["name"]: rec for rec in skills if isinstance(rec, dict) and "name" in rec}
    except (ValueError, OSError, AttributeError):
        return {}


def _learned_skill_candidates(aib: Path, target: Path) -> Tuple[List[Dict], Set[str]]:
    """One candidate per .ai-badger/skills/learned/<category>/<name>/ dir (research C9).

    Returns the candidates plus the set of file paths they cover, so the per-file
    loop below can skip them instead of emitting one candidate per file.
    """
    learned_root = aib / "skills" / "learned"
    candidates: List[Dict] = []
    covered: Set[str] = set()
    if not learned_root.is_dir():
        return candidates, covered

    records = _load_learned_records(aib)
    for category_dir in sorted(p for p in learned_root.iterdir() if p.is_dir()):
        for skill_dir in sorted(p for p in category_dir.iterdir() if p.is_dir()):
            for f in skill_dir.rglob("*"):
                if f.is_file():
                    covered.add(f.relative_to(target).as_posix())
            candidate = {
                "status": "new", "feature": "skills",
                "path": skill_dir.relative_to(target).as_posix(),
                "name": skill_dir.name, "origin": "hermes-learned",
            }
            record = records.get(skill_dir.name)
            if record and record.get("sourcePath"):
                candidate["sourcePath"] = record["sourcePath"]
            candidates.append(candidate)
    return candidates, covered


def main(argv=None) -> int:
    """CLI entry point: emit new/changed contribution candidates for --target as JSON."""
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--target", default=".")
    ap.add_argument("--root")
    args = ap.parse_args(argv)
    target = Path(args.target).resolve()
    aib = target / ".ai-badger"
    if not (aib / "manifest.json").exists():
        print(json.dumps({
            "error": "no .ai-badger/manifest.json — repo not scaffolded by ai-badger",
        }))
        return 1

    manifest = bl.load_json(aib / "manifest.json")
    entries = manifest.get("entries", [])
    # split manifest targets into files vs directories (skills scaffold as directories)
    file_targets: Dict[str, Dict] = {}
    dir_targets: Dict[str, Dict] = {}
    for e in entries:
        # Source-hashed entries (templates, adjustments) record the framework script's hash,
        # not the artifact's — no project file can ever match it, so comparing would report
        # a change that never clears.
        if bl.feature_type(e["feature"]).hashes_source:
            continue
        tp = target / e["target"]
        (dir_targets if tp.is_dir() else file_targets)[e["target"]] = e

    def under_dir_target(rel: str) -> bool:
        """Return True if rel is inside a directory entry, EXCEPT extension files.

        Extension files have per-file manifest entries and must fall through
        to the per-file comparison loop (#65).
        """
        for d in dir_targets:
            if rel == d:
                return True
            if rel.startswith(d + "/"):
                # Allow extension files to fall through
                suffix = rel[len(d) + 1:]
                if suffix.startswith("extensions/"):
                    return False
                return True
        return False

    candidates: List[Dict] = []

    # directory-level entries (skills): one changed candidate per whole skill
    for rel, entry in sorted(dir_targets.items()):
        dest_dir = target / rel
        if not dest_dir.is_dir():
            continue
        # Use dir_content_hash with same exclusions as drift detection.
        # Also exclude extensions/ (config-driven, not project additions) and whatever
        # another entry wrote in here — an adjustment's file is not a contribution (#224).
        try:
            fingerprint = bl.dir_content_hash(
                dest_dir, exclude=bl.SKILL_EXCLUDE_PATTERNS + ["extensions"],
                exclude_rel=(bl.nested_entry_targets(entries, rel)
                             + list(entry.get("projectOwned") or ())),
            )
            dest_hash = fingerprint["content_hash"]
        except (ValueError, OSError):
            dest_hash = bl.sha256_file(dest_dir)
        if dest_hash != entry.get("hash"):
            candidates.append({
                "status": "changed", "feature": entry["feature"], "path": rel,
                "name": entry["name"], "originStack": entry["stack"],
                "originSource": entry["source"],
            })

    # learned-skill dirs: one candidate per skill, not one per contained file
    learned_candidates, learned_covered = _learned_skill_candidates(aib, target)
    candidates.extend(learned_candidates)

    # file-level entries + genuinely new files in managed dirs
    for subdir, feature in MANAGED.items():
        base = aib / subdir
        if not base.is_dir():
            continue
        for f in sorted(base.rglob("*")):
            if not f.is_file():
                continue
            # `.ai-badger/skills/` is a skill tree, so the authoring conventions apply there
            # and nowhere else: elsewhere `tests`/`evals` name ordinary project content (#224).
            patterns = (bl.SKILL_EXCLUDE_PATTERNS if subdir == "skills"
                        else bl.ARTEFACT_EXCLUDE_PATTERNS)
            if bl.excluded_by_patterns(f.relative_to(base).as_posix(), patterns):
                continue  # an OS dropping or a build artefact is not a contribution (#224)
            rel = f.relative_to(target).as_posix()
            if rel in learned_covered:
                continue  # part of a learned-skill dir — handled above
            if under_dir_target(rel):
                continue  # part of a scaffolded skill dir — handled above
            entry = file_targets.get(rel)
            if entry is None:
                candidates.append({
                    "status": "new", "feature": feature,
                    "path": rel, "name": f.stem,
                    "suggestedGeneralization": (
                        "review for project-specific tokens before contributing"
                    ),
                })
            elif bl.sha256_file(f) != entry.get("hash"):
                candidates.append({
                    "status": "changed", "feature": feature, "path": rel,
                    "name": entry["name"], "originStack": entry["stack"],
                    "originSource": entry["source"],
                })

    print(json.dumps({
        "frameworkVersion": manifest.get("frameworkVersion"),
        "candidateCount": len(candidates),
        "candidates": candidates,
    }, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
