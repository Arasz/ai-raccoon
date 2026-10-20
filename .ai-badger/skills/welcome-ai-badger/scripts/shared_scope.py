#!/usr/bin/env python3
"""Share one ai-badger scope across repositories: from the parent side, or explicitly."""
from __future__ import annotations

import argparse
import copy
import json
import os
import uuid
from pathlib import Path
from typing import Any, Optional

from scaffold import DEFAULT_SKILLS, FRAMEWORK_ROOT, Scaffolder
from detect import propose_config
import badger_lib as bl
import project_id


def read_object(path: Path) -> dict[str, Any]:
    """Read one JSON object, refusing malformed profiles."""
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"expected an object: {path}")
    return value


def members(target: Path, names: list[str]) -> list[Path]:
    """Validate explicit, distinct repository roots inside the collection."""
    result: list[Path] = []
    for name in names:
        relative = Path(name)
        if relative.is_absolute() or ".." in relative.parts:
            raise ValueError(f"repository must be relative to collection: {name}")
        child = (target / relative).resolve()
        if child == target or not child.is_relative_to(target):
            raise ValueError(f"repository escapes collection or selects itself: {name}")
        if any(child == prior or child.is_relative_to(prior) or prior.is_relative_to(child)
               for prior in result):
            raise ValueError(f"duplicate or overlapping repository: {name}")
        if not (child / ".git").exists():
            raise ValueError(f"not a repository root: {child}")
        if (child / ".ai-badger").is_symlink():
            raise ValueError(f"shared child scaffold must be a real directory: {child}")
        result.append(child)
    return result


def merge(parent: dict[str, Any], overrides: dict[str, Any]) -> dict[str, Any]:
    """Merge objects recursively; an authored list replaces the inherited list."""
    result = copy.deepcopy(parent)
    for key, value in overrides.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = merge(result[key], value)
        else:
            result[key] = copy.deepcopy(value)
    return result


def descriptor(child: Path, root: Path) -> dict[str, Any]:
    """Load authored child inputs, separate from the generated effective profile."""
    path = child / ".ai-badger" / "shared-scope.json"
    if path.exists():
        return validate_descriptor(read_object(path), root)
    config = read_object(child / ".ai-badger" / "config.json")
    return validate_descriptor({"mode": "inherit", "localStacks": config["stacks"],
                                 "overrides": {key: value for key, value in config.items()
                                               if key not in {"stacks", "frameworkVersion", "$schema"}}},
                               root)


def without_empty_values(settings: dict[str, Any]) -> dict[str, Any]:
    """The settings a detection contributes, without the empty values it always produces.

    A detection has no summary, no domain and no persona routing, and a list replaces an
    inherited list — so keeping the empty values would wipe what the parent authored.
    """
    result: dict[str, Any] = {}
    for key, value in settings.items():
        if isinstance(value, dict):
            nested = without_empty_values(value)
            if nested:
                result[key] = nested
        elif isinstance(value, list):
            if value:
                result[key] = value
        elif value is None or value == "":
            continue
        else:
            result[key] = value
    return result


def detected_descriptor(child: Path, root: Path) -> dict[str, Any]:
    """The authored inputs of a child that has none yet: a detection of the child."""
    proposed = propose_config(child, root)
    return validate_descriptor({"mode": "inherit", "localStacks": proposed["stacks"],
                                 "overrides": without_empty_values(
                                     {key: value for key, value in proposed.items()
                                      if key not in {"stacks", "frameworkVersion", "$schema"}})},
                                root)


def child_inputs(child: Path, root: Path) -> dict[str, Any]:
    """The child's own settings: its descriptor, its own config, or a fresh detection.

    A child that is already a member carries its own descriptor, so its authored stacks
    and overrides survive every refresh. A child that is only scaffolded carries its own
    config: its stacks are its own, and its other settings are its overrides. A child that
    has neither is new, and a detection describes it.
    """
    path = child / ".ai-badger" / "shared-scope.json"
    if path.exists():
        return descriptor(child, root)
    if (child / ".ai-badger" / "config.json").exists():
        return descriptor(child, root)
    return detected_descriptor(child, root)


