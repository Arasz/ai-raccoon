#!/usr/bin/env python3
"""mcp-index: manage the MCP tool index for ai-badger projects.

Reads whichever host CLI still lists MCP servers (`host_listings.py` owns the chain and its
measured output shapes) and produces .ai-badger/mcp-tools.json: the mcp catalog's curated tags
and intents for every tool it describes, name heuristics for the rest, and a per-server
`status` for the ones that reported no tools at all (ADR-0014 decision 7).

Commands:
  init     — create index from MCP tool list
  update   — add new tools, mark removed ones (preserves manual tags)
  validate — check all tools have proper tags and intents
  tag      — set tags for a specific tool
  intent   — set intent for a specific tool
  list     — display all tools, optionally filtered by tag
  migrate  — one-shot: convert a legacy mcp-tools.yaml into mcp-tools.json

JSON is the format going forward (docs/adr/0012 §4, issue #145); mcp-tools.yaml is read
for backward compatibility and migrated to JSON the first time any write command runs.
"""

from __future__ import annotations

import json
import os
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Optional

try:
    import yaml  # pylint: disable=import-error
except ImportError:  # pragma: no cover - exercised via a patched __import__
    yaml = None

YAML_MISSING_HINT = (
    "mcp-index needs PyYAML: pip install pyyaml (it is in engine/requirements.txt)"
)

# host_listings.py, legacy_yaml_subset.py and tool_descriptions.py sit beside this file in every
# deployment shape (sync_plugin_skills.py and the scaffold's skill installer both copy a skill's
# scripts/ whole).
sys.path.insert(0, str(Path(__file__).resolve().parent))
import host_listings as hl  # noqa: E402  pylint: disable=wrong-import-position,import-error
import tool_descriptions as td  # noqa: E402  pylint: disable=wrong-import-position,import-error
from legacy_yaml_subset import parse_legacy_yaml_subset as _parse_legacy_yaml_subset  # noqa: E402  pylint: disable=wrong-import-position,import-error

MANUAL, OK, ABSENT = td.MANUAL, td.OK, td.ABSENT


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


try:
    FRAMEWORK_ROOT: Optional[Path] = _bootstrap_lib()
except RuntimeError:  # the built-in taxonomy is the fallback; a missing root is not fatal
    FRAMEWORK_ROOT = None


def _valid_tags() -> set[str]:
    """The closed tag vocabulary, from the framework's mcp-tags.json when it is reachable."""
    return td.all_valid_tags(td.load_taxonomy(FRAMEWORK_ROOT))


def _auto_tags(tool_name: str, server_name: str = "") -> list[str]:
    """Heuristic tags for one tool name (`tool_descriptions.auto_tags`)."""
    return td.auto_tags(tool_name, server_name, _valid_tags())


def load_catalog(fw_root: Optional[Path]) -> td.Catalog:
    """The mcp catalog keyed by server then tool name (`tool_descriptions.load_catalog`)."""
    return td.load_catalog(fw_root)


def describe_tool(server: str, name: str, description: str,
                  catalog: td.Catalog) -> dict[str, Any]:
    """Tags, intent and origin for one tool, against the reachable taxonomy."""
    return td.describe_tool(server, name, description, catalog, _valid_tags())


def _quiet_sources(index: dict[str, Any]) -> list[str]:
    """`name (status)` for every source whose status is not `ok`, for the human to act on."""
    return [f"{s['name']} ({s['status']})" for s in index.get("sources", [])
            if s.get("status") not in (None, OK)]


def _report_untouched(names: list[str]) -> None:
    """Say which sources this listing could not speak for, so silence never reads as removal."""
    if names:
        print(f"  {len(names)} source(s) left untouched — this listing carries no tool detail, "
              f"so a server it does not name is not evidence of removal: {', '.join(names)}.")


def _report_quiet_sources(index: dict[str, Any]) -> None:
    """Print the non-ok sources; each status has its own remedy, so each is named."""
    quiet = _quiet_sources(index)
    if quiet:
        print(f"  {len(quiet)} server(s) reporting no tools: {', '.join(quiet)}.")


