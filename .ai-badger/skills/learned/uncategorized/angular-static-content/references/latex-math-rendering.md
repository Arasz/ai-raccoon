# LaTeX Equation Rendering in Angular SSG Content Pipelines

Architecture, build-time pre-rendering patterns, CSS/font loading strategies, and security sanitization for KaTeX / MathJax equation rendering in Angular 22+ static site generation (SSG) article pipelines (`build-articles.mjs`).

---

## 1. Build-Time Pre-rendering vs. Client Runtime

When adding LaTeX math rendering (`$...$` inline and `$$...$$` block) to Angular SSG blogs:

| Metric                    | Build-Time Pre-rendering (`build-articles.mjs`)      | Client-Side Runtime Component                          |
|:--------------------------|:-----------------------------------------------------|:-------------------------------------------------------|
| **Client JS Overhead**    | **0 KB** (KaTeX is a build devDependency)            | **~85 KB - 100 KB gzipped** (KaTeX JS bundle)          |
| **Initial Bundle Budget** | **0% impact** (complies with 500kB limit)            | **+20% initial bundle size** or requires lazy chunking |
| **Layout Shift (CLS)**    | **0.00** (Prerendered HTML contains exact layout)    | **0.05 - 0.25** (Reflow when raw math transforms)      |
| **FOUT / Flash of LaTeX** | **None** (HTML rendered statically)                  | **Visible flash** of raw `$formula$` code              |
| **SEO & Accessibility**   | **100% accessible** (embedded MathML `<annotation>`) | **Requires JS execution** by crawlers                  |
| **Node Build Speed**      | **Fast** (KaTeX processes 100+ formulas in < 20ms)   | **0ms** build time                                     |

**Recommendation**: **Build-time pre-rendering with KaTeX** in Node.js. Matches existing `diagram-layout.mjs` (Dagre SVG) architecture in `arasz-home-page`.

---

## 2. KaTeX vs. MathJax for Node.js SSG

- **KaTeX**: Lightweight C-like parser in JS. Renders synchronously via `katex.renderToString()`. 10x–100x faster than MathJax in Node.js. Produces clean HTML + MathML markup.
- **MathJax (v3+)**: Heavier engine requiring DOM adapters (`liteAdaptor`/`jsdom`). Slower in Node.js. SVG output is self-contained but payload size per equation is 3x–5x larger than KaTeX HTML.

---

## 3. Data Model Extensions (`article.ts`)

```typescript
export type InlineContent =
  | { type: 'text'; text: string }
  | { type: 'bold'; text?: string; children?: InlineContent[] }
  | { type: 'italic'; text: string }
  | { type: 'code'; text: string }
  | { type: 'link'; text: string; href: string; title?: string }
  | { type: 'math-inline'; formula: string; html: string };

export interface MathBlock {
  type: 'math';
  formula: string;
  html: string;
  label?: string;
}

export type ContentBlock =
  | ParagraphBlock
  | HeadingBlock
  // ... other blocks ...
  | MathBlock;
```

---

## 4. Build-Time Converter (`build-articles.mjs`)

```javascript
import katex from 'katex';

function renderKaTeX(formula, displayMode = false) {
  try {
    return katex.renderToString(formula.trim(), {
      displayMode,
      throwOnError: false,
    });
  } catch (err) {
    console.warn(`    KaTeX rendering warning for "${formula}":`, err.message);
    return `<span class="katex-error">${formula}</span>`;
  }
}

// Block Math Parser
function parseMathBlock(lines, i) {
  const line = lines[i].trim();
  if (line === '$$') {
    const mathLines = [];
    let next = i + 1;
    while (next < lines.length && lines[next].trim() !== '$$') {
      mathLines.push(lines[next]);
      next++;
    }
    const formula = mathLines.join('\n');
    const html = renderKaTeX(formula, true);
    return {
      block: { type: 'math', formula, html },
      next: next < lines.length ? next + 1 : next,
    };
  }
  return null;
}
```

---

## 5. CSS & Web Font Asset Optimization

1. **Global CSS Import (`styles.css`)**:
   Add `@import 'katex/dist/katex.min.css';` to `frontend/src/styles.css`.
    - **Do NOT** place in component CSS (`article-renderer.css`) — KaTeX CSS (~25 KB raw) will **breach the 8 kB component style budget** (`anyComponentStyle` error limit in `angular.json`).
    - Adding it to global `styles.css` adds only ~6 KB gzipped to the global CSS bundle.
2. **Font Copying & On-Demand Fetching**:
    - `@angular/build:application` (esbuild) automatically copies referenced WOFF2 fonts to `dist/araszme/browser/media/`.
    - Browsers fetch KaTeX font files **only when an article uses those font characters**. Pages without math make **0 font HTTP requests**.

---

## 6. Security & DomSanitizer Isolation

- KaTeX has `trust: false` by default, blocking dangerous commands (`\href`, `\url`, `\htmlData`).
- `throwOnError: false` safely converts invalid syntax to escaped HTML error spans without crashing builds.
- Angular template `[innerHTML]` requires `DomSanitizer.bypassSecurityTrustHtml()`. Isolate this in a standalone `MathRendererComponent`:

```typescript
@Component({
  selector: 'app-math-renderer',
  standalone: true,
  template: `<span [innerHTML]="safeHtml()"></span>`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MathRendererComponent {
  private readonly sanitizer = inject(DomSanitizer);
  readonly html = input.required<string>();
  readonly safeHtml = computed(() => this.sanitizer.bypassSecurityTrustHtml(this.html()));
}
```
