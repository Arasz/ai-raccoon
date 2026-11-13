#!/usr/bin/env python3
# pylint: disable=invalid-name  # hyphenated script filename is the shipped CLI name
"""Derive the facts a manual checklist run compares the running build against.

Prints `version`, `mcp-tool-count` and `mcp-prompt-count` on stdout, one per line, and exits
non-zero when any of them cannot be derived — a missing path fails loudly instead of yielding a
confident zero.

Usage: checklist-derive-facts.py [<repo-root>] [--fact NAME]   (default root: the current directory)

`--fact NAME` prints only that fact's value; an unknown NAME exits 2.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path
from typing import NamedTuple

PREFIX = "checklist-derive-facts"
LAYOUT_HINT = (f"{PREFIX}: the tree layout moved; fix this script rather than typing the fact "
               "by hand.")
COPY_HINT = f'{PREFIX}: these are the checklist facts in docs/checklist/checks.json.'

CSPROJ = "src/AiRaccoon/AiRaccoon.csproj"
# The version moved out of the csproj into a plain file at the repo root; the element is still
# read first because a tree carrying both is stating the local one deliberately.
VERSION_FILE = "VERSION"
TOOLS_DIR = "src/AiRaccoon/Tools"
PROMPTS_DIR = "src/AiRaccoon/Prompts"

# A line whose trimmed content opens with one of these is prose. `McpToolInventory.cs` documents
# the attribute it reflects over, so the literal token appears in a `///` line that is not a tool.
COMMENT_OPENERS = ("///", "//", "*")
VERSION_RE = re.compile(r"<PackageVersion>(.*)</PackageVersion>")
# An MSBuild property reference this script cannot evaluate; reporting it verbatim would put an
# unresolved token in the checklist facts.
VERSION_PLACEHOLDER = "$(Version)"


class DerivationFailed(Exception):
    """A fact could not be derived; the message names which one."""


class Facts(NamedTuple):
    """The three facts a checklist run compares the running build against."""

    version: str
    tools: int
    prompts: int

    def as_dict(self) -> dict[str, str]:
        """Fact name to printed value, in output order."""
        return {"version": self.version, "mcp-tool-count": str(self.tools),
                "mcp-prompt-count": str(self.prompts)}

    def render(self) -> str:
        """The stdout contract, read by the fact commands in docs/checklist/checks.json."""
        return (f"version={self.version}\n"
                f"mcp-tool-count={self.tools}\n"
                f"mcp-prompt-count={self.prompts}\n")


def is_comment_line(line: str) -> bool:
    """Whether *line* is prose rather than code — a doc comment, a line comment, or a `/* */`
    block's continuation."""
    return line.lstrip().startswith(COMMENT_OPENERS)


def count_attribute_uses(source: str, attribute: str) -> int:
    """Occurrences of ``[<attribute>`` in *source*, ignoring block comments and comment lines.

    Counts per occurrence, not per line: two attributes on one line are two uses. Only a line
    that *opens* as a comment is dropped, so an attribute following code on the same line counts.

    Matched with a trailing ``\\b`` (L9-8), not a bare substring: a plain ``[McpServerTool``
    substring also matches the C# SDK's class-level ``[McpServerToolType]`` (and
    ``[McpServerPrompt`` matches ``[McpServerPromptType]``), inflating every count by one per
    tool/prompt class. ``\\b`` already excludes that ``Type`` suffix on its own — both sides of
    the boundary would have to be word characters for it to match there, so a further
    ``(?!Type)`` lookahead would be redundant.
    """
    source = re.sub(r'//[^\n]*|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|/\*.*?\*/',
                    lambda match: match[0] if not match[0].startswith("/*")
                    else "\n" * match[0].count("\n"), source, flags=re.DOTALL)
    pattern = re.compile(r"\[" + re.escape(attribute) + r"\b")
    return sum(len(pattern.findall(line)) for line in source.splitlines()
               if not is_comment_line(line))


def count_in_tree(directory: Path, attribute: str) -> int:
    """Uses of *attribute* across every ``*.cs`` file under *directory*.

    Raises when the total is zero: a moved path summing to a confident 0 is the failure this
    whole script exists to prevent.
    """
    total = sum(count_attribute_uses(_read(path), attribute)
                for path in sorted(directory.rglob("*.cs")))
    if total == 0:
        raise DerivationFailed(f"zero [{attribute}] found under {directory}")
    return total


def read_package_version(csproj_text: str) -> str:
    """The literal ``<PackageVersion>`` in *csproj_text*, or `""` when there is none to trust."""
    match = VERSION_RE.search(csproj_text)
    if match is None:
        return ""
    version = match.group(1).strip()
    return "" if version == VERSION_PLACEHOLDER else version


def read_version_file(path: Path) -> str:
    """The trimmed contents of the ``VERSION`` file at *path*, or `""` when absent or blank."""
    if not path.is_file():
        return ""
    return _read(path).strip()


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def derive(repo_root: Path) -> Facts:
    """Derive the three facts from *repo_root*, or raise naming the one that could not be."""
    csproj = repo_root / CSPROJ
    tools_dir = repo_root / TOOLS_DIR
    prompts_dir = repo_root / PROMPTS_DIR

    if not csproj.is_file():
        raise DerivationFailed(f"no csproj at {csproj}")
    if not tools_dir.is_dir():
        raise DerivationFailed(f"no tools directory at {tools_dir}")
    if not prompts_dir.is_dir():
        raise DerivationFailed(f"no prompts directory at {prompts_dir}")

    version_file = repo_root / VERSION_FILE
    version = read_package_version(_read(csproj)) or read_version_file(version_file)
    if not version:
        raise DerivationFailed(f"no version in {csproj} nor {version_file}")

    return Facts(version=version,
                 tools=count_in_tree(tools_dir, "McpServerTool"),
                 prompts=count_in_tree(prompts_dir, "McpServerPrompt"))


def main(argv: list[str]) -> int:
    """Print the derived facts, or the reason there are none. 0 on success, 1 on a failed
    derivation, 2 on an unknown `--fact` name."""
    parser = argparse.ArgumentParser(prog=PREFIX, description="Derive the checklist facts.")
    parser.add_argument("repo_root", nargs="?", default=".")
    parser.add_argument("--fact", metavar="NAME", help="print only this fact's value")
    args = parser.parse_args(argv)
    try:
        facts = derive(Path(args.repo_root))
    except DerivationFailed as failure:
        print(f"{PREFIX}: {failure}", file=sys.stderr)
        print(LAYOUT_HINT, file=sys.stderr)
        return 1
    if args.fact is not None:
        values = facts.as_dict()
        if args.fact not in values:
            print(f"{PREFIX}: unknown fact {args.fact!r}; known: {', '.join(values)}",
                  file=sys.stderr)
            return 2
        print(values[args.fact])
        return 0
    print(facts.render(), end="")
    print(COPY_HINT, file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
