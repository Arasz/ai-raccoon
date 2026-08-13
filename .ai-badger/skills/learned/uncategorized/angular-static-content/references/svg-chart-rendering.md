# SVG Chart Rendering in Angular (LEGACY — replaced by ApexCharts)

> **Note**: Custom inline SVG charts were replaced by ApexCharts in this project. See `references/apexcharts-integration.md` for the current approach. This file is retained for reference on SVG accessibility patterns and data visualization
> best practices.

Patterns for inline SVG charts in Angular components. No external chart library — pure SVG rendered via Angular templates.

## Component Structure

```
chart-renderer/
  chart-renderer.ts      — Component with computed signals for scaling, formatting
  chart-renderer.html    — SVG template with @switch for chart kinds
  chart-renderer.css     — Monochrome styles (no colored hex — theme-tokens test)
  chart-renderer.spec.ts — Unit tests
```

Selector: `app-chart-renderer`. Input: `ChartBlock` from `models/article.ts`.

## Chart Kinds

| Kind             | Use case                           | SVG pattern                   |
|------------------|------------------------------------|-------------------------------|
| `bar-horizontal` | Comparing values across categories | Horizontal bars, labels above |
| `bar-vertical`   | Time series, ranked comparisons    | Vertical bars, labels below   |
| `donut`          | Proportional distribution          | Arc segments with legend      |
| `scatter`        | Two-variable correlation           | Dots positioned by x/y        |
| `timeline`       | Duration/availability              | Filled track bars             |

## Bar Chart Scaling

### The wide-range problem

When values span >10x (e.g., $0.06 vs $4.00), linear scaling makes small bars invisible.

**Solution: auto-detect and switch to log scale:**

```typescript
protected readonly scaleFactor = computed(() => {
  const vals = this.chart().data.map(d => Math.abs(d.value)).filter(v => v > 0);
  if (!vals.length) return () => 0;
  const min = Math.min(...vals);
  const maxV = Math.max(...vals);
  if (maxV / min > 10) {
    // Log scale
    const logMin = Math.log10(min);
    const logMax = Math.log10(maxV);
    const logRange = logMax - logMin || 1;
    return (v: number) => (Math.log10(Math.max(v, 0.001)) - logMin) / logRange;
  }
  // Linear scale
  return (v: number) => v / (maxV || 1);
});
```

Use `scaleFactor()(p.value)` in template instead of `abs(p.value) / max()`.

### Label clipping

SVG viewBox starts at (0,0). Labels above bars at `y=-2` get clipped.

**Solution: add labelPad top padding:**

```typescript
protected readonly labelPad = 14;

protected readonly barChartHeight = computed(() => {
  const n = this.chart().data.length;
  return this.labelPad + n * (this.barHeight + this.barGap) + this.barGap;
});
```

In template: `[attr.transform]="'translate(0,' + (labelPad + i * (barHeight + barGap) + barGap) + ')'"`.

## Donut Chart

Use arc path generation with inner/outer radius. Center text for summary.

```typescript
protected readonly donutSize = 200;
protected readonly donutRadius = 70;
protected readonly donutInner = 42;
```

Path formula: `M x1 y1 A r r 0 largeArc 1 x2 y2 L ix1 iy1 A ir ir 0 largeArc 0 ix2 iy2 Z`.

## Monochrome Enforcement

The `theme-tokens.spec.ts` test scans ALL `.css` files for hex colors. Any color with RGB channel delta > 10 fails.

**Design token colors only:**

