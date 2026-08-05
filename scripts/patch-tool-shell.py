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
from typing import Optional

XML_TEMPLATE = """<?xml version="1.0" encoding="utf-8"?>
<DotNetCliTool Version="2">
  <Commands>
    <Command {command} />
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


def parse_settings(content: str) -> "tuple[str, str, Optional[str], Optional[str]]":
    """Returns (tool command name, id prefix, entry point, runner) from a shell settings
    file. Entry point/runner are optional (absent in the packed shell shape, present in
    the intermediate SDK shape — preserved when seen)."""
    command = None
    prefix = None
    entry_point = None
    runner = None
    rids = []
    for line in content.splitlines():
        line = line.strip()
        if line.startswith("<Command "):
            command = line.split('Name="')[1].split('"')[0]
            if 'EntryPoint="' in line:
                entry_point = line.split('EntryPoint="')[1].split('"')[0]
            if 'Runner="' in line:
                runner = line.split('Runner="')[1].split('"')[0]
        elif line.startswith("<RuntimeIdentifierPackage "):
            rid = line.split('RuntimeIdentifier="')[1].split('"')[0]
            id_ = line.split('Id="')[1].split('"')[0]
            if not id_.endswith("." + rid):
                raise SystemExit(f"unexpected package id shape: Id='{id_}' for rid '{rid}'")
            prefix = id_[: -len(rid) - 1]  # strip "." + rid from the package id
            rids.append(rid)
    if command is None or prefix is None:
        raise SystemExit("unexpected shell shape: Command or RuntimeIdentifierPackage not found")
    if len(rids) != 1:
        raise SystemExit(f"expected exactly one RuntimeIdentifierPackage, found {rids}")
    return command, prefix, entry_point, runner


def build_settings(command: str, prefix: str, rids: list[str],
                   entry_point: Optional[str] = None, runner: Optional[str] = None) -> str:
    command_attrs = f'Name="{command}"'
    if entry_point is not None:
        command_attrs += f' EntryPoint="{entry_point}"'
    if runner is not None:
        command_attrs += f' Runner="{runner}"'
    packages = "\n".join(
        f'    <RuntimeIdentifierPackage RuntimeIdentifier="{rid}" Id="{prefix}.{rid}" />'
        for rid in rids
    )
    return XML_TEMPLATE.format(command=command_attrs, packages=packages)


def main() -> int:
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    nupkg = Path(sys.argv[1])
    rids = sys.argv[2:]
    if len(set(rids)) != len(rids):
        raise SystemExit(f"duplicate rids: {rids}")
    # The matrix in publish.yml drives the payloads; a RID added there but not here
    # would ship a shell that silently excludes a platform. Keep this count in sync
    # with the matrix (the mismatch is loud on purpose).
    if len(rids) != 6:
        raise SystemExit(f"expected 6 rids (publish.yml matrix), got {len(rids)}: {rids}")

    with zipfile.ZipFile(nupkg) as zf:
        settings_name = find_settings(zf)
        command, prefix, entry_point, runner = parse_settings(zf.read(settings_name).decode("utf-8"))
        entries = [(i, zf.read(i)) for i in zf.namelist() if i != settings_name]
        content_types = zf.read("[Content_Types].xml") if "[Content_Types].xml" in zf.namelist() else None

    new_settings = build_settings(command, prefix, rids, entry_point, runner)

    with zipfile.ZipFile(nupkg, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data in entries:
            zf.writestr(name, data)
        zf.writestr(settings_name, new_settings)

    # Self-gate: re-read and verify every requested RID is present exactly once, the
    # rewritten id matches the derived prefix, and no entry was dropped in the rewrite.
    with zipfile.ZipFile(nupkg) as zf:
        names = zf.namelist()
        content = zf.read(settings_name).decode("utf-8")
        if content_types is not None and zf.read("[Content_Types].xml") != content_types:
            raise SystemExit("verification failed: [Content_Types].xml changed")
    if sorted(names) != sorted([settings_name] + [n for n, _ in entries]):
        raise SystemExit("verification failed: package entries changed")
    for rid in rids:
        if content.count(f'RuntimeIdentifier="{rid}"') != 1:
            raise SystemExit(f"verification failed: rid {rid} not present exactly once")
        if f'Id="{prefix}.{rid}"' not in content:
            raise SystemExit(f"verification failed: id for {rid} does not match prefix '{prefix}'")
    print(f"patched {nupkg.name}: command={command}, prefix={prefix}, rids={rids}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
