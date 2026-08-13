#!/usr/bin/env python3
"""Bump the ai-raccoon tool version across all markers.

Usage:
    python3 scripts/version-bump.py <patch|minor|major>
"""
import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CSPROJ = REPO_ROOT / "src" / "AiRaccoon" / "AiRaccoon.csproj"
SERVER_JSON = REPO_ROOT / "src" / "AiRaccoon" / ".mcp" / "server.json"
VERSION_TEST = REPO_ROOT / "tests" / "AiRaccoon.Tests" / "Unit" / "Setup" / "VersionContractTests.cs"


def read_current() -> str:
    m = re.search(r"<PackageVersion>([^<]+)</PackageVersion>", CSPROJ.read_text())
    if not m:
        sys.exit("version-bump: <PackageVersion> not found in AiRaccoon.csproj")
    return m.group(1)


def bump(version: str, level: str) -> str:
    major, minor, patch = (int(p) for p in version.split("."))
    if level == "major":
        return f"{major + 1}.0.0"
    if level == "minor":
        return f"{major}.{minor + 1}.0"
    return f"{major}.{minor}.{patch + 1}"


def replace_version(path: Path, old: str, new: str, expected: int) -> None:
    text = path.read_text()
    found = text.count(old)
    if found != expected:
        sys.exit(f"version-bump: expected {expected} x {old!r} in {path.name}, found {found} (markers drifted?)")
    path.write_text(text.replace(old, new))


def main() -> None:
    ap = argparse.ArgumentParser(description="Bump the ai-raccoon tool version")
    ap.add_argument("level", choices=("patch", "minor", "major"), help="semver bump: patch | minor | major")
    args = ap.parse_args()

    old = read_current()
    new = bump(old, args.level)

    replace_version(CSPROJ, old, new, 3)        # PackageVersion, InformationalVersion, AssemblyVersion
    replace_version(SERVER_JSON, old, new, 2)   # top-level version + packages[0].version
    replace_version(VERSION_TEST, old, new, 1)  # ExpectedVersion

    print(f"version-bump: {old} -> {new}")
    print("Verify: dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter FullyQualifiedName~VersionContractTests")
    print("Release notes: add a compact README 'What's new' entry only for a braggable feature (see the whats-new-update skill).")


if __name__ == "__main__":
    main()
