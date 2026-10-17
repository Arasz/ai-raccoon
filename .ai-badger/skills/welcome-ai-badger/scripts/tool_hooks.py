"""Hook commands for opt-in CLI tools (the ``tools`` feature type), and their recogniser.

The command fails open: no binary, a failed run or non-JSON output all leave the harness
with exit 0 and empty stdout, while the tool's stderr still passes through.
"""
from __future__ import annotations

import re

MARKER = "AI_BADGER_TOOL"

_NAME = re.compile(r"[a-z0-9][a-z0-9-]*\Z")
_MARKED = re.compile(rf"(?:^|\s){MARKER}=\S")


def guarded_command(tool: str, hook_arg: str) -> str:
    """The marked shell command that runs ``<tool> hook <hook_arg>`` behind the fail-open guard."""
    for value in (tool, hook_arg):
        if not _NAME.match(value):
            raise ValueError(f"not a safe tool or hook name: {value!r}")
    telemetry = f"{tool.upper().replace('-', '_')}_TELEMETRY_DISABLED=1"
    script = (f"command -v {tool} >/dev/null 2>&1 || exit 0; "
              f"out=$({tool} hook {hook_arg}) || exit 0; "
              "case \"$out\" in \"{\"*) printf \"%s\" \"$out\";; esac")
    return f"env {MARKER}={tool} {telemetry} sh -c '{script}'"


def is_ours(command: object) -> bool:
    """True when a hook command carries the tool marker, whatever the rest of it says."""
    return isinstance(command, str) and bool(_MARKED.search(command))
