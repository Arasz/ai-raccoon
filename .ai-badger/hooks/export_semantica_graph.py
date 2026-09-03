"""Export hook script for Semantica knowledge graph.

Writes per-session graph snapshots under .semantica/ with atomic writes, and
auto-saves the export_graph MCP tool result via the Hermes post_tool_call hook.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
import time
from pathlib import Path

SEMANTICA_DIR = ".semantica"

DEFAULT_SEED = {
    "version": "1.0",
    "nodes": [],
    "edges": [],
    "decisions": [],
    "metadata": {
        "source": "semantica-mcp",
        "updatedAt": None,
    },
}

NUDGE_LINE = (
    "[ai-badger] Semantica is configured: record key decisions via record_decision — "
    "they stay queryable this session via query_decisions and find_precedents."
)


def semantica_indexed(index) -> bool:
    """True when any index source's last ':' token is 'semantica' (bare or decorated)."""
    return any(
        (source.get("name") or "").rsplit(":", 1)[-1] == "semantica"
        for source in (index or {}).get("sources", [])
    )


def is_export_graph(tool_name) -> bool:
    """True for any naming spelling of the export_graph tool; never other tools."""
    if not isinstance(tool_name, str):
        return False
    name = tool_name
    if name.startswith("mcp__"):
        name = name[len("mcp__"):].split("__", 1)[-1]
    if ":" in name:
        name = name.rsplit(":", 1)[-1]
    # export_graph is a generic name, so a future unrelated server could false-positive (accepted).
    return name == "export_graph"


def _sanitize_segment(value: str) -> str:
    """Keep [A-Za-z0-9._-]; substitute '_' for everything else (session ids may hold ':', '/', ' ').

    Guards dot-only traversal segments: '.' -> '_' and '..' -> '__'.
    """
    if not value:
        return ""
    sanitized = "".join(
        ch if ch.isascii() and (ch.isalnum() or ch in "._-") else "_" for ch in str(value)
    )
    if sanitized == ".":
        return "_"
    if sanitized == "..":
        return "__"
    return sanitized


def _now_slug() -> str:
    """Filesystem-safe timestamp slug (no ':') unique across rapid calls."""
    return str(time.time_ns())


def session_export_target(session_id, project_dir) -> Path:
    """Per-session target path under project_dir/.semantica/, timestamped for uniqueness."""
    project_dir = Path(project_dir)
    slug = _now_slug()
    if session_id:
        filename = f"{_sanitize_segment(session_id)}-{slug}.json"
    else:
        filename = f"{slug}.json"
    return project_dir / SEMANTICA_DIR / filename


def extract_graph_json(result) -> dict | None:
    """Unwrap Hermes' double-encoded MCP result into the graph dict, or None."""
    if isinstance(result, str):
        try:
            result = json.loads(result)
        except json.JSONDecodeError:
            return None
    if not isinstance(result, dict):
        return None
    if result.get("error") is not None or result.get("isError"):
        return None
    if "result" in result:
        inner = result["result"]
        if isinstance(inner, str):
            try:
                inner = json.loads(inner)
            except json.JSONDecodeError:
                return None
        if isinstance(inner, dict):
            # The inner payload may itself carry the error (observed live 2026-08-20:
            # an export_graph failure arrives as {"result": "{\"error\": ...}"} and was
            # previously saved as a graph dump).
            if inner.get("error") is not None or inner.get("isError"):
                return None
            return inner
        return None
    if "structuredContent" in result:
        inner = result["structuredContent"]
        if isinstance(inner, dict):
            # Same inner-error guard as the "result" branch — a structured
            # error payload must never be saved as a graph dump.
            if inner.get("error") is not None or inner.get("isError"):
                return None
            return inner
        return None
    # Anything that matched no envelope used to fall through and be written as a
    # graph. Require the shape a graph actually has rather than enumerating the
    # envelopes that are not one.
    if "nodes" in result:
        return result
    return None