def validate_descriptor(value: dict[str, Any], root: Path) -> dict[str, Any]:
    """The descriptor is a closed shape: an unknown field is a mistake, not an extension."""
    errors = bl.validate(value, bl.load_json(root / "schemas" / "shared-scope.schema.json"))
    if errors:
        raise ValueError("invalid shared-scope descriptor: " + "; ".join(errors))
    return value


def validate_config(config: dict[str, Any], root: Path) -> None:
    """Validate an effective profile before scaffold writes."""
    errors = bl.validate(config, bl.load_json(root / "schemas" / "config.schema.json"))
    if errors:
        raise ValueError("invalid shared config: " + "; ".join(errors))


def plan_identity(child: Path, *, mint_without_bank: bool = False) -> tuple[str, str]:
    """Decide the child's own identity without writing anything.

    One decision, made once: the write path never re-decides, so a lookup that comes
    back ambiguous or unavailable cannot turn into a minted id on the way in. `keep` is
    the child's own id, `reuse` the bank's id for that name, `mint` a fresh local one
    for a name the bank does not know.

    `mint_without_bank` covers a machine with no ai-raccoon at all, or one whose bank
    access is switched off: there is no answer to wait for, so the parent-side bootstrap
    mints a local id instead of refusing every child. An answer the bank gave and cannot
    be trusted on still refuses.
    """
    aib = child / ".ai-badger"
    try:
        current = (aib / "project-id").read_text(encoding="utf-8").strip()
    except OSError:
        current = ""
    if current:
        return ("keep", current)
    name = project_id.project_name(child)
    result = project_id.lookup_project_id(name)
    if result.kind == "hit" and result.id:
        return ("reuse", result.id)
    if result.kind == "not-found" or (mint_without_bank and result.kind in {"missing", "off"}):
        return ("mint", str(uuid.uuid4()))
    detail = result.note or "the canonical lookup did not answer"
    raise ValueError(f"refusing to enroll {child}: it has no identity of its own and "
                     f"{detail}. Give the child its own identity first, then enroll it.")


def plan_parent_identity(parent: Path) -> tuple[str, str]:
    """Decide the collection's own identity without writing anything.

    The collection is a scope, not a project: the bank holds projects, and the parent's
    id only has to be stable, so that a child can name the collection it belongs to. It
    is therefore kept when the parent has one and minted locally otherwise — the parent
    never asks the bank for a name it does not own.
    """
    try:
        current = (parent / ".ai-badger" / "project-id").read_text(encoding="utf-8").strip()
    except OSError:
        current = ""
    if current:
        return ("keep", current)
    return ("mint", str(uuid.uuid4()))


def commit_identity(child: Path, plan: tuple[str, str], register: bool) -> None:
    """Write the decided identity, and register it with the bank only when asked."""
    kind, value = plan
    if kind == "keep":
        return
    aib = child / ".ai-badger"
    if kind == "mint" and register:
        project_id.register_project_id(value, project_id.project_name(child))
    aib.mkdir(parents=True, exist_ok=True)
    (aib / "project-id").write_text(f"{value}\n", encoding="utf-8")


def own_id(scope: Path) -> Optional[str]:
    """The project id of a scope, when it has one."""
    try:
        value = (scope / ".ai-badger" / "project-id").read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return value or None


def enrolled_elsewhere(child: Path, source: Path) -> Optional[str]:
    """The collection a child is already enrolled in, when it is not this one.

    The collection's own project id is the identity: a child that arrives carrying
    another collection's id was enrolled there, and re-enrolling it here would leave
    two collections each claiming it.
    """
    path = child / ".ai-badger" / "shared-scope.json"
    if not path.exists():
        return None
    profile = read_object(path)
    recorded = profile.get("parentId")
    mine = own_id(source.parent)
    if isinstance(recorded, str) and recorded and recorded != (mine or ""):
        return f"another collection (project {recorded})"
    origin = profile.get("source")
    if isinstance(origin, str) and origin and mine is None:
        other = (child / origin).resolve()
        if other != source.resolve():
            return str(other)
    return None