# ── MCP tool discovery ──────────────────────────────────────────────────────

def _fetch_mcp_tools(from_json: Optional[str] = None,
                     host: Optional[str] = None) -> list[dict[str, Any]]:
    """The servers a host CLI reports, or a refusal naming every source that was asked.

    `from_json` reads a saved `hermes mcp list --json` document instead of asking anyone.
    Otherwise `host_listings.discover` walks the source chain; if none of them answers there is
    nothing to index, and saying which ones were tried is the whole remedy (issue #188).
    """
    if from_json is not None:
        return hl.parse_hermes_json_listing(from_json)

    listing = hl.discover(host)
    if not listing.servers:
        tried = "".join(f"\n  - {note}" for note in listing.notes)
        print(f"ERROR: no host CLI listed any MCP server.{tried}\n"
              "  Install or fix one of the above, pass --host to pick one, or pass "
              "--from-json <listing> to index a saved `hermes mcp list --json` document.",
              file=sys.stderr)
        sys.exit(1)
    print(f"Listed {len(listing.servers)} server(s) via `{listing.label}`.", file=sys.stderr)
    for note in listing.notes:
        print(f"  (skipped {note})", file=sys.stderr)
    return listing.servers


# ── Index file operations ────────────────────────────────────────────────────

class McpIndexError(RuntimeError):
    """Base for _read_index/_write_index failures the CLI dispatch turns into an exit code."""


class LegacyYamlUnreadable(McpIndexError):
    """A legacy mcp-tools.yaml exists but cannot be safely read."""


class IndexInvalid(McpIndexError):
    """The index failed schema validation."""


class ValidationUnavailable(McpIndexError):
    """Schema validation could not run at all (no framework root, or no jsonschema)."""


VALIDATION_UNAVAILABLE_HINT = (
    "cannot validate against schemas/mcp-tools.schema.json: pass --root <framework "
    "checkout> if the framework isn't reachable, and `pip install jsonschema` if it "
    "isn't installed (engine/requirements.txt)."
)


def _index_path(target: str) -> Path:
    """Return the path to .ai-badger/mcp-tools.json for a project."""
    return Path(target) / ".ai-badger" / "mcp-tools.json"


def _legacy_index_path(target: str) -> Path:
    """Return the path to a project's legacy .ai-badger/mcp-tools.yaml, if any."""
    return Path(target) / ".ai-badger" / "mcp-tools.yaml"


def _read_legacy_yaml(path: Path) -> dict:
    """Read a legacy mcp-tools.yaml: pyyaml when present, else the verified subset parser."""
    text = path.read_text(encoding="utf-8")
    if yaml is not None:
        return yaml.safe_load(text)
    parsed = _parse_legacy_yaml_subset(text)
    if parsed is not None:
        return parsed
    raise LegacyYamlUnreadable(
        f"{path} is a legacy YAML index and PyYAML is not installed, so it cannot be "
        "safely read (it fell outside the subset this script can verify).\n"
        f"  Remedy 1: {YAML_MISSING_HINT}, then re-run.\n"
        "  Remedy 2: regenerate — `mcp-index init --target <project> --from-json "
        "<hermes mcp list --json output>`. This LOSES curated tags and intents."
    )


def _read_index(target: str) -> Optional[dict[str, Any]]:
    """Read the index: the JSON file wins when both it and a legacy YAML file exist."""
    json_path = _index_path(target)
    if json_path.exists():
        return json.loads(json_path.read_text(encoding="utf-8"))
    legacy_path = _legacy_index_path(target)
    if not legacy_path.exists():
        return None
    return _read_legacy_yaml(legacy_path)


def _read_index_safe(target: str) -> tuple[Optional[dict[str, Any]], Optional[str]]:
    """`_read_index`, turning a LegacyYamlUnreadable refusal into a message instead of a raise."""
    try:
        return _read_index(target), None
    except LegacyYamlUnreadable as exc:
        return None, str(exc)


