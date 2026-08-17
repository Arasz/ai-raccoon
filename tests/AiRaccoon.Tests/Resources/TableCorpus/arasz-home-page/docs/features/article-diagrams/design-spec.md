# Article Diagram Rendering — Design Specification

> **Status:** Proposed  
> **Created:** 2026-07-24  
> **Scope:** Add dagre-based diagram rendering to the article pipeline  
> **Approach:** Build-time SVG generation (no client-side diagram JS)

---

## 1. Problem Statement

The article system needs to render directed graphs — flowcharts and state machine diagrams. The existing mermaid approach was abandoned (Kroki API issues, client-side CommonJS warnings). We need a solution that:

- Runs entirely at build time (no network calls, no client-side diagram library)
- Produces self-contained SVG embedded in `.data.ts` files
- Follows the same HTML-comment → build script → TypeScript block pattern used by charts
- Uses the monochrome dark theme (JetBrains Mono, design tokens from `chart-renderer.ts`)

## 2. Markdown Data Format

Diagrams are specified as HTML comments in the article markdown, exactly like charts. The build script (`build-articles.mjs`) parses them into structured data, computes layout via dagre, renders SVG, and emits a `DiagramBlock` with the SVG inlined.

### 2.1 Flowchart (Directed Graph)

```markdown
<!-- DIAGRAM: auth-flow -->
<!-- kind: flowchart -->
<!-- title: Authentication Flow -->
<!-- description: How tokens flow through the system -->
<!-- node: start:Start -->
<!-- node: validate:Validate Token -->
<!-- node: refresh:Refresh Token -->
<!-- node: access:Access Resource -->
<!-- node: reject:Reject -->
<!-- edge: start->validate -->
<!-- edge: validate->access:valid -->
<!-- edge: validate->refresh:expired -->
<!-- edge: refresh->validate:retry -->
<!-- edge: validate->reject:invalid -->
<!-- edge: refresh->reject:max retries -->
```

### 2.2 State Machine

```markdown
<!-- DIAGRAM: connection-states -->
<!-- kind: state-machine -->
<!-- title: Connection State Machine -->
<!-- node: idle:Idle -->
<!-- node: connecting:Connecting -->
<!-- node: connected:Connected -->
<!-- node: reconnecting:Reconnecting -->
<!-- node: closed:Closed -->
<!-- edge: idle->connecting:connect() -->
<!-- edge: connecting->connected:success -->
<!-- edge: connecting->closed:failure -->
<!-- edge: connected->reconnecting:disconnect -->
<!-- edge: reconnecting->connected:success -->
<!-- edge: reconnecting->closed:timeout -->
<!-- edge: closed->idle:reset() -->
```

### 2.3 Comment Syntax Reference

| Comment | Required | Description |
|---------|----------|-------------|
| `<!-- DIAGRAM: {id} -->` | Yes | Unique identifier for the diagram |
| `<!-- kind: flowchart \| state-machine -->` | Yes | Diagram type (affects layout + rendering) |
| `<!-- title: {text} -->` | Yes | Human-readable title |
| `<!-- description: {text} -->` | No | Footnote text below the diagram |
| `<!-- node: {id}:{label} -->` | Yes (1+) | Node definition. `id` is unique within diagram, `label` is display text |
| `<!-- edge: {from}->{to} -->` | Yes (1+) | Edge definition. `from`/`to` reference node ids |
| `<!-- edge: {from}->{to}:{label} -->` | No | Edge with label text displayed along the edge |

**Parsing rules:**
- Lines between `<!-- DIAGRAM: ... -->` and the next non-`<!--` line are consumed as diagram metadata
- `<!-- node: ... -->` and `<!-- edge: ... -->` lines can appear in any order after the header lines
- Node/edge ids must match `[a-zA-Z0-9_-]+`
- Labels can contain any characters after the `:` separator
- Blank `<!-- -->` lines within a diagram block are skipped

### 2.4 Real-World Example

An article about the article pipeline itself could include:

