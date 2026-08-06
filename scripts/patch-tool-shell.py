#!/usr/bin/env python3
"""Rewrite a PackAsTool shell nupkg's DotnetToolSettings.xml to reference every
given RuntimeIdentifierPackage. The SDK emits exactly one entry (the RID it was
packed for); a multi-RID tool shell must list all of them or `dotnet tool install`
rejects every other platform. Self-gating: exits nonzero if the input shape is
unexpected or the output does not contain every requested RID.

Usage: patch-tool-shell.py <shell.nupkg> <rid> [<rid> ...]
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
from tool_shell import patch  # noqa: E402


def main() -> int:
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    nupkg = Path(sys.argv[1])
    rids = sys.argv[2:]
    command, prefix = patch(nupkg, rids)
    print(f"patched {nupkg.name}: command={command}, prefix={prefix}, rids={rids}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