def _try_import_badger_lib():
    """Return the badger_lib module, or None when validation cannot run at all.

    Covers both causes uniformly: FRAMEWORK_ROOT is None (nothing on sys.path to import
    from), or badger_lib itself fails to import (it imports jsonschema unguarded).
    """
    if FRAMEWORK_ROOT is None:
        return None
    try:
        import badger_lib as bl  # pylint: disable=import-outside-toplevel,import-error
    except ImportError:
        return None
    return bl


def _validate_against_schema(data: dict[str, Any]) -> Optional[list[str]]:
    """Errors from schemas/mcp-tools.schema.json, or None when validation is unavailable."""
    bl = _try_import_badger_lib()
    if bl is None:
        return None
    schema_path = FRAMEWORK_ROOT / "schemas" / "mcp-tools.schema.json"
    if not schema_path.is_file():
        return None
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    return bl.validate(data, schema)


def _write_index(target: str, data: dict[str, Any], *, require_validation: bool) -> None:
    """Validate, write JSON, and migrate a legacy YAML file if one is present.

    A source with no tools is written, not dropped: its `status` is the whole point (ADR-0014
    decision 7), and dropping it made "switched off" and "running but silent" indistinguishable.

    `require_validation` splits the write choke point in two, because this local copy of
    `atomic_write_text` exists precisely so this script runs in projects with no framework
    on sys.path (see below) — a hard requirement on FRAMEWORK_ROOT + jsonschema would
    contradict that for every command, not just the bulk ones:
      * True  (init/update): refuse outright when validation can't run, or when it finds
        errors. These commands overwrite most or all of the file from a fetched tool list,
        where an undetected schema violation is most likely and most costly.
      * False (tag/intent): best-effort. tag/intent already enforce the two fields the
        schema constrains for a single tool (tag taxonomy membership, intent length) without
        jsonschema at all; a missing framework root or jsonschema degrades to a printed note
        rather than stranding a curation edit that has nowhere else to happen.

    Local copy of `badger_lib.atomic_write_text`: this script is scaffolded into projects
    that need not have the framework on sys.path.
    """
    errors = _validate_against_schema(data)
    if errors is None:
        if require_validation:
            raise ValidationUnavailable(VALIDATION_UNAVAILABLE_HINT)
        print(f"NOTE: wrote unvalidated — {VALIDATION_UNAVAILABLE_HINT}", file=sys.stderr)
    elif errors:
        message = "index failed schema validation:\n" + "\n".join(f"  - {e}" for e in errors)
        if require_validation:
            raise IndexInvalid(message)
        print(f"WARNING: {message}", file=sys.stderr)

    path = _index_path(target)
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(data, indent=2, sort_keys=False, ensure_ascii=False) + "\n"
    handle, tmp = tempfile.mkstemp(dir=str(path.parent), prefix=path.name + ".", suffix=".tmp")
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as fh:
            fh.write(text)
        os.replace(tmp, str(path))
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)

    legacy_path = _legacy_index_path(target)
    if legacy_path.exists():
        migrated_path = legacy_path.with_name(legacy_path.name + ".migrated")
        legacy_path.replace(migrated_path)
        print(f"Migrated legacy {legacy_path.name} -> {migrated_path.name}.", file=sys.stderr)


# ── Commands ─────────────────────────────────────────────────────────────────

def _index_source(server: dict[str, Any],
                  catalog: dict[str, dict[str, dict[str, Any]]]) -> dict[str, Any]:
    """One `sources[]` entry for a freshly listed server: its status and its described tools."""
    name = server.get("name", "")
    entry: dict[str, Any] = {"name": name, "status": td.server_status(server)}
    if server.get("url"):
        entry["url"] = server["url"]
    entry["tools"] = {
        tool["name"]: describe_tool(name, tool["name"], tool.get("description", ""), catalog)
        for tool in server.get("tools", [])
    }
    return entry


