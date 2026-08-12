"""Export hook script for Semantica knowledge graph.

Exports the current in-memory Semantica graph snapshot to .ai-raccoon/semantica-graph.json
with atomic writes, timestamping, and non-blocking graceful fallback when unattached.
"""
from __future__ import annotations

import argparse
import datetime
import json
import os
import sys
from pathlib import Path

DEFAULT_RELATIVE_TARGET = ".ai-raccoon/semantica-graph.json"

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


def export_graph(
    target_path: Path,
    raw_json: str | None = None,
    data_dict: dict | None = None,
) -> Path:
    """Atomic write of Semantica graph JSON snapshot to target_path."""
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

    temp_path = target_path.with_name(f"{target_path.name}.tmp.{os.getpid()}")
    temp_path.write_text(content, encoding="utf-8")
    os.replace(temp_path, target_path)

    return target_path


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Export Semantica graph to disk for AiRaccoon watch.")
    parser.add_argument("--target", help="Path to output JSON file (default: .ai-raccoon/semantica-graph.json)")
    parser.add_argument("--json", help="Raw JSON string exported from Semantica")
    args = parser.parse_args(argv)

    target_file = Path(args.target) if args.target else Path.cwd() / DEFAULT_RELATIVE_TARGET

    try:
        exported = export_graph(target_path=target_file, raw_json=args.json)
        print(f"Semantica graph snapshot exported to {exported}")
        return 0
    except Exception as exc:  # pylint: disable=broad-exception-caught
        print(f"Warning: Failed to export Semantica graph: {exc}", file=sys.stderr)
        return 0


if __name__ == "__main__":
    sys.exit(main())
