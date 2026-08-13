---
name: angular-static-content
description: "Build static content pages (blog, docs, articles) in Angular SPAs — routing, lazy-loaded pages, static TS data models, build-time RSS feed generation, table-of-contents, article pager navigation, and Angular+vitest testing patterns."
version: 1.0.0
metadata:
  hermes:
    tags: ["angular", "blog", "static-content", "rss", "articles"]
---

# Angular Static Content Pipeline

Build blog/docs/article pages in Angular SPAs using static TypeScript data models, lazy-loaded routes, and build-time RSS generation.

## When to Use

- Adding a blog, docs section, or any routable content page
- Converting Markdown articles to structured TypeScript
- Building article listing pages with previews
- Generating RSS feeds from static data

## Architecture

```
content/articles/*.md  →  scripts/build-articles.mjs  →  frontend/src/app/data/articles/*.data.ts
                                                         + articles.provider.ts (auto-generated)
```

                                              ArticleRenderer component
                                                           ↓
                                              /blog/:slug route

```

The build script runs automatically before `ng build` via `npm run build-articles` in package.json.

## Article TypeScript Model

All content types and helpers live in `frontend/src/app/models/article.ts`.

### Types

```typescript
interface Article {
  meta: ArticleMeta;
  sections: ArticleSection[];
}

interface ArticleMeta {
  title: string;
  subtitle?: string;
  publishedAt: string;      // ISO 8601
  updatedAt?: string;
  author: string;
  slug: string;
  coverImage?: string;
  description: string;       // SEO meta description
  tags: string[];
  readingTimeMinutes: number;
  status: 'draft' | 'review' | 'published';
}

interface ArticleSection {
  heading: string;
  id?: string;
  blocks: ContentBlock[];
  subsections?: ArticleSubsection[];
}
```

### ContentBlock Union

```typescript
type ContentBlock =
  | ParagraphBlock      // { type: 'paragraph', content: InlineContent[] }
  | HeadingBlock        // { type: 'heading', level: 2|3|4, content: InlineContent[] }
  | TableBlock          // { type: 'table', headers: TableRow, rows: TableRow[] }
  | ListBlock           // { type: 'list', ordered?: boolean, items: ListItem[] }
  | CalloutBlock        // { type: 'callout', variant: 'note'|'warning'|'tip'|'important' }
  | HorizontalRuleBlock // { type: 'hr' }
  | CodeBlock           // { type: 'code', language?: string, code: string }
  | ChartBlock          // { type: 'chart', id, kind, title, data: ChartDataPoint[] }
  | DiagramBlock        // { type: 'diagram', id, kind, title, nodes, edges, svg (pre-rendered at build time) }
  | MathBlock           // { type: 'math', formula, html (pre-rendered via KaTeX at build time), label? }
```

### Helper Functions

Import from `../../models/article`:

```typescript
// Inline content
text('hello')           // { type: 'text', text: 'hello' }
bold('important')       // { type: 'bold', text: 'important' }
code('npm install')     // { type: 'code', text: 'npm install' }
link('Docs', 'https://…') // { type: 'link', text: 'Docs', href: 'https://…' }

// Blocks
paragraph(text('...'), bold('...'))
heading(2, text('Section Title'))
bulletList(item(text('...')), item(bold('...'), text('...')))
callout('note', text('Important info'))
chart('id', 'bar-horizontal', 'Title', [{ label: 'A', value: 100 }])
diagram('auth-flow', 'flowchart', 'Auth Flow', [{id:'a',label:'A'}], [{from:'a',to:'b'}], '<svg>...</svg>', {description: 'Optional desc'})
hr()

// Tables
table(
  [cell(bold('Col A')), cell(bold('Col B'))],  // headers
  [[cell(text('A1')), cell(text('B1'))]],       // rows
)
cell(text('content'))
row(cell(text('A')), cell(text('B')))
item(text('list item'))
```

## Data File Pattern

Each article lives in `frontend/src/app/data/articles/{slug}.data.ts`:

```typescript
import type { Article } from '../../models/article';
import { bold, text, paragraph, table, cell, row, item, bulletList, callout, chart, diagram } from '../../models/article';