```markdown
## Build Pipeline

The article build pipeline processes markdown files through several stages:

<!-- DIAGRAM: article-pipeline -->
<!-- kind: flowchart -->
<!-- title: Article Build Pipeline -->
<!-- description: How markdown becomes rendered HTML -->
<!-- node: md:Markdown File -->
<!-- node: fm:Parse Frontmatter -->
<!-- node: blocks:Parse Blocks -->
<!-- node: sections:Group Sections -->
<!-- node: dagre:dagre Layout -->
<!-- node: svg:Render SVG -->
<!-- node: ts:Generate .data.ts -->
<!-- node: angular:Angular Renderer -->
<!-- edge: md->fm -->
<!-- edge: fm->blocks -->
<!-- edge: blocks->sections -->
<!-- edge: blocks->dagre:diagram blocks -->
<!-- edge: dagre->svg -->
<!-- edge: svg->ts:embed SVG string -->
<!-- edge: sections->ts -->
<!-- edge: ts->angular -->
```

## 3. DiagramBlock Type Definition

Add to `frontend/src/app/models/article.ts`:

```typescript
// ─── Diagram types ─────────────────────────────────────────────

export type DiagramKind = 'flowchart' | 'state-machine';

export interface DiagramNode {
  /** Unique identifier within the diagram (e.g. 'validate'). */
  id: string;
  /** Display label (e.g. 'Validate Token'). */
  label: string;
}

export interface DiagramEdge {
  /** Source node id. */
  from: string;
  /** Target node id. */
  to: string;
  /** Optional label displayed along the edge. */
  label?: string;
}

export interface DiagramBlock {
  type: 'diagram';
  /** Unique diagram identifier, e.g. 'auth-flow'. */
  id: string;
  /** Diagram kind (determines layout + rendering). */
  kind: DiagramKind;
  /** Human-readable title displayed above the diagram. */
  title: string;
  /** Graph nodes. */
  nodes: DiagramNode[];
  /** Graph edges (directed). */
  edges: DiagramEdge[];
  /** Description shown below the diagram (footnote). */
  description?: string;
  /**
   * Pre-rendered SVG string (inline, self-contained).
   * Generated at build time by dagre + SVG renderer in build-articles.mjs.
   */
  svg: string;
}
```

**Update ContentBlock union** (in `article.ts`):

```typescript
export type ContentBlock =
  | ParagraphBlock
  | HeadingBlock
  | TableBlock
  | ListBlock
  | CalloutBlock
  | HorizontalRuleBlock
  | CodeBlock
  | SeparatorBlock
  | ChartBlock
  | DiagramBlock          // ← new
  | ProseWithChartBlock;
```

**Add helper function** (in `article.ts`):

```typescript
/** Create a diagram block. */
export function diagram(
  id: string,
  kind: DiagramKind,
  title: string,
  nodes: DiagramNode[],
  edges: DiagramEdge[],
  svg: string,
  opts?: Partial<Pick<DiagramBlock, 'description'>>,
): DiagramBlock {
  return {type: 'diagram', id, kind, title, nodes, edges, svg, ...opts};
}
```

### 3.1 Design Decisions

**Why `svg: string` instead of rendering at runtime?**
- dagre is a layout engine only — it computes coordinates but doesn't render. We'd still need custom SVG rendering code.
- Pre-rendering at build time means zero JS shipped to the client for diagrams.
- The SVG is self-contained (inline styles, no external deps) so it works as static HTML.
- Pattern is analogous to how SSR works: compute once, serve as static markup.

**Why separate `DiagramNode` and `DiagramEdge` instead of a single adjacency list?**
- Mirrors the markdown authoring format (nodes and edges are separate comment lines).
- Allows future per-node styling (shape, color overrides) without changing the edge format.
- Enables tests to verify node/edge structure independently of SVG output.

## 4. dagre Layout Engine Integration

### 4.1 New File: `frontend/scripts/diagram-layout.mjs`

This module runs at build time only (imported by `build-articles.mjs`). It uses dagre for layout and generates SVG strings.

```
frontend/scripts/diagram-layout.mjs
├── parseDiagramComments(lines, startIndex) → { block, endIndex }
├── layoutDiagram(nodes, edges, kind) → dagre graph with positions
└── renderDiagramSvg(dagreGraph, kind, title, id) → SVG string
```

### 4.2 dagre Configuration

