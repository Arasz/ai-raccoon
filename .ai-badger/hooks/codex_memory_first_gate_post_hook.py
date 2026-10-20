#!/usr/bin/env python3
"""Filter failed Codex memory-search results before calling the shared credit observer."""
from __future__ import annotations

import json
import io
import runpy
import sys
from pathlib import Path
from typing import Any, Dict


def main() -> int:
    """Pass successful Codex PostToolUse payloads to the shared observer only."""
    try:
        payload: Dict[str, Any] = json.load(sys.stdin)
    except (json.JSONDecodeError, OSError):
        return 0
    if not isinstance(payload, dict):
        return 0
    response = payload.get('tool_response')
    if isinstance(response, dict) and response.get('isError') is True:
        return 0
    if len(sys.argv) < 2:
        return 0
    sys.stdin = io.StringIO(json.dumps(payload))
    runpy.run_path(str(Path(sys.argv[-1])), run_name='__main__')
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:  # pylint: disable=broad-exception-caught
        sys.exit(0)
