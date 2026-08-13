# Diagram Rendering — Build-Time dagre SVG

## Overview

Diagrams (flowcharts, state machines) are specified as HTML comments in article markdown. The build script runs dagre layout at build time, generates self-contained SVG strings, and embeds them in `.data.ts` files. The Angular component
injects SVG via `DomSanitizer.bypassSecurityTrustHtml()`.

**No client-side diagram JS is shipped.** The SVG is static HTML.

## Architecture

```
MD comments → build-articles.mjs parseBlocks()
  → diagram-layout.mjs layoutDiagram() (dagre)
  → diagram-layout.mjs renderDiagramSvg() (SVG string)
  → DiagramBlock with svg field in .data.ts
  → DiagramRendererComponent [innerHTML]="safeSvg"
```

## Markdown Syntax

```markdown
<!-- DIAGRAM: auth-flow -->
<!-- kind: flowchart -->
<!-- title: Authentication Flow -->
<!-- description: How tokens flow through the system -->
<!-- node: start:Start -->
<!-- node: validate:Validate Token -->
<!-- node: access:Access Resource -->
<!-- edge: start->validate -->
<!-- edge: validate->access:valid -->
<!-- edge: validate->reject:invalid -->
```

### Syntax Rules

- `<!-- DIAGRAM: {id} -->` — required, unique identifier
- `<!-- kind: flowchart | state-machine -->` — required
- `<!-- title: {text} -->` — required
- `<!-- description: {text} -->` — optional footnote
- `<!-- node: {id}:{label} -->` — required (1+). Label defaults to id if omitted
- `<!-- edge: {from}->{to} -->` — required (1+)
- `<!-- edge: {from}->{to}:{label} -->` — optional edge label
- Node/edge ids: `[a-zA-Z0-9_-]+`
- Lines after non-`<!--` line stop diagram parsing

## dagre Configuration

```javascript
import dagre from '@dagrejs/dagre';  // v3+, NOT 'dagre' (archived)

const g = new dagre.graphlib.Graph();
g.setGraph({
  rankdir: 'TB',        // top-to-bottom
  marginx: 24,
  marginy: 24,
  nodesep: 60,          // horizontal separation
  ranksep: 80,          // vertical separation
  edgesep: 20,
  acyclicer: 'greedy',  // handle cycles
  ranker: 'network-simplex',
});
g.setDefaultEdgeLabel(() => ({}));

// Node sizing: max(120, label.length * 9 + 40)
// 9px per char ≈ monospace at 13px, 40px padding
```

## SVG Theme Tokens (Monochrome)

| Token         | Hex       | Usage                  |
|---------------|-----------|------------------------|
| surface0      | `#0b0b0c` | SVG background         |
| surface1      | `#151517` | Node fill              |
| borderDefault | `#3a3a3f` | Edge stroke            |
| borderStrong  | `#6b6b70` | Node stroke, arrowhead |
| textPrimary   | `#f4f4f5` | Node labels            |
| textMuted     | `#838388` | Edge labels            |

Font: `JetBrains Mono, monospace`

## Edge Path Rendering

dagre provides edge waypoints in `g.edge(e).points`. Render smooth cubic Bézier curves.

**CRITICAL: Use dagre's ORIGINAL point order. Do NOT sort by y.** For cycles/back-edges, points go bottom-to-top. Sorting breaks cycle rendering.

```javascript
function edgePath(points) {
  if (points.length < 2) return '';
  let d = `M ${points[0].x} ${points[0].y}`;
  if (points.length === 2) {
    const mx = (points[0].x + points[1].x) / 2;
    d += ` C ${mx} ${points[0].y}, ${mx} ${points[1].y}, ${points[1].x} ${points[1].y}`;
  } else {
    for (let i = 1; i < points.length; i++) {
      const prev = points[i - 1], curr = points[i];
      d += ` C ${prev.x} ${prev.y + (curr.y - prev.y) / 3}, ${curr.x} ${curr.y - (curr.y - prev.y) / 3}, ${curr.x} ${curr.y}`;
    }
  }
  return d;
}
```

## Node Rendering

- **Flowchart**: `rx=2` (slightly rounded, almost rectangular)
- **State machine**: `rx=6` (rounded rectangles)

## Angular Component Pattern

```typescript
@Component({
  selector: 'app-diagram-renderer',
  templateUrl: './diagram-renderer.html',
  styleUrls: ['./diagram-renderer.css'],  // MUST be styleUrls (array), not styleUrl
})
export class DiagramRendererComponent {
  readonly diagram = input.required<DiagramBlock>();
  private readonly sanitizer = inject(DomSanitizer);

  get safeSvg(): SafeHtml {
    return this.sanitizer.bypassSecurityTrustHtml(this.diagram().svg);
  }
}
```

Template uses `[innerHTML]="safeSvg"` on a wrapper section.

## TypeScript Code Generation

The `tsBlock()` function in build-articles.mjs generates:

```typescript
diagram("auth-flow", "flowchart", "Auth Flow", [
  { id: "start", label: "Start" },
  { id: "validate", label: "Validate Token" },
], [
  { from: "start", to: "validate" },
  { from: "validate", to: "access", label: "valid" },
], '<svg>...</svg>', { description: "Standard flow" }),
```

## Files

| File                                                   | Purpose                                   |
|--------------------------------------------------------|-------------------------------------------|
| `scripts/diagram-layout.mjs`                           | dagre layout + SVG rendering (build-time) |
| `scripts/tests/test-diagram-layout.mjs`                | 43 tests for layout engine                |
| `components/diagram-renderer/diagram-renderer.ts`      | Angular component                         |
| `components/diagram-renderer/diagram-renderer.html`    | Template                                  |
| `components/diagram-renderer/diagram-renderer.css`     | Styles (monochrome)                       |
| `components/diagram-renderer/diagram-renderer.spec.ts` | 8 component tests                         |

## TDD Phases (for adding diagram support)

1. **Layout engine tests** → `test-diagram-layout.mjs` (parseDiagramComments, layoutDiagram, renderDiagramSvg, edgePath, escapeXml)
2. **Build script tests** → add to `test-build-articles.mjs` (DIAGRAM parsing, validation, tsBlock generation)
3. **TypeScript types** → DiagramBlock, DiagramNode, DiagramEdge, DiagramKind + diagram () helper
4. **Angular component tests** → diagram-renderer.spec.ts (create, title, badge, SVG, description)
5. **Verify** → build-articles → test:single → build