```javascript
import dagre from '@dagrejs/dagre';

function layoutDiagram(nodes, edges, kind) {
  const g = new dagre.graphlib.Graph();
  g.setGraph({
    rankdir: 'TB',        // top-to-bottom for flowcharts
    marginx: 24,
    marginy: 24,
    nodesep: 60,          // horizontal separation between nodes
    ranksep: 80,          // vertical separation between ranks
    edgesep: 20,          // edge separation
    acyclicer: 'greedy',  // handle cycles
    ranker: 'network-simplex',
  });

  g.setDefaultEdgeLabel(() => ({}));

  // Add nodes with size hints
  for (const node of nodes) {
    const width = Math.max(120, node.label.length * 9 + 40);
    const height = 40;
    g.setNode(node.id, { label: node.label, width, height });
  }

  // Add edges
  for (const edge of edges) {
    g.setEdge(edge.from, edge.to, { label: edge.label || '' });
  }

  dagre.layout(g);
  return g;
}
```

**Node sizing heuristic:** `max(120, label.length * 9 + 40)` — monospace font at ~13px ≈ 9px per character, plus 40px padding. Minimum 120px for short labels. dagre will expand ranks as needed.

### 4.3 Build Script Integration

In `build-articles.mjs`, the `parseBlocks()` function gets a new branch after the chart comment parser (line ~208):

```javascript
// Diagram comment
const diagramMatch = stripped.match(/<!--\s*DIAGRAM:\s*(.+?)\s*-->/);
if (diagramMatch) {
  const diagramBlock = {
    type: 'diagram',
    id: diagramMatch[1].trim(),
    kind: 'flowchart',
    title: diagramMatch[1].trim(),
    nodes: [],
    edges: [],
    description: '',
    svg: '',
  };
  i++;
  // Parse subsequent HTML comment lines for diagram metadata
  while (i < lines.length) {
    const metaLine = lines[i].trim();
    const kindMatch = metaLine.match(/<!--\s*kind:\s*(.+?)\s*-->/);
    const titleMatch = metaLine.match(/<!--\s*title:\s*(.+?)\s*-->/);
    const descMatch = metaLine.match(/<!--\s*description:\s*(.+?)\s*-->/);
    const nodeMatch = metaLine.match(/<!--\s*node:\s*(.+?)\s*-->/);
    const edgeMatch = metaLine.match(/<!--\s*edge:\s*(.+?)\s*-->/);

    if (kindMatch) { diagramBlock.kind = kindMatch[1].trim(); i++; continue; }
    if (titleMatch) { diagramBlock.title = titleMatch[1].trim(); i++; continue; }
    if (descMatch) { diagramBlock.description = descMatch[1].trim(); i++; continue; }
    if (nodeMatch) {
      const parts = nodeMatch[1].split(':');
      diagramBlock.nodes.push({
        id: parts[0].trim(),
        label: (parts[1] || parts[0]).trim(),
      });
      i++; continue;
    }
    if (edgeMatch) {
      const raw = edgeMatch[1].trim();
      const arrowIdx = raw.indexOf('->');
      if (arrowIdx === -1) { i++; continue; }
      const from = raw.slice(0, arrowIdx).trim();
      const rest = raw.slice(arrowIdx + 2);
      const colonIdx = rest.indexOf(':');
      const to = colonIdx === -1 ? rest.trim() : rest.slice(0, colonIdx).trim();
      const label = colonIdx === -1 ? '' : rest.slice(colonIdx + 1).trim();
      diagramBlock.edges.push({ from, to, label: label || undefined });
      i++; continue;
    }
    break; // no more diagram metadata lines
  }

  // ── Validation ──
  const VALID_KINDS = ['flowchart', 'state-machine'];
  if (!VALID_KINDS.includes(diagramBlock.kind)) {
    console.warn(`    Invalid diagram kind "${diagramBlock.kind}" in ${diagramBlock.id}, defaulting to "flowchart"`);
    diagramBlock.kind = 'flowchart';
  }

  const nodeIds = new Set(diagramBlock.nodes.map(n => n.id));
  for (const n of diagramBlock.nodes) {
    if (nodeIds.has(n.id) && diagramBlock.nodes.filter(x => x.id === n.id).length > 1) {
      console.warn(`    Duplicate node id "${n.id}" in diagram ${diagramBlock.id}`);
    }
  }
  for (const e of diagramBlock.edges) {
    if (!nodeIds.has(e.from)) console.warn(`    Edge references undefined node "${e.from}" in diagram ${diagramBlock.id}`);
    if (!nodeIds.has(e.to)) console.warn(`    Edge references undefined node "${e.to}" in diagram ${diagramBlock.id}`);
  }

  // Run dagre layout + SVG rendering at build time
  const { layoutDiagram, renderDiagramSvg } = await import('./diagram-layout.mjs');
  const g = layoutDiagram(diagramBlock.nodes, diagramBlock.edges, diagramBlock.kind);
  diagramBlock.svg = renderDiagramSvg(g, diagramBlock.kind, diagramBlock.title, diagramBlock.id);

  blocks.push(diagramBlock);
  continue;
}
```