export const myArticle: Article = {
  meta: {
    title: 'My Article',
    publishedAt: '2026-07-23',
    author: 'Rafał Araszkiewicz',
    slug: 'my-article',
    description: 'Short description',
    tags: ['tag1', 'tag2'],
    readingTimeMinutes: 5,
    status: 'published',
  },
  sections: [
    {
      heading: 'Section One',
      id: 'section-one',
      blocks: [
        paragraph(text('Content here')),
      ],
    },
  ],
};
```

## Blog Listing

Blog summaries for listing pages live in `frontend/src/app/data/blog-posts.data.ts`:

```typescript
export interface BlogPost {
  readonly id: string;
  readonly title: string;
  readonly date: string;
  readonly summary: string;
  readonly body: string;    // Not used for Article-rendered posts
  readonly tags: readonly string[];
  readonly category: 'project' | 'engineering' | 'career';
}
```

The main page blog preview limits to the latest 3 posts (`.slice(0, 3)`).

## Components

| Component         | Path                           | Purpose                                               |
|-------------------|--------------------------------|-------------------------------------------------------|
| `ArticleRenderer` | `components/article-renderer/` | Renders Article → HTML                                |
| `ChartRenderer`   | `components/chart-renderer/`   | Renders ChartBlock → ApexCharts (monochrome)          |
| `DiagramRenderer` | `components/diagram-renderer/` | Renders DiagramBlock → SVG via DomSanitizer innerHTML |
| `BlogPreview`     | `components/blog-preview/`     | Main page latest 3 posts                              |
| `BlogArticlePage` | `pages/blog-article/`          | Single article page                                   |

Chart rendering details: `skill_view(name, file_path='references/apexcharts-integration.md')` (current). Legacy SVG patterns: `references/svg-chart-rendering.md`. Diagram rendering: `references/diagram-rendering.md`. LaTeX math equation
rendering: `skill_view(name, file_path='references/latex-math-rendering.md')`.

## MD-to-TS Converter

```bash
# Node.js script (not Python) — runs automatically before ng build
npm run build-articles
# Or directly: node scripts/build-articles.mjs
```

Parses Markdown with YAML frontmatter, outputs TypeScript Article objects, generates `articles.provider.ts` barrel file, and runs ESLint --fix on generated files.

Tests: `node --test scripts/tests/test-build-articles.mjs` (64 tests).

Key behaviors:

- Dynamic imports: scans generated TS for which helpers are actually called, only imports those
- ESLint --fix: auto-fixes unused imports, formatting in generated files
- Parser safety: always advances line index in parsing loops to prevent infinite loops
- Frontmatter required: MD files must start with `---` YAML frontmatter block
- Diagram parsing: `<!-- DIAGRAM: id -->` triggers dagre layout + SVG rendering at build time (see `references/diagram-rendering.md`)
- `parseBlocks()` and `convertMdToArticle()` are async (dynamic import of dagre for diagram blocks)

## Testing

```bash
cd frontend && npm run test:single    # vitest
cd frontend && npm run lint           # eslint
cd frontend && npx ng build           # Angular build
```

All three must pass. The `theme-tokens.spec.ts` test enforces monochrome CSS (no colored hex values).

## Reviewing ApexCharts Integration

When reviewing Angular components that use `apexcharts` / `ng-apexcharts`, load the ApexCharts MCP reference (`apexcharts_get_reference(file='SKILL.md')`) for the authoritative data format table, formatter signatures, and pitfalls. Then
follow the structured checklist:
`skill_view(name, file_path='references/apexcharts-review-checklist.md')`.

Key review areas: series data format per chart type, formatter function signatures, non-axis chart config hygiene, global CSS override for `* { max-width: 100% }`, accessibility (aria-labelledby on apx-chart), ResizeObserver polyfill in
tests, and fmt () consistency.

## Pitfalls

1. **Angular template `Math` not available** — use a component method like `abs()` instead.
2. **Protected members in tests** — cast to `any` to test protected computed signals, but prefer making public.
3. **`::ng-deep` for innerHTML content** — Angular encapsulation blocks styles on `[innerHTML]` children.
4. **Monochrome CSS only** — the theme-tokens test catches any colored hex values.
5. **`as const` on table objects** — causes build errors with Angular's compiler. Don't use.
6. **String escaping in TS data files** — use template literals or Unicode escapes for apostrophes. Single-quoted strings with `'` (apostrophe) break the parser. Use `\u2019` or double-quoted strings.
7. **Unicode escapes in templates** — `\u2193` in Angular templates renders as literal text, not the ↓ character. Use the actual Unicode character directly.
8. **`virtual` keyword not valid in TypeScript** — methods are virtual by default. Using `virtual fmt(...)` causes parsing errors.
9. **Wiring structured renderer with legacy fallback** — when migrating from `[innerHTML]` to a structured renderer, check for the structured data first (`@if (article())`), fall back to legacy innerHTML in `@else`. This lets old posts keep
   working.