def export_graph(
    target_path: Path,
    raw_json: str | None = None,
    data_dict: dict | None = None,
    temp_dir: Path | None = None,
) -> Path:
    """Atomic write of Semantica graph JSON snapshot to target_path.

    temp_dir (default target_path.parent) holds the temp file, so autosave can
    stage it outside the watched .semantica/ dir (same device, os.replace-safe).
    """
    target_path = target_path.resolve()
    target_path.parent.mkdir(parents=True, exist_ok=True)

    now_iso = datetime.datetime.now(datetime.timezone.utc).isoformat()

    if data_dict is not None:
        payload = data_dict
    elif raw_json is not None:
        try:
            payload = json.loads(raw_json)
        except json.JSONDecodeError:
            payload = dict(DEFAULT_SEED)
            payload["raw_unparsed"] = raw_json
    else:
        if target_path.is_file():
            try:
                payload = json.loads(target_path.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                payload = dict(DEFAULT_SEED)
        else:
            payload = dict(DEFAULT_SEED)

    if isinstance(payload, dict):
        if "metadata" not in payload or not isinstance(payload["metadata"], dict):
            payload["metadata"] = {"source": "semantica-mcp"}
        payload["metadata"]["updatedAt"] = now_iso

    content = json.dumps(payload, indent=2, ensure_ascii=False)

    write_dir = temp_dir.resolve() if temp_dir is not None else target_path.parent
    write_dir.mkdir(parents=True, exist_ok=True)
    temp_path = write_dir / f"{target_path.name}.tmp.{os.getpid()}"
    temp_path.write_text(content, encoding="utf-8")
    os.replace(temp_path, target_path)

    return target_path


def _error_payload_error(result) -> str | None:
    """The error message when the (possibly double-encoded) result carries one.

    Mirrors extract_graph_json's reject paths: outer error/isError, then the
    inner payload of either envelope. None when the result is not an error —
    a skipped write for any other reason stays silent.
    """
    if isinstance(result, str):
        try:
            result = json.loads(result)
        except json.JSONDecodeError:
            return None
    if not isinstance(result, dict):
        return None
    if result.get("error") is not None or result.get("isError"):
        return str(result.get("error") or "isError")
    for key in ("result", "structuredContent"):
        inner = result.get(key)
        if isinstance(inner, str):
            try:
                inner = json.loads(inner)
            except json.JSONDecodeError:
                continue
        if isinstance(inner, dict) and (inner.get("error") is not None or inner.get("isError")):
            return str(inner.get("error") or "isError")
    content = result.get("content")
    if isinstance(content, list):
        for part in content:
            if not isinstance(part, dict) or part.get("type") != "text":
                continue
            try:
                inner = json.loads(part.get("text", ""))
            except json.JSONDecodeError:
                continue
            if isinstance(inner, dict) and (inner.get("error") is not None or inner.get("isError")):
                return str(inner.get("error") or "isError")
    return None


def autosave_export(tool_name, result, session_id, project_dir) -> Path | None:
    """Auto-save an export_graph result; None (no write) when not a graph or not export."""
    if not is_export_graph(tool_name):
        return None
    graph = extract_graph_json(result)
    if graph is None:
        # A skipped error must not be invisible (the bridge died silently for
        # weeks before 0.130.0). One stderr line, never stdout — the hook is
        # advisory and must not pollute the tool result channel.
        error = _error_payload_error(result)
        if error is not None:
            print(
                f"[ai-badger] semantica export_graph failed; .semantica/ dump skipped: {error}",
                file=sys.stderr,
            )
        else:
            print(
                "[ai-badger] semantica export_graph returned an unrecognized payload "
                "shape; .semantica/ dump skipped.",
                file=sys.stderr,
            )
        return None
    target = session_export_target(session_id, project_dir)
    return export_graph(target_path=target, data_dict=graph, temp_dir=Path(project_dir))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Export Semantica graph to disk for AiRaccoon watch.")
    parser.add_argument("--target", help="Path to output JSON file (default: .semantica/semantica-graph-<timestamp>.json)")
    parser.add_argument("--json", help="Raw JSON string exported from Semantica")
    args = parser.parse_args(argv)

    if args.target:
        target_file = Path(args.target)
        temp_dir = None
    else:
        target_file = Path.cwd() / SEMANTICA_DIR / f"semantica-graph-{_now_slug()}.json"
        # Stage the temp outside .semantica/ so a directory watch never ingests a .tmp.
        temp_dir = Path.cwd()

    try:
        exported = export_graph(target_path=target_file, raw_json=args.json, temp_dir=temp_dir)
        print(f"Semantica graph snapshot exported to {exported}")
        return 0
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"Warning: Failed to export Semantica graph: {exc}", file=sys.stderr)
        return 0


if __name__ == "__main__":
    sys.exit(main())
