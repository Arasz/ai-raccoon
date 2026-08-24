#!/usr/bin/env bun
/**
 * Render README.md's mermaid blocks to SVG for the NuGet package readme.
 *
 * nuget.org does not run JavaScript, so ```mermaid blocks in the packed
 * README.md show up as raw code. This script renders each block via kroki.io
 * (deflate+base64 GET) and rewrites them as inline <div> SVG, writing the
 * result to the path given after -- (never over the repo source).
 *
 * Usage:  bun run scripts/render-package-readme.ts [--kroki-url URL] -- OUTPUT [INPUT]
 *         INPUT defaults to README.md at the repo root.
 */

import { resolve, dirname } from "path";
import { readFileSync, writeFileSync, mkdirSync } from "fs";
import { deflateSync } from "zlib";

const DEFAULT_KROKI = "https://kroki.io";

const MERMAID_BLOCK =
  /^[ \t]*```mermaid[ \t]*\n(.*?)^[ \t]*```[ \t]*$/gms;

// Relative doc links are dead inside the package (no repo files shipped); point them at
// the repo so nuget.org readers land on the real page. Absolute URLs pass through.
const REPO_URL = "https://github.com/Arasz/ai-raccoon/blob/main/";
const LINK = /\]\((?!https?:\/\/|#|mailto:)([^)#]+)(#[^)]*)?\)/g;

function deflateBase64(source: string): string {
  // kroki.io accepts zlib deflate+base64 (RFC 1950, matching Python's zlib.compress)
  const compressed = deflateSync(Buffer.from(source), { level: 9 });
  return compressed.toString("base64url");
}

async function krokiSvg(source: string, baseUrl: string): Promise<string> {
  const encoded = deflateBase64(source);
  const url = `${baseUrl}/mermaid/svg/${encoded}`;
  const response = await fetch(url, {
    headers: { "User-Agent": "ai-raccoon-pack/1.0" },
    signal: AbortSignal.timeout(120_000),
  });
  if (!response.ok) {
    throw new Error(`kroki returned ${response.status}`);
  }
  return await response.text();
}

async function rewrite(
  readme: string,
  baseUrl: string
): Promise<[string, number]> {
  let count = 0;

  const matches = [...readme.matchAll(MERMAID_BLOCK)];
  if (matches.length === 0) return [readme, 0];

  let result = readme;
  // Process in reverse order so string indices stay valid
  for (const match of matches.reverse()) {
    const svg = await krokiSvg(match[1]!, baseUrl);
    // Inline width style: SVG keeps its aspect ratio but never exceeds the page.
    const styledSvg = svg.replace(
      "<svg ",
      '<svg style="max-width:100%;height:auto;" '
    );
    const replacement = `<p align="center">\n\n${styledSvg}\n\n</p>`;
    result =
      result.slice(0, match.index!) +
      replacement +
      result.slice(match.index! + match[0].length);
    count++;
  }

  // Rewrite relative doc links to point at the repo
  result = result.replace(LINK, (_match, path: string, fragment?: string) => {
    return `](${REPO_URL}${path}${fragment ?? ""})`;
  });

  return [result, count];
}

async function main(): Promise<number> {
  const args = process.argv.slice(2);

  let krokiUrl = DEFAULT_KROKI;
  const krokiIdx = args.indexOf("--kroki-url");
  if (krokiIdx !== -1 && args[krokiIdx + 1]) {
    krokiUrl = args[krokiIdx + 1]!;
    args.splice(krokiIdx, 2);
  }

  // Find the -- separator
  const dashIdx = args.indexOf("--");
  const positional = dashIdx !== -1 ? args.slice(dashIdx + 1) : args;

  if (positional.length === 0) {
    console.error(
      "Usage: render-package-readme.ts [--kroki-url URL] -- OUTPUT [INPUT]"
    );
    return 1;
  }

  const outputPath = resolve(positional[0]!);
  const inputPath = positional[1]
    ? resolve(positional[1])
    : resolve(dirname(new URL(import.meta.url).pathname), "..", "README.md");

  const readme = readFileSync(inputPath, "utf-8");
  let rendered: string;
  let count: number;
  try {
    [rendered, count] = await rewrite(readme, krokiUrl.replace(/\/+$/, ""));
  } catch (error: any) {
    console.error(`render-package-readme: kroki render failed: ${error.message}`);
    return 1;
  }

  if (count === 0) {
    console.log("render-package-readme: no mermaid blocks found — copying unchanged");
  } else {
    console.log(`render-package-readme: rendered ${count} mermaid block(s)`);
  }

  mkdirSync(dirname(outputPath), { recursive: true });
  writeFileSync(outputPath, rendered, "utf-8");
  console.log(`render-package-readme: wrote ${outputPath}`);
  return 0;
}

process.exit(await main());