def _note_missing_catalog(catalog: dict[str, dict[str, dict[str, Any]]]) -> None:
    """Say so when no catalog was reachable — otherwise "no curated tags" is silent."""
    if not catalog:
        print("NOTE: no mcp catalog found under the framework root — tags and intents come "
              "from name heuristics only.", file=sys.stderr)


def cmd_init(target: str, from_json: Optional[str] = None,
             host: Optional[str] = None) -> int:
    """Create a new index from the current MCP tool list."""
    servers = _fetch_mcp_tools(from_json, host)
    catalog = load_catalog(FRAMEWORK_ROOT)
    _note_missing_catalog(catalog)

    index = {
        "version": "0.1.0",
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sources": [_index_source(s, catalog) for s in servers],
    }

    try:
        _write_index(target, index, require_validation=True)
    except McpIndexError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    tool_count = sum(len(s["tools"]) for s in index["sources"])
    general_count = sum(
        1 for s in index["sources"]
        for t in s["tools"].values() if t["tags"] == ["general"]
    )
    print(f"Indexed {tool_count} tools across {len(index['sources'])} server(s).")
    if general_count:
        print(
            f"  {general_count} tool(s) tagged as 'general' — "
            "run 'mcp-index tag <tool> <tags...>' to curate."
        )
    _report_quiet_sources(index)
    return 0


def cmd_validate(target: str) -> int:
    """Validate the index: all tools have non-general, non-empty tags and intents."""
    index, err = _read_index_safe(target)
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        return 1
    if index is None:
        print(
            "ERROR: .ai-badger/mcp-tools.json not found. "
            "Run 'mcp-index init' first.",
            file=sys.stderr,
        )
        return 1

    errors: list[str] = []
    all_tags = _valid_tags()

    for source in index.get("sources", []):
        sname = source["name"]
        for tname, tool in source.get("tools", {}).items():
            full = f"{sname}:{tname}"
            tags = tool.get("tags", [])

            if not tags:
                errors.append(f"{full}: no tags")
            elif tags == ["general"]:
                errors.append(
                    f"{full}: still tagged 'general' — needs curation"
                )
            else:
                invalid = [t for t in tags if t not in all_tags]
                if invalid:
                    errors.append(f"{full}: invalid tags: {invalid}")

            intent = tool.get("intent", "")
            if not intent:
                errors.append(f"{full}: no intent")
            elif len(intent) < 10:
                errors.append(
                    f"{full}: intent too short ({len(intent)} chars, min 10)"
                )

    if errors:
        print(
            f"Validation failed: {len(errors)} error(s):", file=sys.stderr
        )
        for e in errors:
            print(f"  - {e}", file=sys.stderr)
        return 1

    total = sum(len(s["tools"]) for s in index.get("sources", []))
    print(f"OK: {total} tool(s) validated")
    _report_quiet_sources(index)
    return 0


def _mark_removed(tools: dict[str, dict[str, Any]], names: list[str]) -> int:
    """Mark each named tool removed, keeping its curation; return how many changed."""
    changes = 0
    for name in names:
        if tools[name].get("status") != "removed":
            tools[name]["status"] = "removed"
            changes += 1
    return changes


def _sync_tools(source: dict[str, Any], server: Optional[dict[str, Any]],
                catalog: dict[str, dict[str, dict[str, Any]]]) -> int:
    """Add newly listed tools and mark vanished ones; return the change count.

    A server the host listed without tool detail (`tools_known` False — the text-table
    fallback) is left alone entirely: "not asked" must not be read as "exposes nothing",
    which would mark every tool in the index removed.
    """
    tools = source.setdefault("tools", {})
    if server is None:
        return _mark_removed(tools, sorted(tools))
    if not server.get("tools_known", True):
        return 0
    current = {t["name"]: t.get("description", "") for t in server.get("tools", [])}
    changes = _mark_removed(tools, sorted(set(tools) - set(current)))
    for name in sorted(set(current) - set(tools)):
        tools[name] = describe_tool(source["name"], name, current[name], catalog)
        changes += 1
    return changes


