#!/usr/bin/env python3
# /// script
# requires-python = ">=3.10"
# dependencies = ["pydantic>=2.12,<3"]
# ///
"""task_graph_cli.py — the 12 frozen task-graph tools as a CLI, payload-equal to the MCP server.

Usage: ``uv run --script task_graph_cli.py <tool-name> --json '<arguments-object>'``

The CLI loads the sibling server module and calls the same handler, so its payloads are the
server's payloads by construction and the tests still pin them as literals. Success prints the
payload as JSON on stdout and exits 0. A tool-domain failure prints the same closed error
envelope the MCP transport returns and exits 1. A usage failure (unknown verb, unparseable
``--json``, non-object ``--json``) writes a diagnostic to stderr and exits 2. A launch failure
(the server module cannot be imported) writes one stderr line and exits 3.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
SERVER_PATH = SCRIPT_DIR / "task_graph_server.py"


def _load_server():
    """The server module by path under its plain name, cached in ``sys.modules``."""
    cached = sys.modules.get("task_graph_server")
    if cached is not None and Path(getattr(cached, "__file__", "")).resolve() == SERVER_PATH:
        return cached
    spec = importlib.util.spec_from_file_location("task_graph_server", SERVER_PATH)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot load the task-graph server at {SERVER_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules["task_graph_server"] = module
    try:
        spec.loader.exec_module(module)
    except BaseException:
        sys.modules.pop("task_graph_server", None)
        raise
    return module


def main(argv=None) -> int:
    """Dispatch one tool verb; 0 ok, 1 tool-domain error, 2 usage error, 3 launch failure."""
    parser = argparse.ArgumentParser(
        prog="task_graph_cli.py", description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("tool", help="one of the 12 task-graph tool names")
    parser.add_argument("--json", required=True, dest="payload", metavar="JSON",
                        help="tool arguments as a JSON object")
    args = parser.parse_args(argv)

    try:
        server = _load_server()
    except Exception as exc:  # pylint: disable=broad-except
        print(f"task-graph cli cannot start: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 3
    if args.tool not in server.TOOL_BY_NAME:
        print(f"unknown tool {args.tool!r}; expected one of: "
              f"{', '.join(spec.name for spec in server.TOOLS)}", file=sys.stderr)
        return 2
    try:
        # argv can carry surrogateescape bytes for input that was never UTF-8; re-encode
        # with replacement so the JSON parser sees a value, never a raw decode failure.
        arguments = json.loads(args.payload.encode("utf-8", errors="replace"))
    except (ValueError, UnicodeDecodeError) as exc:
        print(f"--json is not valid JSON: {exc}", file=sys.stderr)
        return 2
    if not isinstance(arguments, dict):
        print("--json must be a JSON object", file=sys.stderr)
        return 2

    structured = server.invoke_tool(args.tool, arguments)
    print(json.dumps(structured, ensure_ascii=False, sort_keys=True, indent=2))
    return 1 if structured.get("ok") is False else 0


if __name__ == "__main__":
    sys.exit(main())
