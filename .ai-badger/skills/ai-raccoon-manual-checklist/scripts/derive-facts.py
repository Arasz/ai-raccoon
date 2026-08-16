#!/usr/bin/env python3
# pylint: disable=invalid-name  # hyphenated script filename is the shipped CLI name
"""Derive the facts a manual checklist run compares the running build against.

Prints `version`, `mcp-tool-count` and `mcp-prompt-count` on stdout, one per line, and exits
non-zero when any of them cannot be derived — a missing path fails loudly instead of yielding a
confident zero.

Usage: derive-facts.py [<repo-root>]   (default: the current directory)
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from typing import NamedTuple

PREFIX = "derive-facts"
LAYOUT_HINT = (f"{PREFIX}: the tree layout moved; fix this script rather than typing the fact "
               "by hand.")
COPY_HINT = f'{PREFIX}: copy these into the checklist\'s "derived" block verbatim.'

CSPROJ = "src/AiRaccoon/AiRaccoon.csproj"
TOOLS_DIR = "src/AiRaccoon/Tools"
PROMPTS_DIR = "src/AiRaccoon/Prompts"

# A line whose trimmed content opens with one of these is prose. `McpToolInventory.cs` documents
# the attribute it reflects over, so the literal token appears in a `///` line that is not a tool.
COMMENT_OPENERS = ("///", "//", "*")
VERSION_RE = re.compile(r"<PackageVersion>(.*)</PackageVersion>")
# An MSBuild property reference this script cannot evaluate; reporting it verbatim would put an
# unresolved token in the checklist's `derived` block.
VERSION_PLACEHOLDER = "$(Version)"


class DerivationFailed(Exception):
    """A fact could not be derived; the message names which one."""


class Facts(NamedTuple):
    """The three facts a checklist run compares the running build against."""

    version: str
    tools: int
    prompts: int

    def render(self) -> str:
        """The stdout contract, quoted verbatim by SKILL.md and the checklist template."""
        return (f"version={self.version}\n"
                f"mcp-tool-count={self.tools}\n"
                f"mcp-prompt-count={self.prompts}\n")


def is_comment_line(line: str) -> bool:
    """Whether *line* is prose rather than code — a doc comment, a line comment, or a `/* */`
    block's continuation."""
    return line.lstrip().startswith(COMMENT_OPENERS)


def count_attribute_uses(source: str, attribute: str) -> int:
    """Occurrences of ``[<attribute>`` in *source*, ignoring comment lines.

    Counts per occurrence, not per line: two attributes on one line are two uses. Only a line
    that *opens* as a comment is dropped, so an attribute following code on the same line counts.
    """
    token = f"[{attribute}"
    return sum(line.count(token) for line in source.splitlines() if not is_comment_line(line))


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

    version = read_package_version(_read(csproj))
    if not version:
        raise DerivationFailed(f"no literal <PackageVersion> in {csproj}")

    return Facts(version=version,
                 tools=count_in_tree(tools_dir, "McpServerTool"),
                 prompts=count_in_tree(prompts_dir, "McpServerPrompt"))


def main(argv) -> int:
    """Print the derived facts, or the reason there are none. 0 on success, 1 on any failure."""
    try:
        facts = derive(Path(argv[0] if argv else "."))
    except DerivationFailed as failure:
        print(f"{PREFIX}: {failure}", file=sys.stderr)
        print(LAYOUT_HINT, file=sys.stderr)
        return 1
    print(facts.render(), end="")
    print(COPY_HINT, file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