### 4.4 TypeScript Code Generation

Add to `tsBlock()` in `build-articles.mjs`:

```javascript
case 'diagram': {
  const nodesStr = block.nodes.map(n =>
    `{ id: ${tsEscape(n.id)}, label: ${tsEscape(n.label)} }`
  ).join(', ');
  const edgesStr = block.edges.map(e => {
    const parts = [`from: ${tsEscape(e.from)}`, `to: ${tsEscape(e.to)}`];
    if (e.label) parts.push(`label: ${tsEscape(e.label)}`);
    return `{ ${parts.join(', ')} }`;
  }).join(', ');
  const opts = [];
  if (block.description) opts.push(`description: ${tsEscape(block.description)}`);
  const optStr = opts.length ? `, { ${opts.join(', ')} }` : '';
  return `${p}diagram(${tsEscape(block.id)}, ${tsEscape(block.kind)}, ${tsEscape(block.title)}, [${nodesStr}], [${edgesStr}], ${tsEscape(block.svg)}${optStr}),`;
}
```

Update `allHelpers` array in `convertMdToArticle()` to include `'diagram'`.

## 5. SVG Renderer

### 5.1 Design Approach

dagre computes node positions (`x`, `y` as center coordinates) and edge paths (arrays of `{x, y}` waypoints). We transform these into SVG elements using:

- **Nodes:** `<rect>` with `<text>` overlay (centered)
- **Edges:** `<path>` with cubic Bézier curves through dagre waypoints
- **Edge labels:** `<text>` positioned at the midpoint of the edge path
- **Arrowheads:** SVG `<marker>` definition in `<defs>`

### 5.2 SVG Structure

```xml
<svg xmlns="http://www.w3.org/2000/svg"
     viewBox="0 0 {width} {height}"
     role="img"
     aria-labelledby="{id}-title"
     style="max-width: 100%; height: auto;">

  <title id="{id}-title">{title}</title>

  <defs>
    <marker id="arrowhead-{id}" viewBox="0 0 10 10"
            refX="10" refY="5" markerWidth="8" markerHeight="8"
            orient="auto-start-reverse">
      <path d="M 0 0 L 10 5 L 0 10 z" fill="#6b6b70"/>
    </marker>
  </defs>

  <rect width="100%" height="100%" fill="#0b0b0c" rx="6"/>

  <!-- Edges -->
  <g class="edges">
    <path d="M ... C ..." stroke="#3a3a3f" stroke-width="1.5"
          fill="none" marker-end="url(#arrowhead-{id})"/>
    <text x="..." y="..." fill="#838388"
          font-family="JetBrains Mono, monospace" font-size="11"
          text-anchor="middle">{edge-label}</text>
  </g>

  <!-- Nodes -->
  <g class="nodes">
    <rect x="..." y="..." width="..." height="..."
          rx="{0|6}" fill="#151517" stroke="#6b6b70" stroke-width="1.5"/>
    <text x="..." y="..." fill="#f4f4f5"
          font-family="JetBrains Mono, monospace" font-size="13"
          text-anchor="middle" dominant-baseline="central">{label}</text>
  </g>
</svg>
```

### 5.3 Theme Tokens

All colors are inlined — no CSS variables or external styles. Tokens match `chart-renderer.ts`:

| Token | Hex | Usage |
|-------|-----|-------|
| `surface0` | `#0b0b0c` | SVG background |
| `surface1` | `#151517` | Node fill |
| `borderSubtle` | `#26262a` | (not used — nodes need more contrast) |
| `borderDefault` | `#3a3a3f` | Edge stroke |
| `borderStrong` | `#6b6b70` | Node stroke, arrowhead fill |
| `textPrimary` | `#f4f4f5` | Node labels |
| `textSecondary` | `#b4b4b8` | (available for subtitles) |
| `textMuted` | `#838388` | Edge labels |

### 5.4 Edge Path Rendering

dagre provides edge waypoints in `g.edge(e).points`. We render smooth curves:

```javascript
function edgePath(points) {
  if (points.length < 2) return '';

  // Use dagre's points in their original order (source→target traversal).
  // Do NOT sort by y — dagre returns correct traversal order including
  // back-edges for cycles. Sorting would break cycle rendering.
  let d = `M ${points[0].x} ${points[0].y}`;

  if (points.length === 2) {
    const mx = (points[0].x + points[1].x) / 2;
    d += ` C ${mx} ${points[0].y}, ${mx} ${points[1].y}, ${points[1].x} ${points[1].y}`;
  } else {
    for (let i = 1; i < points.length; i++) {
      const prev = points[i - 1];
      const curr = points[i];
      const cpx1 = prev.x;
      const cpy1 = prev.y + (curr.y - prev.y) / 3;
      const cpx2 = curr.x;
      const cpy2 = curr.y - (curr.y - prev.y) / 3;
      d += ` C ${cpx1} ${cpy1}, ${cpx2} ${cpy2}, ${curr.x} ${curr.y}`;
    }
  }

  return d;
}
```

### 5.5 Node Rendering

```javascript
function renderNode(node, dagreNode, kind) {
  const x = dagreNode.x - dagreNode.width / 2;
  const y = dagreNode.y - dagreNode.height / 2;
  const rx = kind === 'state-machine' ? 6 : 2;

  return `
    <rect x="${x}" y="${y}" width="${dagreNode.width}" height="${dagreNode.height}"
          rx="${rx}" fill="#151517" stroke="#6b6b70" stroke-width="1.5"/>
    <text x="${dagreNode.x}" y="${dagreNode.y}" fill="#f4f4f5"
          font-family="JetBrains Mono, monospace" font-size="13"
          text-anchor="middle" dominant-baseline="central">${escapeXml(node.label)}</text>`;
}
```

**State machine difference:** `rx=6` (rounded rectangles) vs flowchart `rx=2` (slightly rounded, almost rectangular).

### 5.6 Full SVG Assembly

```javascript
function renderDiagramSvg(g, kind, title, id) {
  const graph = g.graph();
  const padding = 24;
  const width = graph.width + padding * 2;
  const height = graph.height + padding * 2;

  const parts = [];
  parts.push(`<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="${escapeXml(id)}-title" style="max-width:100%;height:auto;">`);
  parts.push(`<title id="${escapeXml(id)}-title">${escapeXml(title)}</title>`);

  // Defs (arrowhead)
  parts.push(`<defs><marker id="arrowhead-${id}" viewBox="0 0 10 10" refX="10" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#6b6b70"/></marker></defs>`);

  // Background
  parts.push(`<rect width="100%" height="100%" fill="#0b0b0c" rx="6"/>`);

  // Edges
  parts.push('<g class="edges">');
  g.edges().forEach(e => {
    const edge = g.edge(e);
    const points = edge.points.map(p => ({ x: p.x + padding, y: p.y + padding }));
    const d = edgePath(points);
    parts.push(`<path d="${d}" stroke="#3a3a3f" stroke-width="1.5" fill="none" marker-end="url(#arrowhead-${id})"/>`);

    if (edge.label) {
      const mid = points[Math.floor(points.length / 2)];
      parts.push(`<text x="${mid.x}" y="${mid.y - 8}" fill="#838388" font-family="JetBrains Mono, monospace" font-size="11" text-anchor="middle">${escapeXml(edge.label)}</text>`);
    }
  });
  parts.push('</g>');

  // Nodes
  parts.push('<g class="nodes">');
  g.nodes().forEach(v => {
    const node = g.node(v);
    const x = node.x - node.width / 2 + padding;
    const y = node.y - node.height / 2 + padding;
    const rx = kind === 'state-machine' ? 6 : 2;
    parts.push(`<rect x="${x}" y="${y}" width="${node.width}" height="${node.height}" rx="${rx}" fill="#151517" stroke="#6b6b70" stroke-width="1.5"/>`);
    parts.push(`<text x="${node.x + padding}" y="${node.y + padding}" fill="#f4f4f5" font-family="JetBrains Mono, monospace" font-size="13" text-anchor="middle" dominant-baseline="central">${escapeXml(node.label)}</text>`);
  });
  parts.push('</g>');

  parts.push('</svg>');
  return parts.join('\n');
}
```

### 5.7 XML Escaping

```javascript
function escapeXml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}
```

## 6. Article Renderer Integration

### 6.1 New Component: DiagramRendererComponent

**Location:** `frontend/src/app/components/diagram-renderer/`

**Files:**
- `diagram-renderer.ts` — component class
- `diagram-renderer.html` — template
- `diagram-renderer.css` — styles
- `diagram-renderer.spec.ts` — unit tests

Since the SVG is pre-rendered at build time, the component is simpler than `ChartRendererComponent` — it just injects the SVG string and wraps it with a title/description.

**Component:**

```typescript
import { Component, inject, input } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { DiagramBlock } from '../../models/article';