10. **Git workflow for cleanup** — when removing old code that's being replaced, commit it in a single commit so it's restorable from git history. Don't mix cleanup with feature work.
11. **Articles provider barrel file** — auto-generate `articles.provider.ts` that imports all article data files and exports them as a `Record<string, Article>` map. Update `BlogArticlePage` to import from the provider instead of individual
    files.
12. **Parser infinite loop** — in any line-by-line parser, always advance the index `i` when a line doesn't match any handler. Pattern: `if (matched) { ... } else { i++; }`. Without this, unhandled lines (like HTML comments that aren't
    chart placeholders) cause infinite loops.
13. **Dynamic imports in generated code** — scan generated TS content for which helper functions are actually called before emitting the import line. Prevents lint errors from unused imports.
14. **ESLint --fix on generated files** — run `npx eslint --fix` on generated `.data.ts` files as part of the build pipeline. Catches formatting and unused-import issues automatically.
15. **Diagram rendering via dagre (not mermaid)** — Mermaid is NOT supported. For directed graphs (flowcharts, state machines), use the `<!-- DIAGRAM: id -->` HTML comment syntax (see `references/diagram-rendering.md`). The build script
    runs dagre layout at build time, generates self-contained SVG, and embeds it in the `.data.ts` file. The Angular `DiagramRendererComponent` injects the SVG via `DomSanitizer.bypassSecurityTrustHtml()`. Diagram code blocks
    (`language=mermaid`) show as raw text — use the DIAGRAM comment syntax instead.
16. **SVG chart viewBox clipping** — labels at `y=-2` get clipped outside viewBox. Add `labelPad=14` top padding to viewBox height and translate bars down by labelPad.
17. **Wide value range in charts** — when values span >10x (e.g., $0.06 vs $4.00), linear scaling makes small bars invisible. Auto-switch to log scale when `max/min > 10`. See `references/svg-chart-rendering.md`.
18. **Chart metadata in MD files** — specify chart data via consecutive HTML comments after `<!-- CHART: id -->`: `<!-- kind: bar-horizontal -->`, `<!-- title: ... -->`, `<!-- data: Label1 $value1, Label2 $value2 -->`,
    `<!-- lowerIsBetter: true -->`, `<!-- xLabel: ... -->`. The converter parses these into ChartBlock with real data.
19. **Integration tests for converters** — always add tests that convert real MD files (not just synthetic test fixtures). Verify brace/paren balance, check for TODO placeholders, assert specific content exists.
20. **Component renaming** — when renaming a component (e.g., chart-placeholder → chart-renderer), update: file names, class name, selector, CSS file reference, all imports across the codebase. Use a batch find-replace script.
21. **E2E WebKit timing** — WebKit (Safari) needs explicit `await element.waitFor({state: 'visible'})` before `toBeVisible()` assertions. Angular's `@if` change detection may not complete in time. For buttons that don't register clicks on
    WebKit, use `{force: true}`. Always test with `--project=webkit` — failures are WebKit-specific.
22. **ESLint `_v` prefix** — the project's ESLint config does NOT allow `_` prefix for unused vars. Use `() => 0` instead of `(_v: number) => 0` for no-op lambdas.
23. **`scaleFactor` public for tests** — if you need to test computed signals in spec files, make them public rather than protected. Casting to `any` triggers `no-explicit-any` lint errors.