def detach_links(child: Path, source: Path) -> None:
    """Turn only previously recorded parent links back into private files."""
    manifest_path = child / ".ai-badger" / "manifest.json"
    if not manifest_path.is_file():
        return
    links = read_object(manifest_path).get("sharedScope", {}).get("links", [])
    if not isinstance(links, list):
        raise ValueError("shared-scope link ledger must be a list")
    checked: list[tuple[Path, str]] = []
    root = child.resolve()
    parent = source.resolve()
    for entry in links:
        if not isinstance(entry, dict):
            raise ValueError("shared-scope link record must be an object")
        relative = Path(entry.get("path", ""))
        raw_target = entry.get("target")
        if (not relative.parts or relative.is_absolute() or ".." in relative.parts
                or not isinstance(raw_target, str) or not raw_target):
            raise ValueError("invalid shared-scope link record")
        destination = child / relative
        if not destination.absolute().is_relative_to(root):
            raise ValueError(f"shared-scope link escapes child: {relative}")
        cursor = destination.parent
        while cursor not in (child, cursor.parent):
            if cursor.is_symlink():
                raise ValueError(f"shared-scope link has a symlink ancestor: {relative}")
            cursor = cursor.parent
        canonical = (destination.parent / raw_target).resolve(strict=False)
        recorded = (parent / entry.get("source", "")).resolve()
        if not canonical.is_relative_to(parent) or canonical != recorded:
            raise ValueError(f"shared-scope link target escapes its parent: {relative}")
        if destination.is_symlink():
            if os.readlink(destination) != raw_target:
                raise ValueError(f"shared-scope link was replaced: {relative}. Delete the file "
                                 f"and sync again, or prune the child from the collection.")
        elif destination.exists():
            raise ValueError(f"shared-scope link was replaced with a file: {relative}. Delete the "
                             f"file and sync again, or prune the child from the collection.")
        checked.append((destination, raw_target))

    for destination, _raw_target in checked:
        if not destination.is_symlink():
            continue
        if destination.exists():
            content = destination.read_bytes()
            destination.unlink()
            destination.write_bytes(content)
        else:
            # The parent file is gone (the union shrank, or the parent was edited):
            # there is no content to copy, so the owned link is dropped and the
            # renderer writes the child's own file, or leaves it absent.
            destination.unlink()


def link_instructions(child: Path, source: Path, mode: str) -> None:
    """Link matching instruction files and record exact ownership in the manifest."""
    aib = child / ".ai-badger"
    links: list[dict[str, str]] = []
    source_instructions = source / "instructions"
    if source_instructions.is_dir():
        for parent_file in sorted(source_instructions.rglob("*.instructions.md")):
            relative = parent_file.relative_to(source)
            child_file = aib / relative
            if parent_file.is_symlink() or any(
                    ancestor.is_symlink() for ancestor in child_file.parents
                    if ancestor != child):
                continue
            if not child_file.is_file() or child_file.is_symlink():
                continue
            if child_file.read_bytes() != parent_file.read_bytes():
                continue
            raw_target = os.path.relpath(parent_file, child_file.parent)
            child_file.unlink()
            child_file.symlink_to(raw_target)
            links.append({
                "path": (Path(".ai-badger") / relative).as_posix(),
                "target": raw_target,
                "source": relative.as_posix(),
            })
    manifest_path = aib / "manifest.json"
    manifest = read_object(manifest_path)
    manifest["sharedScope"] = {
        "mode": mode,
        "source": os.path.relpath(source, child),
        "links": links,
    }
    bl.dump_json(manifest_path, manifest)