@Component({
  selector: 'app-diagram-renderer',
  templateUrl: './diagram-renderer.html',
  styleUrls: ['./diagram-renderer.css'],
})
export class DiagramRendererComponent {
  readonly diagram = input.required<DiagramBlock>();
  private readonly sanitizer = inject(DomSanitizer);

  get safeSvg(): SafeHtml {
    return this.sanitizer.bypassSecurityTrustHtml(this.diagram().svg);
  }
}
```

**Template:**

```html
<article class="diagram-card">
  <header class="diagram-header">
    <h3 [id]="diagram().id + '-title'" class="diagram-title">{{ diagram().title }}</h3>
    <span class="diagram-kind-badge">{{ diagram().kind }}</span>
  </header>

  <section class="diagram-svg-wrap" [innerHTML]="safeSvg"></section>

  @if (diagram().description) {
    <p class="diagram-description">{{ diagram().description }}</p>
  }
</article>
```

**Styles (mirroring chart-renderer.css):**

```css
.diagram-card {
  border: 1px solid var(--border-subtle);
  border-radius: var(--radius-md);
  padding: var(--space-4);
  margin: var(--space-4) 0;
  background: var(--surface-1);
}

.diagram-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: var(--space-3);
}

.diagram-title {
  font-size: var(--text-sm-size);
  font-weight: var(--text-sm-weight);
  color: var(--text-primary);
  margin: 0;
}

.diagram-kind-badge {
  font-size: var(--text-xs-size);
  color: var(--text-muted);
  border: 1px solid var(--border-subtle);
  border-radius: var(--radius-sm);
  padding: 2px 6px;
}

.diagram-svg-wrap {
  max-width: 100%;
  margin-bottom: var(--space-3);
  overflow-x: auto;
}

.diagram-svg-wrap svg {
  display: block;
  width: 100%;
  height: auto;
}

.diagram-description {
  font-size: var(--text-xs-size);
  color: var(--text-secondary);
  margin: 0;
  line-height: var(--text-xs-line);
}
```

### 6.2 Article Renderer Changes

**`article-renderer.ts`** — add import and to `imports` array:

```typescript
import {DiagramRendererComponent} from '../diagram-renderer/diagram-renderer';
// ...
imports: [NgTemplateOutlet, DatePipe, UpperCasePipe, ChartRendererComponent, DiagramRendererComponent],
```

**`article-renderer.html`** — add case in the `@switch (block.type)` block:

```html
@case ('diagram') {
  <div class="block-diagram">
    <app-diagram-renderer [diagram]="block"></app-diagram-renderer>
  </div>
}
```

### 6.3 Security Consideration

Using `[innerHTML]` with the SVG string is safe because:
1. The SVG is generated entirely by our build script from structured data (not user input)
2. The build script runs in CI/locally — the SVG never comes from external sources
3. We control the `escapeXml()` function that sanitizes all label text

## 7. Testing Strategy

### 7.1 Unit Tests — Build Script (Node.js `node:test`)

**File:** `frontend/scripts/tests/test-build-articles.mjs` (add sections)

```
describe('parseBlocks — diagram')
  it('parses DIAGRAM comment into diagram block with correct id')
  it('parses kind: flowchart')
  it('parses kind: state-machine')
  it('parses title and description')
  it('parses multiple nodes from node: comments')
  it('parses nodes without labels (uses id as label)')
  it('parses multiple edges from edge: comments')
  it('parses edges with labels')
  it('parses edges without labels')
  it('handles node/edge order independence')
  it('stops parsing at non-comment line')
  it('generates valid TypeScript with diagram() call')
  it('includes SVG string in generated TypeScript')
  it('handles empty diagram gracefully (no nodes/edges)')