def _redescribe_from_catalog(source: dict[str, Any],
                             catalog: dict[str, dict[str, dict[str, Any]]]) -> list[str]:
    """Re-describe every non-manual tool the catalog knows; return the `server:tool` names."""
    changed = []
    curated = td.catalog_tools(catalog, source["name"])
    for name, tool in source.get("tools", {}).items():
        if tool.get("origin") == MANUAL or name not in curated:
            continue
        described = describe_tool(source["name"], name, tool.get("intent", ""), catalog)
        if all(tool.get(key) == value for key, value in described.items()):
            continue
        tool.update(described)
        changed.append(f"{source['name']}:{name}")
    return changed


def _reorder_source(source: dict[str, Any]) -> None:
    """name, status, url, tools — a status behind a 40-entry tools object is unreadable."""
    ordered = {key: source[key] for key in ("name", "status", "url", "tools") if key in source}
    ordered.update({key: value for key, value in source.items() if key not in ordered})
    source.clear()
    source.update(ordered)


def _update_source(source: dict[str, Any], server: Optional[dict[str, Any]],
                   catalog: dict[str, dict[str, dict[str, Any]]]) -> tuple[int, list[str]]:
    """Restate one source's status and sync its tools; return (changes, re-described tools)."""
    was = source.get("status")
    source["status"] = ABSENT if server is None else td.server_status(server)
    changes = _sync_tools(source, server, catalog) + (1 if source["status"] != was else 0)
    redescribed = _redescribe_from_catalog(source, catalog)
    _reorder_source(source)
    return changes + len(redescribed), redescribed


def cmd_update(target: str, from_json: Optional[str] = None,
               host: Optional[str] = None) -> int:
    """Update index: add new tools, mark removed ones, restate status, preserve manual tags."""
    index, err = _read_index_safe(target)
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        return 1
    if index is None:
        print("No existing index. Running init instead.", file=sys.stderr)
        return cmd_init(target, from_json, host)

    servers = _fetch_mcp_tools(from_json, host)
    catalog = load_catalog(FRAMEWORK_ROOT)
    _note_missing_catalog(catalog)
    listed = {s.get("name", ""): s for s in servers}
    # A listing with no tool detail cannot tell "this server is gone" from "this is a different
    # host's listing", and calling it gone marks every tool in the source removed (issue #188).
    absence_is_evidence = hl.carries_tool_detail(servers)

    changes = 0
    redescribed: list[str] = []
    untouched: list[str] = []
    for source in index.get("sources", []):
        server = listed.get(source["name"])
        if server is None and not absence_is_evidence:
            untouched.append(source["name"])
            continue
        source_changes, source_redescribed = _update_source(source, server, catalog)
        changes += source_changes
        redescribed.extend(source_redescribed)
    _report_untouched(untouched)

    existing_names = {s["name"] for s in index["sources"]}
    for server in servers:
        if server.get("name") not in existing_names:
            index["sources"].append(_index_source(server, catalog))
            changes += 1

    if changes:
        ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        index["generated_at"] = ts
        try:
            _write_index(target, index, require_validation=True)
        except McpIndexError as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 1
        print(f"Updated: {changes} change(s) applied.")
        if redescribed:
            print(f"  {len(redescribed)} tool(s) re-described from the mcp catalog: "
                  f"{', '.join(redescribed)}.")
        _report_quiet_sources(index)
    else:
        print("No changes — index is up to date.")

    return 0


