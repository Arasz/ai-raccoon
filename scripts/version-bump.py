#!/usr/bin/env python3
"""Bump VERSION, the single hand-written version marker for the ai-raccoon tool.
Everything else (assembly version, server.json) derives from it at build/pack time.

Usage:
    python3 scripts/version-bump.py <patch|minor|major>
"""
import argparse
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
VERSION_FILE = REPO_ROOT / "VERSION"


def read_current() -> str:
    return VERSION_FILE.read_text().strip()


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

    replace_version(VERSION_FILE, old, new, 1)  # the single hand-written version marker

    print(f"version-bump: {old} -> {new}")
    print("Verify: dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter FullyQualifiedName~VersionContractTests")
    print("Release notes: add a compact README 'What's new' entry only for a braggable feature (see the whats-new-update skill).")


if __name__ == "__main__":
    main()