27. **SVG viewBox max-height** — don't set `max-height: 300px` on chart SVGs — it clips tall bar charts. Let viewBox control sizing.
28. **Donut CSS nth-child vs inline fill** — don't use CSS `nth-child` to override donut segment colors. It fights with inline `[attr.fill]` from TS. Use inline fill only, driven by the `displayColor` computed.
29. **Component `protected` → `public` for testability** — prefer making computed signals and methods public if tests need to access them. The alternative (`as any`) triggers `no-explicit-any` lint errors.
30. **Chart `fmt()` formatting** — don't hardcode `$` for all values. Use magnitude-based formatting: `$` only for values < 1 (small costs), plain numbers for larger values.
31. **Vertical bar chart height** — don't reuse `barChartHeight()` (computed from item count). Use a fixed `verticalBarHeight = 220`. Otherwise 2 items give extremely short bars, 10 items give enormous viewBox.
32. **Timeline value clamping** — timeline bars assume a max value (e.g., 7 days). Always clamp: `clamp(p.value, 0, 7)`. Otherwise values > max overflow the SVG.
33. **ApexCharts in Angular — computed signals don't trigger render** — `apx-chart` component does NOT react to Angular computed signal inputs. Use `AfterViewInit` + `ViewChild` + explicit `updateOptions()` call. Pattern: `effect()` updates
    a plain property + calls `updateOptions` if view is ready; `ngAfterViewInit` sets `ready = true` and calls `updateOptions` once.
34. **ApexCharts `width: '100%'`** — always set `width: '100%'` in the chart config object. Without it, ApexCharts renders at the parent element's natural width which can cause horizontal overflow and scrollbars.
35. **ApexCharts template binding** — Use the defaults pattern (see pitfall 46): `buildOptions` returns `Required<ApexOptions>` natively, `get opts()` returns `this.apexOptions` with no cast. Bind as `[series]="opts.series"`,
    `[chart]="opts.chart"`, etc. See `references/apexcharts-integration.md` for the full defaults object and pattern.
36. **ApexCharts monochrome theme** — use `theme: { mode: 'dark', monochrome: { enabled: true, color: '#838388', shadeTo: 'light', shadeIntensity: 0.6 } }` with design token colors in the `colors` array for per-segment control.
37. **ApexCharts SSR chunk** — `ng-apexcharts` imports `apexcharts/ssr` dynamically for SSR support. This shows as a ~183KB lazy chunk in the build but is NOT executed in browser. Don't try to tree-shake it — it's expected.
38. **Chart library selection** — for monochrome terminal aesthetic: ApexCharts wins (built-in monochrome theme, CSS custom properties `--apx-*`, SVG output, ~130KB gzipped). Chart.js is lighter but Canvas-based. ECharts is heaviest but
    most powerful. AG Charts is enterprise-grade. See `references/chart-library-comparison.md`.
39. **ApexCharts in tests** — `apx-chart` doesn't render real charts in unit tests (no DOM). Test the options object instead of rendered output. Make `apexOptions` a public plain property (not a signal) so tests can read it directly.
41. **ALWAYS edit the MD source, never the TS data file directly** — the pipeline is `content/articles/*.md` → `scripts/build-articles.mjs` → `*.data.ts`. The TS files are auto-generated and will be overwritten. Edit the markdown source in
    `content/articles/`, then run `node scripts/build-articles.mjs` from the project root to regenerate. Editing the TS file directly means your changes get lost on the next build. If you're unsure whether a TS file is generated, check for
    the comment `// Auto-generated by scripts/build-articles.mjs` at the top.
40. **Angular `effect()` does NOT support async callbacks** — `effect(async () => {...})` returns a Promise immediately. Angular's dependency tracking only works synchronously, so signal reads inside the async body are NOT tracked. The
    effect never re-fires when signals change. Use a synchronous callback: `effect(() => { ... })`. If you need async work, use `afterNextRender` or `afterRenderEffect` instead. This was the root cause of ApexCharts charts appearing empty —
    the `this.chart()` read inside the async effect was never tracked.
42. **Global `* { max-width: 100% }` breaks chart libraries** — Common CSS resets with `* { max-width: 100% }` bleed into chart library SVG internals (ApexCharts `foreignObject`, canvas, bars). Horizontal bars render as zero-width. **The
    override MUST be in global `styles.css`, NOT component CSS** — Angular ViewEncapsulation blocks component styles from reaching third-party library internals. Add right after the `*` rule in styles.css:
    `.apexcharts-canvas, .apexcharts-svg, .apexcharts-canvas foreignObject, .apexcharts-canvas svg { max-width: none !important; width: auto !important; }`. Debug by inspecting `foreignObject` computed styles.
43. **`ng-apexcharts` version pinning** — `ng-apexcharts@2.4.0` requires `apexcharts@^5.10.3`. Installing `apexcharts@6.x` breaks horizontal bars (they vanish). Pin `apexcharts` to `~5.16.0`. Check `npm ls` for `invalid` warnings after dep
    updates.