def cmd_tag(target: str, tool_ref: str, tags: list[str]) -> int:
    """Set tags for a specific tool."""
    if not tags:
        print("ERROR: at least one tag is required.", file=sys.stderr)
        return 2

    all_tags = _valid_tags()
    invalid = [t for t in tags if t not in all_tags]
    if invalid:
        print(
            f"ERROR: invalid tags: {invalid}. "
            f"Valid tags: {sorted(all_tags)}",
            file=sys.stderr,
        )
        return 1

    index, err = _read_index_safe(target)
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        return 1
    if index is None:
        print("ERROR: index not found.", file=sys.stderr)
        return 1

    if ":" not in tool_ref:
        print(
            "ERROR: tool reference must be in format 'server:tool_name'.",
            file=sys.stderr,
        )
        return 2

    server_name, tool_name = tool_ref.split(":", 1)
    for source in index.get("sources", []):
        if source["name"] == server_name:
            if tool_name in source["tools"]:
                source["tools"][tool_name]["tags"] = sorted(tags)
                source["tools"][tool_name]["origin"] = MANUAL
                try:
                    _write_index(target, index, require_validation=False)
                except McpIndexError as exc:  # pragma: no cover - best-effort never raises
                    print(f"ERROR: {exc}", file=sys.stderr)
                    return 1
                print(f"Tags for {tool_ref} set to: {tags}")
                return 0
            print(
                f"ERROR: tool '{tool_name}' not found "
                f"in server '{server_name}'.",
                file=sys.stderr,
            )
            return 1

    print(f"ERROR: server '{server_name}' not found.", file=sys.stderr)
    return 1


def cmd_intent(target: str, tool_ref: str, intent: str) -> int:
    """Set intent for a specific tool."""
    if len(intent) < 10:
        print(
            f"ERROR: intent must be at least 10 characters "
            f"(got {len(intent)}).",
            file=sys.stderr,
        )
        return 1

    index, err = _read_index_safe(target)
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        return 1
    if index is None:
        print("ERROR: index not found.", file=sys.stderr)
        return 1

    if ":" not in tool_ref:
        print(
            "ERROR: tool reference must be in format 'server:tool_name'.",
            file=sys.stderr,
        )
        return 2

    server_name, tool_name = tool_ref.split(":", 1)
    for source in index.get("sources", []):
        if source["name"] == server_name:
            if tool_name in source["tools"]:
                source["tools"][tool_name]["intent"] = intent
                source["tools"][tool_name]["origin"] = MANUAL
                try:
                    _write_index(target, index, require_validation=False)
                except McpIndexError as exc:  # pragma: no cover - best-effort never raises
                    print(f"ERROR: {exc}", file=sys.stderr)
                    return 1
                print(f"Intent for {tool_ref} set.")
                return 0
            print(
                f"ERROR: tool '{tool_name}' not found "
                f"in server '{server_name}'.",
                file=sys.stderr,
            )
            return 1

    print(f"ERROR: server '{server_name}' not found.", file=sys.stderr)
    return 1


def cmd_list(
    target: str, tag: Optional[str] = None, untagged: bool = False
) -> int:
    """List tools, optionally filtered."""
    index, err = _read_index_safe(target)
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        return 1
    if index is None:
        print("ERROR: index not found.", file=sys.stderr)
        return 1

    total = 0
    for source in index.get("sources", []):
        sname = source["name"]
        header_printed = False
        for tname, tool in source.get("tools", {}).items():
            tags = tool.get("tags", [])
            is_removed = tool.get("status") == "removed"

            # Apply filters
            if tag is not None and tag not in tags:
                continue
            if untagged and tags != ["general"]:
                continue

            if not header_printed:
                print(f"\n[{sname}]")
                header_printed = True

            status = " (removed)" if is_removed else ""
            tag_str = ", ".join(tags)
            print(f"  {sname}:{tname}{status}")
            print(f"    tags:   {tag_str}")
            print(f"    intent: {tool.get('intent', '')}")
            total += 1

    filter_desc = ""
    if tag:
        filter_desc = f" (tag={tag})"
    elif untagged:
        filter_desc = " (untagged only)"
    print(f"\n{total} tool(s){filter_desc}")
    return 0