def plan_config(authored: dict[str, Any], parent: dict[str, Any], root: Path,
                mode: str) -> dict[str, Any]:
    """The effective config of one child, validated before anything is written.

    The parent's defaults, the child's own overrides, and the stacks the mode decides:
    an inherited child carries the parent's union, a synced child carries its own.
    """
    config = merge(parent, authored["overrides"])
    config["stacks"] = parent["stacks"] if mode == "inherit" else authored["localStacks"]
    validate_config(config, root)
    return config


def materialize(child: Path, source: Path, authored: dict[str, Any],
                config: dict[str, Any], root: Path, mode: str) -> None:
    """Render a private child scaffold from its planned effective config."""
    detach_links(child, source)
    Scaffolder(root, child, config, DEFAULT_SKILLS, install=False).run(
        allow_shared_scope=True)
    authored = dict(authored, mode=mode, source=os.path.relpath(source, child))
    parent_id = own_id(source.parent)
    if parent_id:
        authored["parentId"] = parent_id
    bl.dump_json(child / ".ai-badger" / "shared-scope.json", authored)
    link_instructions(child, source, mode)


def is_collection_parent(child: Path) -> bool:
    """True when the child is a collection of its own, with members of its own."""
    path = child / ".ai-badger" / "shared-scope.json"
    if not path.exists():
        return False
    return read_object(path).get("mode") == "collection"


def discover_children(target: Path) -> tuple[list[Path], list[tuple[str, str]]]:
    """The direct child repositories of a collection, and the ones that cannot be enrolled.

    Discovery is one level deep: a directory that is not a direct child is not a member,
    whatever it contains. A child is a direct subdirectory that carries a `.git` entry —
    a directory or a file, so a linked worktree counts. A symlinked directory and a
    directory that is a collection of its own are reported, not enrolled: neither can
    become a member without a decision this discovery does not make.
    """
    if not target.is_dir():
        raise ValueError(f"not a directory: {target}")
    children: list[Path] = []
    skipped: list[tuple[str, str]] = []
    for entry in sorted(target.iterdir()):
        if not entry.is_dir() or not (entry / ".git").exists():
            continue
        if entry.is_symlink():
            skipped.append((entry.name, "a symlinked directory is not enrolled"))
            continue
        if is_collection_parent(entry):
            skipped.append((entry.name, "it is a collection parent itself"))
            continue
        children.append(entry)
    return children, skipped


def stack_index(names: list[str], inputs: list[dict[str, Any]]) -> dict[str, list[str]]:
    """The dir:stacks index of a collection: each member and the stacks it was found with."""
    return {name: list(child_input["localStacks"])
            for name, child_input in zip(names, inputs)}


def collect(target: Path, names: list[str], root: Path, config_path: Path | None,
            register: bool = False) -> None:
    """Recompute the parent stack union and enroll its explicit members."""
    selected = members(target, names)
    source = target / ".ai-badger"
    # Nothing is written until every member is decided: a child whose identity the
    # canonical lookup cannot resolve, or a child already enrolled in another
    # collection, stops the whole run before the first write.
    for child in selected:
        other = enrolled_elsewhere(child, source)
        if other is not None:
            raise ValueError(f"{child} is already enrolled in {other}; prune it there first")
    plans = [plan_identity(child) for child in selected]
    authored = [descriptor(child, root) for child in selected]
    parent_path = config_path or target / ".ai-badger" / "config.json"
    if parent_path.exists():
        config = copy.deepcopy(read_object(parent_path))
    else:
        config = propose_config(target, root)
    config["stacks"] = list(dict.fromkeys(stack for child in authored
                                         for stack in child["localStacks"]))
    validate_config(config, root)
    for child, plan in zip(selected, plans):
        commit_identity(child, plan, register)
    Scaffolder(root, target, config, DEFAULT_SKILLS, install=False).run()
    bl.dump_json(source / "shared-scope.json", {
        "mode": "collection", "members": [str(child.relative_to(target)) for child in selected],
        "memberStacks": stack_index([str(child.relative_to(target)) for child in selected],
                                    authored)})
    for child, inputs in zip(selected, authored):
        materialize(child, source, inputs, plan_config(inputs, config, root, "inherit"),
                    root, "inherit")