```

### 7.2 Unit Tests — Layout Engine (Node.js `node:test`)

**File:** `frontend/scripts/tests/test-diagram-layout.mjs` (new)

```
describe('layoutDiagram')
  it('returns dagre graph with node positions')
  it('positions all nodes with x, y coordinates')
  it('computes edge points for all edges')
  it('handles single-node graph')
  it('handles linear chain (A -> B -> C)')
  it('handles branching (A -> B, A -> C)')
  it('handles cycles (A -> B -> C -> A)')
  it('handles disconnected components')
  it('sizes nodes proportionally to label length')
  it('applies minimum node width')

describe('renderDiagramSvg')
  it('returns string starting with <svg')
  it('contains role="img" attribute')
  it('contains aria-labelledby pointing to title')
  it('contains <title> element with diagram title')
  it('contains arrowhead marker in <defs>')
  it('contains <rect> for each node')
  it('contains <text> for each node label')
  it('contains <path> for each edge')
  it('contains edge labels as <text> when present')
  it('uses monochrome theme colors')
  it('uses JetBrains Mono font')
  it('applies rx=2 for flowchart nodes')
  it('applies rx=6 for state-machine nodes')
  it('produces valid SVG (parsable by DOMParser)')
  it('escapes XML special characters in labels')
  it('handles empty graph (no nodes, no edges)')
```

### 7.3 Unit Tests — Angular Component (Vitest/Karma)

**File:** `frontend/src/app/components/diagram-renderer/diagram-renderer.spec.ts`

```
describe('DiagramRendererComponent')
  it('should create')
  it('renders title in h3')
  it('renders kind badge')
  it('renders SVG via innerHTML')
  it('shows description when provided')
  it('hides description when not provided')
  it('sets aria-labelledby on SVG')
  it('applies diagram-card CSS class')
```

### 7.4 E2E Tests (Playwright)

**File:** `frontend/e2e/article-diagrams.spec.ts`

```
test.describe('Article Diagrams — Critical Journey')
  test('renders diagram SVG on article page')
    - Navigate to article with diagram
    - Assert app-diagram-renderer is visible
    - Assert SVG element exists inside diagram
    - Assert SVG has non-zero dimensions

  test('diagram shows title and kind badge')
    - Assert .diagram-title text matches expected title
    - Assert .diagram-kind-badge shows 'flowchart' or 'state-machine'

  test('diagram SVG contains node text')
    - Assert SVG <text> elements contain expected node labels

  test('diagram SVG contains edge paths')
    - Assert SVG <path> elements with marker-end exist

  test('diagram is responsive (max-width: 100%)')
    - Assert SVG style attribute contains max-width:100%

  test('diagram with description shows footnote')
    - Assert .diagram-description is visible with correct text