def cmd_migrate(target: str) -> int:
    """One-shot: convert a legacy mcp-tools.yaml into mcp-tools.json.

    Read + write through `_read_index`/`_write_index`, the same functions every other
    command uses — no second conversion implementation to keep in sync (issue #145).
    """
    json_path = _index_path(target)
    legacy_path = _legacy_index_path(target)

    if json_path.exists():
        print(f"{json_path} already exists — nothing to migrate.")
        return 0
    if not legacy_path.exists():
        print(
            "ERROR: no mcp-tools.yaml or mcp-tools.json found. "
            "Run 'mcp-index init' first.",
            file=sys.stderr,
        )
        return 1

    try:
        data = _read_legacy_yaml(legacy_path)
    except LegacyYamlUnreadable as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    try:
        _write_index(target, data, require_validation=True)
    except McpIndexError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    tool_count = sum(len(s.get("tools", {})) for s in data.get("sources", []))
    print(
        f"Migrated {legacy_path.name} -> {json_path.name}: "
        f"{tool_count} tool(s) across {len(data.get('sources', []))} server(s)."
    )
    return 0


# ── CLI dispatch ────────────────────────────────────────────────────────────

def _usage() -> int:
    print(__doc__)
    return 2


def _parse_target_and_remaining(
    argv: list[str],
) -> tuple[Optional[str], list[str]]:
    """Extract --target value and return (target, remaining_args)."""
    try:
        target_idx = argv.index("--target")
        target = argv[target_idx + 1]
        remaining = [
            a for i, a in enumerate(argv)
            if i not in (target_idx, target_idx + 1)
        ]
        return target, remaining
    except (ValueError, IndexError):
        return None, argv


def _extract_clean_args(args: list[str]) -> list[str]:
    """Remove --target and its value from args list."""
    clean: list[str] = []
    skip = False
    for a in args:
        if skip:
            skip = False
            continue
        if a == "--target":
            skip = True
            continue
        clean.append(a)
    return clean


def _flag_value(args: list[str], flag: str) -> Optional[str]:
    """The value following `flag`, or None when it is absent or has nothing after it."""
    if flag not in args:
        return None
    index = args.index(flag)
    return args[index + 1] if index + 1 < len(args) else None


def _requested_host(args: list[str]) -> tuple[Optional[str], bool]:
    """(host, ok): which host CLI to ask, and whether the name is one this script knows."""
    host = _flag_value(args, "--host")
    if host is None:
        return None, "--host" not in args
    return host, host in hl.HOSTS


def main(argv: Optional[list[str]] = None) -> int:
    """Dispatch to the appropriate subcommand."""
    if argv is None:
        argv = sys.argv[1:]

    if not argv:
        return _usage()

    cmd = argv[0]
    target, remaining = _parse_target_and_remaining(argv)

    if target is None:
        print("ERROR: --target <path> is required.", file=sys.stderr)
        return 2

    if cmd in ("init", "update"):
        host, ok = _requested_host(remaining)
        if not ok:
            print(f"ERROR: unknown --host {host!r}. Choose one of: "
                  f"{', '.join(sorted(hl.HOSTS))}.", file=sys.stderr)
            return 2
        from_json = _flag_value(remaining, "--from-json")
        run = cmd_init if cmd == "init" else cmd_update
        return run(target, from_json, host)

    if cmd == "validate":
        return cmd_validate(target)

    if cmd == "migrate":
        return cmd_migrate(target)

    if cmd == "tag":
        clean_args = _extract_clean_args(remaining[1:])
        if len(clean_args) < 2:
            print(
                "ERROR: tag requires tool_ref and at least one tag.",
                file=sys.stderr,
            )
            return 2
        return cmd_tag(target, clean_args[0], clean_args[1:])

    if cmd == "intent":
        clean_args = _extract_clean_args(remaining[1:])
        if len(clean_args) < 2:
            print(
                "ERROR: intent requires tool_ref and intent text.",
                file=sys.stderr,
            )
            return 2
        return cmd_intent(target, clean_args[0], " ".join(clean_args[1:]))

    if cmd == "list":
        tag_filter = None
        if "--tag" in remaining:
            ti = remaining.index("--tag")
            tag_filter = remaining[ti + 1]
        return cmd_list(
            target, tag=tag_filter, untagged="--untagged" in remaining
        )

    return _usage()


if __name__ == "__main__":
    sys.exit(main())
