#!/usr/bin/env python3
"""Rewrite a PackAsTool shell nupkg's DotnetToolSettings.xml to reference every
given RuntimeIdentifierPackage. The SDK emits exactly one entry (the RID it was
packed for); a multi-RID tool shell must list all of them or `dotnet tool install`
rejects every other platform. Self-gating: exits nonzero if the input shape is
unexpected or the output does not contain every requested RID.

Usage: patch-tool-shell.py <shell.nupkg> <rid> [<rid> ...]
"""

import sys
import zipfile
from pathlib import Path

XML_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="2">
  <Commands>
    <Command Name="{command}" />
  </Commands>
  <RuntimeIdentifierPackages>
{packages}
  </RuntimeIdentifierPackages>
</DotNetCliTool>
"""


def find_settings(zf: zipfile.ZipFile) -> str:
    matches = [n for n in zf.namelist() if n.endswith("DotnetToolSettings.xml")]
    if len(matches) != 1:
        raise SystemExit(f"expected exactly one DotnetToolSettings.xml, found {matches}")
    return matches[0]


def parse_settings(content: str) -> tuple[str, str]:
    """Returns (tool command name, id prefix) from a shell settings file."""
    command = None
    prefix = None
    rids = []
    for line in content.splitlines():
        line = line.strip()
        if line.startswith("<Command Name="):
            command = line.split('"')[1]
        elif line.startswith("<RuntimeIdentifierPackage "):
            rid = line.split('RuntimeIdentifier="')[1].split('"')[0]
            id_ = line.split('Id="')[1].split('"')[0]
            prefix = id_[: -len(rid) - 1]  # strip "." + rid from the package id
            rids.append(rid)
    if command is None or prefix is None:
        raise SystemExit("unexpected shell shape: Command or RuntimeIdentifierPackage not found")
    if len(rids) != 1:
        raise SystemExit(f"expected exactly one RuntimeIdentifierPackage, found {rids}")
    return command, prefix


def build_settings(command: str, prefix: str, rids: list[str]) -> str:
    packages = "\n".join(
        f'    <RuntimeIdentifierPackage RuntimeIdentifier="{rid}" Id="{prefix}.{rid}" />'
        for rid in rids
    )
    return XML_TEMPLATE.format(command=command, packages=packages)


def main() -> int:
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    nupkg = Path(sys.argv[1])
    rids = sys.argv[2:]
    if len(set(rids)) != len(rids):
        raise SystemExit(f"duplicate rids: {rids}")

    with zipfile.ZipFile(nupkg) as zf:
        settings_name = find_settings(zf)
        command, prefix = parse_settings(zf.read(settings_name).decode("utf-8"))
        entries = [(i, zf.read(i)) for i in zf.namelist() if i != settings_name]

    new_settings = build_settings(command, prefix, rids)

    with zipfile.ZipFile(nupkg, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data in entries:
            zf.writestr(name, data)
        zf.writestr(settings_name, new_settings)

    # Self-gate: re-read and verify every requested RID is present exactly once.
    with zipfile.ZipFile(nupkg) as zf:
        content = zf.read(settings_name).decode("utf-8")
    for rid in rids:
        if content.count(f'RuntimeIdentifier="{rid}"') != 1:
            raise SystemExit(f"verification failed: rid {rid} not present exactly once")
    print(f"patched {nupkg.name}: command={command}, prefix={prefix}, rids={rids}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
