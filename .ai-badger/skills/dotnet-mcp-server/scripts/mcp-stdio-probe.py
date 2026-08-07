#!/usr/bin/env python3
# pylint: disable=invalid-name  # hyphenated script filename is the shipped CLI name
"""Smoke-test a stdio MCP server end-to-end: spawn it, send a real `initialize`
handshake, and verify the JSON-RPC result arrives on a CLEAN stdout.

Why this exists: `dotnet run` prints "Using launch settings from ..." to stdout
before the first protocol message unless `--no-launch-profile` is passed, which
corrupts the newline-delimited JSON-RPC stream strict MCP clients expect. This
probe is the exact check a strict client runs, so it catches that bug.

Usage:
    python3 mcp-stdio-probe.py [--cmd CMD [ARGS...]] [--wait SECONDS]

Defaults to: dotnet run --project src/<Server>/<Server>.csproj --no-launch-profile --no-build
(macOS has no timeout(1); the probe is pure Python and kills the server's own
process group when done).

Pitfalls encoded (all learned the hard way):
  - NEVER communicate(input=...): closing stdin makes a stdio server exit on EOF
    before it replies. Write the request, keep stdin open, read with select().
  - Use start_new_session=True + os.killpg: killing just the `dotnet` CLI leaves
    the server child holding the pipes, and communicate() hangs forever.
  - Pre-build with --no-build so the probe doesn't wait on a compile.
Exit code: 0 = clean handshake (result line, no launch-settings pollution),
1 = handshake missing or stdout polluted.
"""
from __future__ import annotations

import json
import os
import select
import signal
import subprocess
import sys
import time

DEFAULT_CMD = ["dotnet", "run", "--project", "src/the project", "--no-launch-profile", "--no-build"]
INITIALIZE = json.dumps({
    "jsonrpc": "2.0", "id": 1, "method": "initialize",
    "params": {"protocolVersion": "2025-06-18", "capabilities": {},
               "clientInfo": {"name": "mcp-stdio-probe", "version": "0"}},
})


def main() -> int:
    args = sys.argv[1:]
    cmd = DEFAULT_CMD
    wait = 3.0
    if args and args[0] == "--cmd":
        cmd = args[1:]
    if args and args[0] == "--wait":
        wait = float(args[1])

    with subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True, bufsize=1,
                          start_new_session=True) as p:
        try:
            p.stdin.write(INITIALIZE + "\n")
            p.stdin.flush()
            time.sleep(wait)
            lines: list[str] = []
            while select.select([p.stdout], [], [], 1)[0]:
                line = p.stdout.readline()
                if not line:
                    break
                lines.append(line.strip())
        finally:
            os.killpg(os.getpgid(p.pid), signal.SIGKILL)
            p.wait()
            err = p.stderr.read()

    if not lines:
        print("FAIL: no stdout output; server may have exited early")
        print("stderr tail:", (err.strip().splitlines() or ["(empty)"])[-1][:120])
        return 1
    first = lines[0]
    handshake = any('"serverInfo"' in l or '"result"' in l for l in lines)
    polluted = "launch settings" in first.lower()
    print(f"stdout lines: {len(lines)}")
    print(f"first line:   {first[:110]}")
    print(f"handshake ok: {handshake}")
    print(f"polluted:     {polluted}")
    if handshake and not polluted:
        return 0
    if polluted:
        print("=> add --no-launch-profile to the client args / run command")
    return 1


if __name__ == "__main__":
    sys.exit(main())