44. **`labelStyle` nesting** — `{colors, fontSize, fontFamily}` is a *style* sub-object. ApexCharts `xaxis.labels` expects `{style: labelStyle, formatter?, ...}`. Wrap: `const labelsConfig = {style: labelStyle}`. Passing `labelStyle`
    directly worked with `any` but fails with `ApexOptions` types.
45. **Test imports from `ng-apexcharts`** — Import `ApexOptions`, `ApexAxisChartSeries` from `ng-apexcharts`, not `apexcharts` directly. The two packages re-export their own type copies; importing from `apexcharts` causes type mismatch
    errors when versions diverge. Use `!` non-null assertions in tests since test data is controlled.
46. **`buildOptions` return `Required<ApexOptions>` with defaults pattern** — Don't use `as Required<ApexOptions>` cast on the `opts` getter (the user explicitly rejected this). Instead, make `buildOptions` return `Required<ApexOptions>`
    natively by defining a `defaults` object typed as `Required<ApexOptions>` that fills ALL 23 top-level fields. Each switch case spreads `...defaults` and overrides only what differs. The `opts` getter then returns `this.apexOptions`
    directly — no cast needed. The full field list: `chart`, `series`, `labels`, `colors`, `theme`, `title`, `tooltip`, `legend`, `plotOptions`, `dataLabels`, `xaxis`, `yaxis`, `grid`, `fill`, `stroke`, `markers`, `annotations`, `noData`,
    `responsive`, `forecastDataPoints`, `states`, `subtitle`, `parsing`.
47. **ResizeObserver polyfill in tests** — ApexCharts calls `ResizeObserver` during `render()`. In jsdom (vitest/jest), this API doesn't exist. The error is non-fatal (tests still pass) but pollutes output with
    `ReferenceError: ResizeObserver is not defined` on every chart-rendering test. Fix: create `frontend/src/test-setup.ts` with a no-op polyfill and reference it in `angular.json` → `test.options.setupFiles`:
   ```typescript
   // frontend/src/test-setup.ts
   if (typeof globalThis.ResizeObserver === 'undefined') {
     globalThis.ResizeObserver = class ResizeObserver {
       observe(): void { /* no-op in jsdom */ }
       unobserve(): void { /* no-op in jsdom */ }
       disconnect(): void { /* no-op in jsdom */ }
     };
   }
   ```
   ```jsonc
   // angular.json → architect.test.options
   "setupFiles": ["src/test-setup.ts"]
   ```
The empty method bodies need comments to satisfy `@typescript-eslint/no-empty-function`.
48. **`overflow: hidden` may clip tooltips** — ApexCharts tooltips can render outside the chart container bounds (especially near viewport edges or with `tooltip.position: 'bottom'`). If the chart wrapper uses `overflow: hidden`, tooltips
    get clipped. Test by hovering near chart edges; if clipped, switch to `overflow: visible` or use `overflow-x: hidden; overflow-y: visible`.
49. **Non-axis chart config noise** — Donut, pie, polarArea, and radialBar ignore `xaxis`, `yaxis`, `grid`, and `markers`. When using the `Required<ApexOptions>` cast pattern, these fields must be present (type requirement) but are
    meaningless. Document this in a comment. Don't rely on grid/axis behavior in non-axis chart branches.
50. **Accessibility: `aria-labelledby` on chart** — `<apx-chart>` doesn't auto-associate with its title. Add `[attr.aria-labelledby]="chartId + '-title'"` on the `apx-chart` element and `[id]="chartId + '-title'"` on the title heading.
    Without this, screen readers can't connect the chart to its label. For the general `aria-describedby` pattern on Angular interactive elements (tabs, links, meters), see the `angular-component-a11y` skill.
51. **`fmt()` formatter consistency** — Don't mix `$` prefix with plain numbers in a single formatter. If the formatter is shared across chart types (cost and non-cost), keep it generic: magnitude-based formatting only (`1.5K`, `100`,
    `0.050`). Add `!Number.isFinite(v)` guard for NaN/Infinity edge cases, returning `'—'`. If currency display is needed, create a separate `fmtCurrency()` and pass it only to cost-related charts.