```

### 7.5 TDD Workflow

1. **RED:** Write `test-diagram-layout.mjs` tests for `layoutDiagram()` and `renderDiagramSvg()` — tests fail (module doesn't exist)
2. **GREEN:** Implement `diagram-layout.mjs` to pass all layout + SVG tests
3. **REFACTOR:** Clean up SVG rendering, optimize path generation
4. **RED:** Write `parseBlocks` diagram tests in `test-build-articles.mjs` — tests fail (parsing not implemented)
5. **GREEN:** Add diagram parsing to `build-articles.mjs`
6. **RED:** Write `diagram-renderer.spec.ts` — tests fail (component doesn't exist)
7. **GREEN:** Implement `DiagramRendererComponent`
8. **RED:** Write `article-diagrams.spec.ts` E2E tests — tests fail (no article with diagram)
9. **GREEN:** Add a test diagram to an existing article, run full pipeline
10. **VERIFY:** `npm run build-articles && npm run test && npm run e2e`

## 8. Files to Create / Modify

### New Files

| File | Purpose |
|------|---------|
| `frontend/scripts/diagram-layout.mjs` | dagre layout + SVG rendering (build-time only) |
| `frontend/scripts/tests/test-diagram-layout.mjs` | Unit tests for layout engine |
| `frontend/src/app/components/diagram-renderer/diagram-renderer.ts` | Angular component |
| `frontend/src/app/components/diagram-renderer/diagram-renderer.html` | Template |
| `frontend/src/app/components/diagram-renderer/diagram-renderer.css` | Styles |
| `frontend/src/app/components/diagram-renderer/diagram-renderer.spec.ts` | Component tests |
| `frontend/e2e/article-diagrams.spec.ts` | E2E tests |

### Modified Files

| File | Changes |
|------|---------|
| `frontend/package.json` | Add `dagre` dependency |
| `frontend/src/app/models/article.ts` | Add `DiagramBlock`, `DiagramNode`, `DiagramEdge`, `DiagramKind` types; add `diagram()` helper; update `ContentBlock` union |
| `frontend/scripts/build-articles.mjs` | Add diagram comment parsing in `parseBlocks()`; add `diagram` case in `tsBlock()`; add `diagram` to `allHelpers` |
| `frontend/src/app/components/article-renderer/article-renderer.ts` | Import `DiagramRendererComponent`; add to `imports` |
| `frontend/src/app/components/article-renderer/article-renderer.html` | Add `@case ('diagram')` block |
| `content/articles/ARTICLE-PIPELINE.md` | Update docs with diagram syntax |

### Optional (for demo/E2E)

| File | Changes |
|------|---------|
| `content/articles/*.md` | Add a `<!-- DIAGRAM: ... -->` block to an existing article for E2E testing |

## 9. Dependencies

```json
{
  "devDependencies": {
    "@dagrejs/dagre": "^1.1.4"
  }
}
```

- `@dagrejs/dagre` is the actively maintained fork (original `dagre` is archived)
- Pure JavaScript, no native bindings, no network calls at runtime
- Used only in `build-articles.mjs` (Node.js build script) — never bundled into the Angular app
- Total size: ~60KB minified (irrelevant since it's build-time only)

## 10. Build Pipeline Flow

```
                    Markdown (content/articles/*.md)
                              │
                              ▼
                    ┌─────────────────────┐
                    │  build-articles.mjs  │
                    │                     │
                    │  parseFrontmatter() │
                    │  parseBlocks()      │
                    │    ├─ CHART → ChartBlock
                    │    ├─ DIAGRAM → DiagramBlock   ← NEW
                    │    │    ├─ parse nodes/edges   │
                    │    │    ├─ layoutDiagram(dagre) │
                    │    │    └─ renderDiagramSvg()   │
                    │    └─ ...                       │
                    │  groupIntoSections()│
                    │  tsBlock()          │
                    │    ├─ chart → chart(...)
                    │    ├─ diagram → diagram(...)  ← NEW
                    │    └─ ...                      │
                    └─────────────────────┘
                              │
                              ▼
                    TypeScript .data.ts
                    (SVG string embedded)
                              │
                              ▼
                    ┌─────────────────────┐
                    │  Angular Renderer    │
                    │                     │
                    │  ArticleRenderer    │
                    │    ├─ ChartRenderer │
                    │    ├─ DiagramRenderer ← NEW
                    │    │   └─ [innerHTML]="svg"
                    │    └─ ...           │
                    └─────────────────────┘
                              │
                              ▼
                    Static HTML with inline SVG
```

## 11. Example Generated TypeScript

After `build-articles.mjs` processes a markdown file with a diagram, the `.data.ts` output contains:

```typescript
import type {Article} from '../../models/article';
import {text, bold, paragraph, heading, diagram} from '../../models/article';

// ── Article ───────────────────────────────────────────────────────────

export const myArticle: Article = {
  meta: {
    // ...
  },

  sections: [
    {
      heading: "Architecture",
      id: "architecture",
      blocks: [
        paragraph(text("The authentication flow:")),
        diagram("auth-flow", "flowchart", "Authentication Flow", [
          { id: "start", label: "Start" },
          { id: "validate", label: "Validate Token" },
          { id: "access", label: "Access Resource" },
          { id: "reject", label: "Reject" },
        ], [
          { from: "start", to: "validate" },
          { from: "validate", to: "access", label: "valid" },
          { from: "validate", to: "reject", label: "invalid" },
        ], '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 300" role="img" aria-labelledby="auth-flow-title" style="max-width:100%;height:auto;">\n<title id="auth-flow-title">Authentication Flow</title>\n...', { description: "Standard token validation flow" }),
      ],
    },
  ],
};
```

## 12. Future Considerations

- **Subgraphs/clusters:** dagre supports compound nodes — could group related nodes visually
- **Custom node shapes:** diamonds for decision points, circles for start/end states
- **Direction options:** `rankdir: 'LR'` for wide diagrams via `<!-- direction: lr -->` comment
- **Theme variants:** if the site adds light mode, the SVG colors would need regeneration
- **Diagram caching:** skip SVG regeneration if markdown hasn't changed (incremental builds)
- **Accessibility:** the `<title>` and `aria-labelledby` provide screen reader support; could add `<desc>` for the description field