- Bars: `var(--text-muted)` (#838388)
- Labels: `var(--text-secondary)` (#b4b4b8)
- Values: `var(--text-primary)` (#f4f4f5)
- Background: `var(--surface-1)` (#151517)
- Borders: `var(--border-default)` (#3a3a3f)

**Don't use**: `#58a6ff`, `#3fb950`, `#f85149`, `#bc8cff` — these are colored and will fail the test.

## Angular Template Gotchas

1. **`Math` not available** — use a component method `abs(v)` instead of `Math.abs(v)`.
2. **Unicode escapes** — `\u2193` renders as literal text. Use the actual character `↓`.
3. **`@switch` on chart kind** — use Angular 17+ control flow (`@switch`, `@case`), not `[ngSwitch]`.
4. **Computed signals for scaling** — `scaleFactor()` returns a function. Call it as `scaleFactor()(p.value)` in template.

## Accessibility (W3C SVG Accessibility)

Every SVG chart needs full accessibility attributes:

```html
<svg
  role="img"
  [attr.aria-labelledby]="chart().id + '-title'"
  [attr.aria-roledescription]="'bar chart'"  <!-- or 'donut chart', 'timeline', etc. -->
  preserveAspectRatio="xMinYMin meet"
>
  <title [id]="chart().id + '-title'">{{ chart().title }}</title>
  @if (chart().description) {
    <desc>{{ chart().description }}</desc>
  }
```

Add `aria-label` on data elements:

```html
<rect [attr.aria-label]="p.label + ': ' + fmt(p.value)" />
<circle [attr.aria-label]="p.label + ': ' + fmt(p.value)" />
<path [attr.aria-label]="seg.label + ': ' + seg.pct + '%'" />
```

Add sr-only fallback table for screen readers:

```html
<table class="sr-only" [attr.aria-label]="chart().title + ' data'">
  <thead><tr><th>Label</th><th>Value</th></tr></thead>
  <tbody>
    @for (p of chart().data; track p.label) {
      <tr><td>{{ p.label }}</td><td>{{ fmt(p.value) }}</td></tr>
    }
  </tbody>
</table>
```

CSS for sr-only:

```css
.sr-only {
  position: absolute; width: 1px; height: 1px;
  padding: 0; margin: -1px; overflow: hidden;
  clip: rect(0, 0, 0, 0); border: 0;
}
```

## Bar Value Text Overflow

When a bar is near full width, the value text at `barEnd + 10` overflows the viewBox and gets clipped.

**Solution: clamp x position:**

```typescript
clamp(v: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(v, hi));
}
```

```html
<text [attr.x]="clamp(scaleFactor()(p.value) * (width - 80) + 10, 10, width - 4)">
```

## Vertical Bar Chart Height

Don't reuse `barChartHeight()` (computed from item count). Use a fixed height:

```typescript
protected readonly verticalBarHeight = 220;
```

Use `scaleFactor` for vertical bars too (consistency with horizontal):

```html
[attr.y]="verticalBarHeight - scaleFactor()(abs(p.value)) * (verticalBarHeight - 50) - 20"
[attr.height]="scaleFactor()(abs(p.value)) * (verticalBarHeight - 50)"
```

## Timeline Value Clamping

Timeline bars assume values are 0–7 (days). If data has values > 7, bars overflow.

**Solution: clamp to expected range:**

```html
[attr.width]="(clamp(p.value, 0, 7) / 7) * (width - 160)"
```

## Donut Segment Colors

Use inline `[attr.fill]="seg.color"` from the TS `displayColor` computed. Don't rely on CSS `nth-child` overrides — they fight with inline fill and ignore the computed palette.

```html
<path [attr.d]="seg.path" class="donut-segment" [attr.fill]="seg.color" />
```

Legend dots should match:

```html
<span class="legend-dot" [style.background]="seg.color"></span>
```

## `fmt()` Smart Formatting

Don't hardcode `$` for all values. Use magnitude-based formatting:

```typescript
fmt(v: number): string {
  if (v >= 10000) return `${(v / 1000).toFixed(1)}K`;
  if (v >= 100) return v.toFixed(0);
  if (v >= 1) return v.toFixed(2);
  if (v >= 0.01) return `$${v.toFixed(2)}`;  // Only $ for small values
  return v.toFixed(3);
}
```

## Testing Tips

- Make `scaleFactor` public (not protected) to test directly without `as any` casts.
- Project ESLint does NOT allow `_` prefix for unused vars. Use `() => 0` for no-op lambdas.
- Test accessibility: check `role="img"`, `aria-roledescription`, `<title>`, `.sr-only` table.
- Test log scale: with values 0.06 and 4.0, `scaleFactor()(0.06)` should be 0 (min maps to 0 in log scale).
- Don't set `max-height: 300px` on SVG — it clips tall bar charts. Let viewBox control sizing.

## Testing

```typescript
it('should render SVG for bar-horizontal chart', () => {
  const svg = compiled.querySelector('.chart-svg');
  expect(svg).toBeTruthy();
});

it('should compute max value correctly', () => {
  expect(component.max()).toBe(100);
});

it('should render donut chart for donut kind', () => {
  fixture.componentRef.setInput('chart', { ...mockChart, kind: 'donut' });
  fixture.detectChanges();
  expect(compiled.querySelector('.donut-segment')).toBeTruthy();
});
```

Test all 5 chart kinds. Test with wide-range data to verify log scale.