52. **ApexCharts MCP `get_reference` file parameter** — The `file` parameter uses bare filenames without the `references/` prefix. Use `bar-charts.md` not `references/bar-charts.md`. Available files: `SKILL.md`, `cartesian-charts.md`,
    `bar-charts.md`, `financial-charts.md`, `circular-charts.md`, `grid-charts.md`, `radar-charts.md`, `v6-features.md`, `tree-shaking.md`, `ssr.md`, `framework-wrappers.md`. The server can crash under rapid consecutive calls — batch reads
    or pace them.
53. **Tests encode format requirements** — build-articles tests assert structural properties of articles (e.g. presence of `callout(`, `table(`, `type: "code"`). When rewriting an article, check the corresponding test assertions first.
    Dropping callouts or tables to simplify prose will break tests even if the article reads fine.
54. **Derived artifact regeneration** — after editing any source file that feeds a code-generation step, regenerate the derived artifacts before running build/test. For articles: `node scripts/build-articles.mjs`. The `npm run build`
    wrapper in this project DOES include `build-articles` as a pre-step, but `npm run test:single` does NOT — run the generator explicitly before tests. Stale generated files produce green builds against old content.
55. **Path migration checklist** — when moving content directories, update: (1) the build script's source path constant, (2) the test file's path constant, (3) documentation references in pipeline docs, (4) auto-generated comments in output
    files (re-run generator), (5) grep the entire repo for the old path to catch stragglers.
56. **Async migration ripple effect** — making `parseBlocks()` async (e.g., for dynamic import of dagre) cascades to `convertMdToArticle()` which calls it, then to `main()` which calls that, and to every test callback that calls either
    function. Checklist: (1) make the function async, (2) add `await` at every call site, (3) make all test callbacks that call it `async`, (4) verify the build script's `main()` function awaits the now-async function. Missing any call site
    produces `TypeError: Cannot read properties of undefined (reading 'match')` because the function returns a Promise instead of the expected object.
57. **Double `async` when bulk-replacing test callbacks** — when making test callbacks async via script (replacing `() => {` with `async () => {`), the script will double-up on callbacks that were already `async`. Result:
    `async async () => {` which is a syntax error. Fix: after bulk replace, also replace `async async` with `async`. Better approach: only replace callbacks that aren't already async (check for `async` before `()`).
59. **Backup files in `content/articles/` break the build** — `build-articles.mjs` scans ALL `*.md` files in `content/articles/` and treats each as an article. Placing a backup file (e.g., `article.bck.md`) in that directory causes the
    script to generate a duplicate `.data.ts` and a duplicate entry in `articles.provider.ts`, which produces a TypeScript duplicate-property error. When backing up an article before updating, place the backup OUTSIDE `content/articles/`
    (e.g., `docs/` or a dedicated `content/articles/.backups/` that the script ignores). The script has no exclusion mechanism — every `.md` in the directory is a candidate.
60. **`link()` spread includes optional params as `undefined` keys** — `link('x', 'href')` returns `{type: 'link', text: 'x', href: 'href', title: undefined}`. The `title` key IS present in the object (just `undefined`). Don't assert
    `'title' in result === false` — use `toEqual({..., title: undefined})` or omit the check. This applies to all helper functions that use spread with optional params (`chart`, `diagram`).
61. **TypeScript union narrowing in tests** — when testing functions that return a discriminated union (like `InlineContent = text | bold | code | link`), you cannot access variant-specific properties (`.title`, `.href`) without narrowing.
    Use `toEqual()` with the full expected shape instead of accessing properties individually. Pattern: `expect(link('x', 'url')).toEqual({type: 'link', text: 'x', href: 'url'})` rather than `expect(result.href).toBe('url')` (which fails
    TypeScript type-checking).
58. **dagre point order — never sort** — dagre returns edge waypoints in source→target traversal order. For cycles/back-edges, this means points may go bottom-to-top. **Do NOT sort points by y-coordinate** — sorting breaks cycle rendering.
    Use dagre's original point order for Bézier curve generation.
62. **KaTeX CSS in component style file breaches 8 kB budget** — Always import `@import 'katex/dist/katex.min.css';` in global `styles.css`. Putting it in component CSS (`article-renderer.css`) violates Angular CLI's `anyComponentStyle`
    error budget limit (8 kB).
63. **Build-time KaTeX pre-rendering vs client JS bloat** — Render KaTeX equations to HTML strings in `build-articles.mjs` during static site generation instead of shipping KaTeX JS (~85 KB gzipped) to client browsers. Gives 0 KB client JS
    overhead, 0 CLS, and instant MathML accessibility.
