#!/usr/bin/env tsx
/**
 * Render README.md's mermaid blocks to PNG image links for the NuGet package readme.
 *
 * nuget.org does not run JavaScript, so ```mermaid blocks in the packed
 * README.md show up as raw code — and nuget.org only renders <img> sources from
 * trusted domains via ABSOLUTE urls (relative paths inside the package are not
 * rendered; NuGetGallery#9610). So this script renders each mermaid block to a
 * PNG via kroki.io, stores it under the repo's asset directory (committed), and
 * rewrites the block to an absolute raw.githubusercontent.com image link.
 *
 * Filenames derive from a sha256 of the diagram source, so an unchanged diagram
 * keeps its file and never churns git history.
 *
 * Usage:  bun run scripts/render-package-readme.ts [--kroki-url URL] [--assets-dir DIR] -- OUTPUT [INPUT]
 *         INPUT defaults to README.md at the repo root.
 */

import { resolve, dirname, join, relative } from "path";
import { readFileSync, writeFileSync, mkdirSync } from "fs";
import { deflateSync } from "zlib";
import { createHash } from "crypto";

const DEFAULT_KROKI = "https://kroki.io";
const REPO_URL = "https://github.com/Arasz/ai-raccoon/blob/main/";
const RAW_URL = "https://raw.githubusercontent.com/Arasz/ai-raccoon/main/";

const MERMAID_BLOCK = /^[ \t]*```mermaid[ \t]*\n(.*?)^[ \t]*```[ \t]*$/gms;

// Relative doc links are dead inside the package (no repo files shipped); point them at
// the repo so nuget.org readers land on the real page. Absolute URLs pass through.
const LINK = /\]\((?!https?:\/\/|#|mailto:)([^)#]+)(#[^)]*)?\)/g;

function deflateBase64(source: string): string {
  // kroki.io accepts zlib deflate+base64 (RFC 1950, matching Python's zlib.compress)
  const compressed = deflateSync(Buffer.from(source), { level: 9 });
  return compressed.toString("base64url");
}

async function krokiFetch(kind: string, source: string, baseUrl: string): Promise<Buffer> {
  const encoded = deflateBase64(source);
  const url = `${baseUrl}/mermaid/${kind}/${encoded}`;
  const response = await fetch(url, {
    headers: { "User-Agent": "ai-raccoon-pack/1.0" },
    signal: AbortSignal.timeout(120_000),
  });
  if (!response.ok) {
    throw new Error(`kroki returned ${response.status} for ${kind}`);
  }
  return Buffer.from(await response.arrayBuffer());
}

interface Rendered {
  count: number;
}

async function rewrite(
  readme: string,
  baseUrl: string,
  repoRoot: string,
  assetsDir: string,
  rawBase: string
): Promise<[string, Rendered]> {
  const matches = [...readme.matchAll(MERMAID_BLOCK)];
  if (matches.length === 0) return [readme, { count: 0 }];

  mkdirSync(assetsDir, { recursive: true });

  let result = readme;
  // Process in reverse order so string indices stay valid
  let count = 0;
  for (const match of [...matches].reverse()) {
    const source = match[1]!;
    count++;
    const hash = createHash("sha256").update(source).digest("hex").slice(0, 12);
    const fileName = `diagram-${hash}.png`;

    // Reuse an existing identical asset; only hit kroki when missing.
    const assetPath = join(assetsDir, fileName);
    if (!exists(assetPath)) {
      const png = await krokiFetch("png", source, baseUrl);
      if (png.length < 1000) {
        throw new Error(`kroki png for block ${count} looks invalid (${png.length} bytes)`);
      }
      writeFileSync(assetPath, png);
    }

    const relToRepo = relative(repoRoot, assetPath).split(/[\\/]/).join("/");
    const replacement =
      `<p align="center">\n\n<img src="${rawBase}${relToRepo}" alt="diagram ${count}" />\n\n</p>`;
    result =
      result.slice(0, match.index!) +
      replacement +
      result.slice(match.index! + match[0].length);
  }

  // Rewrite relative doc links to point at the repo
  result = result.replace(LINK, (_m, path: string, fragment?: string) => {
    return `](${REPO_URL}${path}${fragment ?? ""})`;
  });

  return [result, { count }];
}

function exists(p: string): boolean {
  try {
    readFileSync(p);
    return true;
  } catch {
    return false;
  }
}

async function main(): Promise<number> {
  const args = process.argv.slice(2);

  let krokiUrl = DEFAULT_KROKI;
  let assetsDirOpt: string | null = null;

  const takeValue = (flag: string): string | null => {
    const idx = args.indexOf(flag);
    if (idx !== -1 && args[idx + 1]) {
      const v = args[idx + 1]!;
      args.splice(idx, 2);
      return v;
    }
    return null;
  };

  krokiUrl = takeValue("--kroki-url") ?? krokiUrl;
  assetsDirOpt = takeValue("--assets-dir");

  const dashIdx = args.indexOf("--");
  const positional = dashIdx !== -1 ? args.slice(dashIdx + 1) : args;

  if (positional.length === 0) {
    console.error(
      "Usage: render-package-readme.ts [--kroki-url URL] [--assets-dir DIR] -- OUTPUT [INPUT]"
    );
    return 1;
  }

  const outputPath = resolve(positional[0]!);
  const inputPath = positional[1]
    ? resolve(positional[1])
    : resolve(dirname(new URL(import.meta.url).pathname), "..", "README.md");

  const repoRoot = resolve(dirname(inputPath));
  const assetsDir = assetsDirOpt ? resolve(assetsDirOpt) : join(repoRoot, "docs", "assets", "readme");

  const readme = readFileSync(inputPath, "utf-8");
  let rendered: string;
  let stats: Rendered;
  try {
    [rendered, stats] = await rewrite(readme, krokiUrl.replace(/\/+$/, ""), repoRoot, assetsDir, RAW_URL);
  } catch (error: any) {
    console.error(`render-package-readme: kroki render failed: ${error.message}`);
    return 1;
  }

  if (stats.count === 0) {
    console.log("render-package-readme: no mermaid blocks found — copying unchanged");
  } else {
    console.log(`render-package-readme: rendered ${stats.count} mermaid block(s) to ${assetsDir}`);
  }

  mkdirSync(dirname(outputPath), { recursive: true });
  writeFileSync(outputPath, rendered, "utf-8");
  console.log(`render-package-readme: wrote ${outputPath}`);
  return 0;
}

main().then((code) => process.exit(code));