def prune(target: Path, name: str, root: Path, register: bool = False) -> None:
    """Remove one enrolled member from the collection and leave it standalone."""
    source = target / ".ai-badger"
    enrolled = read_object(source / "shared-scope.json")
    members = enrolled.get("members")
    if not isinstance(members, list):
        raise ValueError("the collection descriptor must list its members")
    if name not in members:
        raise ValueError(f"{name} is not an enrolled member of {target}")
    if len(members) == 1:
        raise ValueError(f"{name} is the last member of {target}: a collection keeps at least one "
                         f"member. Remove the collection itself if it is no longer needed.")
    child = (target / name).resolve()
    if not child.is_relative_to(target.resolve()):
        raise ValueError(f"repository escapes the collection: {name}")
    inputs = descriptor(child, root)
    detach_links(child, source)
    (child / ".ai-badger" / "shared-scope.json").unlink(missing_ok=True)
    current = read_object(child / ".ai-badger" / "config.json")
    standalone = merge(inputs["overrides"], {"stacks": inputs["localStacks"]})
    for key in ("frameworkVersion", "$schema"):
        if key in current:
            standalone[key] = current[key]
    validate_config(standalone, root)
    Scaffolder(root, child, standalone, DEFAULT_SKILLS, install=False).run()
    collect(target, [member for member in members if member != name], root, None, register)


def sync(child: Path, source: Path, root: Path, register: bool = False) -> None:
    """Render one enrolled child using its local stacks and current parent defaults."""
    plan = plan_identity(child)
    commit_identity(child, plan, register)
    inputs = descriptor(child, root)
    parent = read_object(source / "config.json")
    materialize(child, source, inputs, plan_config(inputs, parent, root, "sync"), root, "sync")


def _identity_word(plan: tuple[str, str]) -> str:
    """The report's word for an identity decision: kept, reused from the bank, or minted."""
    return {"keep": "kept", "reuse": "reused from the bank",
            "mint": "minted locally"}[plan[0]]


def discover(target: Path, root: Path) -> None:
    """Report the direct child repositories, their stacks and the union. Writes nothing."""
    children, skipped = discover_children(target)
    if not children:
        raise ValueError(f"no repository found in {target}: a child is a direct subdirectory "
                         f"that carries a .git entry")
    inputs = [child_inputs(child, root) for child in children]
    for child, child_input in zip(children, inputs):
        print(f"member {child.name}:{','.join(child_input['localStacks'])}")
    for name, reason in skipped:
        print(f"skipped {name}: {reason}")
    union = list(dict.fromkeys(stack for child_input in inputs
                               for stack in child_input["localStacks"]))
    print(f"union {','.join(union)}")


