#!/usr/bin/env python3
"""Fail open on Codex when the shared memory lookup cannot reach its backend."""
from __future__ import annotations

import contextlib
import io
import runpy
import sys
from pathlib import Path
from typing import List, Optional


def main(argv: Optional[List[str]] = None) -> int:
    """Run the shared gate and suppress its denial only when memory lookup is unavailable."""
    args = sys.argv[1:] if argv is None else argv
    if not args:
        return 0
    hook = Path(args[-1]).resolve()
    if not hook.is_file():
        return 0
    original_path = sys.path[:]
    missing = object()
    previous_gate = sys.modules.get('memory_first_gate', missing)
    gate = None
    original_memory_hits = None
    try:
        sys.path.insert(0, str(hook.parent))
        import memory_first_gate as gate  # pylint: disable=import-outside-toplevel
        unavailable = False
        original_memory_hits = gate.memory_hits

        def memory_hits(*call_args, **call_kwargs):
            nonlocal unavailable
            try:
                result = original_memory_hits(*call_args, **call_kwargs)
            except Exception:  # pylint: disable=broad-exception-caught  # Codex adapter fails open
                unavailable = True
                raise
            if result is None:
                unavailable = True
            return result

        gate.memory_hits = memory_hits
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            namespace = runpy.run_path(str(hook), run_name='codex_memory_first_gate_hook')
            namespace['main']()
        if not unavailable:
            sys.stdout.write(output.getvalue())
    except Exception:  # pylint: disable=broad-exception-caught  # hooks must not block tools
        return 0
    finally:
        if gate is not None and original_memory_hits is not None:
            gate.memory_hits = original_memory_hits
        sys.path[:] = original_path
        if previous_gate is missing:
            sys.modules.pop('memory_first_gate', None)
        else:
            sys.modules['memory_first_gate'] = previous_gate
    return 0


if __name__ == '__main__':
    sys.exit(main())
