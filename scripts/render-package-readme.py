#!/usr/bin/env python3
"""Render README.md's mermaid blocks to SVG for the NuGet package readme.

nuget.org does not run JavaScript, so ```mermaid blocks in the packed
README.md show up as raw code. This script renders each block via kroki.io
(deflate+base64 GET) and rewrites them as inline <div> SVG, writing the
result to the path given after -- (never over the repo source).

Usage:  render-package-readme.py [--kroki-url URL] -- OUTPUT [INPUT]
        INPUT defaults to README.md beside the script's repo root.
"""
import argparse
import base64
import pathlib
import re
import sys
import urllib.error
import urllib.request
import zlib

DEFAULT_KROKI = "https://kroki.io"

MERMAID_BLOCK = re.compile(r"^[ \t]*```mermaid[ \t]*\n(.*?)^[ \t]*```[ \t]*$", re.S | re.M)

# Relative doc links are dead inside the package (no repo files shipped); point them at
# the repo so nuget.org readers land on the real page. Absolute URLs pass through.
REPO_URL = "https://github.com/Arasz/ai-raccoon/blob/main/"
LINK = re.compile(r"(\]\()(?!https?://|#|mailto:)([^)#]+)(#[^)]*)?\)")


def kroki_svg(source: str, base_url: str) -> str:
    """Render one mermaid block; raises on any non-200 so pack fails loudly."""
    encoded = base64.urlsafe_b64encode(zlib.compress(source.encode(), 9)).decode()
    url = f"{base_url}/mermaid/svg/{encoded}"
    request = urllib.request.Request(url, headers={"User-Agent": "ai-raccoon-pack/1.0"})
    with urllib.request.urlopen(request, timeout=120) as response:
        if response.status != 200:
            raise RuntimeError(f"kroki returned {response.status}")
        return response.read().decode()


def rewrite(readme: str, base_url: str) -> tuple[str, int]:
    count = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal count
        count += 1
        svg = kroki_svg(match.group(1), base_url)
        # Inline width style: SVG keeps its aspect ratio but never exceeds the page.
        svg = re.sub(r"<svg ", '<svg style="max-width:100%;height:auto;" ', svg, count=1)
        return f'<p align="center">\n\n{svg}\n\n</p>'

    rendered = MERMAID_BLOCK.sub(replace, readme)

    def relink(match: re.Match[str]) -> str:
        prefix, path, fragment = match.groups()
        return f"{prefix}{REPO_URL}{path}{fragment or ''})"

    return LINK.sub(relink, rendered), count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kroki-url", default=DEFAULT_KROKI)
    parser.add_argument("output", help="path to write the rendered readme to")
    parser.add_argument("input", nargs="?", default=None, help="source readme (default: README.md)")
    args = parser.parse_args()

    input_path = pathlib.Path(args.input) if args.input else None
    if input_path is None:
        # Default: repo root two levels up from scripts/.
        input_path = pathlib.Path(__file__).resolve().parent.parent / "README.md"
    output_path = pathlib.Path(args.output)

    readme = input_path.read_text(encoding="utf-8")
    try:
        rendered, count = rewrite(readme, args.kroki_url.rstrip("/"))
    except (urllib.error.URLError, RuntimeError) as error:
        print(f"render-package-readme: kroki render failed: {error}", file=sys.stderr)
        return 1

    if count == 0:
        print("render-package-readme: no mermaid blocks found — copying unchanged")
    else:
        print(f"render-package-readme: rendered {count} mermaid block(s)")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(rendered, encoding="utf-8")
    print(f"render-package-readme: wrote {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