def init(target: Path, root: Path, config_path: Path | None, register: bool = False,
         dry_run: bool = False) -> None:
    """Initialize a collection from the parent side: discover, decide, then write.

    Every decision is made before the first write, so a refusal leaves the directory
    exactly as it was. The parent is scaffolded with the union of the members' stacks
    and every member is materialized from it, whether it was already a scaffolded
    repository or a fresh one.
    """
    children, skipped = discover_children(target)
    if not children:
        raise ValueError(f"no repository found in {target}: a child is a direct subdirectory "
                         f"that carries a .git entry")
    refusal = bl.shared_scope_refusal(target)
    if refusal:
        raise ValueError(refusal)
    source = target / ".ai-badger"
    # Nothing is written until every member is decided: a child whose identity the
    # canonical lookup cannot resolve, or a child already enrolled in another
    # collection, stops the whole run before the first write.
    for child in children:
        other = enrolled_elsewhere(child, source)
        if other is not None:
            raise ValueError(f"{child} is already enrolled in {other}; prune it there first")
    inputs = [child_inputs(child, root) for child in children]
    plans = [plan_identity(child, mint_without_bank=True) for child in children]
    parent_plan = plan_parent_identity(target)
    parent_path = config_path or target / ".ai-badger" / "config.json"
    if parent_path.exists():
        config = copy.deepcopy(read_object(parent_path))
    else:
        config = propose_config(target, root)
    config["stacks"] = list(dict.fromkeys(stack for child_input in inputs
                                         for stack in child_input["localStacks"]))
    validate_config(config, root)
    names = [child.name for child in children]
    # Every child's effective config is planned and validated before the first write, so
    # a member whose own settings cannot be rendered stops the run with the collection
    # exactly as it was.
    plans_config = [plan_config(child_input, config, root, child_input["mode"])
                    for child_input in inputs]
    report = [f"member {name}:{','.join(child_input['localStacks'])}"
              for name, child_input in zip(names, inputs)]
    report += [f"skipped {name}: {reason}" for name, reason in skipped]
    report.append(f"union {','.join(config['stacks'])}")
    report.append(f"parent identity: {_identity_word(parent_plan)}")
    report += [f"identity {name}: {_identity_word(plan)}"
               for name, plan in zip(names, plans)]
    if dry_run:
        for line in report:
            print(f"plan {line}")
        return
    # The collection is a scope, not a bank project: its identity is local, and only the
    # members' identities reach the bank.
    commit_identity(target, parent_plan, register=False)
    for child, plan in zip(children, plans):
        commit_identity(child, plan, register)
    Scaffolder(root, target, config, DEFAULT_SKILLS, install=False).run()
    bl.dump_json(source / "shared-scope.json", {
        "mode": "collection", "members": names, "memberStacks": stack_index(names, inputs)})
    for child, child_input, child_config in zip(children, inputs, plans_config):
        materialize(child, source, child_input, child_config, root, child_input["mode"])
    for line in report:
        print(line)


def main(argv: list[str] | None = None) -> int:
    """Run the explicit shared-scope command."""
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    collect_parser = commands.add_parser("collect")
    collect_parser.add_argument("--target", required=True)
    collect_parser.add_argument("--repo", action="append", required=True)
    collect_parser.add_argument("--config")
    collect_parser.add_argument("--root")
    collect_parser.add_argument("--register", action="store_true",
                                help="register a newly minted child id with ai-raccoon")
    sync_parser = commands.add_parser("sync")
    sync_parser.add_argument("--target", required=True)
    sync_parser.add_argument("--source", required=True)
    sync_parser.add_argument("--root")
    sync_parser.add_argument("--register", action="store_true",
                             help="register a newly minted child id with ai-raccoon")
    prune_parser = commands.add_parser("prune")
    prune_parser.add_argument("--target", required=True)
    prune_parser.add_argument("--repo", required=True)
    prune_parser.add_argument("--root")
    prune_parser.add_argument("--register", action="store_true",
                               help="register a newly minted child id with ai-raccoon")
    discover_parser = commands.add_parser("discover")
    discover_parser.add_argument("--target", required=True)
    discover_parser.add_argument("--root")
    init_parser = commands.add_parser("init")
    init_parser.add_argument("--target", required=True)
    init_parser.add_argument("--config")
    init_parser.add_argument("--root")
    init_parser.add_argument("--register", action="store_true",
                              help="register a newly minted id with ai-raccoon")
    init_parser.add_argument("--dry-run", action="store_true",
                              help="report the members, the union and the identities; write nothing")
    args = parser.parse_args(argv)
    try:
        root = Path(args.root).resolve() if args.root else FRAMEWORK_ROOT
        target = Path(args.target).resolve()
        if args.command == "collect":
            collect(target, args.repo, root,
                    Path(args.config).resolve() if args.config else None, args.register)
        elif args.command == "prune":
            prune(target, args.repo, root, args.register)
        elif args.command == "discover":
            discover(target, root)
        elif args.command == "init":
            init(target, root, Path(args.config).resolve() if args.config else None,
                 args.register, args.dry_run)
        else:
            sync(target, Path(args.source).resolve(), root, args.register)
    except (OSError, ValueError, KeyError) as exc:
        parser.exit(1, f"shared scope refused: {exc}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
