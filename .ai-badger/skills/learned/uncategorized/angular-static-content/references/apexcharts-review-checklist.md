# ApexCharts Review Checklist

Systematic audit for Angular components that integrate ApexCharts via `ng-apexcharts`. Derived from the ApexCharts SKILL.md v2.0.1 (library v6.2.0 reference) and real review findings. Load the ApexCharts MCP reference
(`apexcharts_get_reference(file='SKILL.md')`)
for the authoritative data format table and pitfalls when reviewing.

## 1. Series Data Format (Critical — #1 source of broken charts)

Each chart type has a specific series format. Verify against the ApexCharts Data Format Table:

| Chart Kind      | Expected Format                                       |
|-----------------|-------------------------------------------------------|
| bar             | `[{ name, data: number[] }]`                          |
| donut / pie     | `series: number[]` + `labels: string[]` (flat arrays) |
| scatter         | `[{ name, data: [{ x, y }] }]` (always XY objects)    |
| bubble          | `[{ name, data: [{ x, y, z }] }]` (z is required)     |
| heatmap/treemap | `[{ name, data: [{ x, y: number }] }]`                |
| rangeBar        | `[{ name, data: [{ x, y: [start, end] }] }]`          |
| candlestick     | `[{ data: [{ x, y: [O, H, L, C] }] }]`                |
| radar           | `[{ name, data: number[] }]` + `xaxis.categories`     |

**Common mistake:** passing `[{ name, data }]` to pie/donut (should be flat `number[]`).

## 2. Formatter Signatures

Verify each formatter matches its expected signature:

| Config Path                                    | Signature                                              | Returns |
|------------------------------------------------|--------------------------------------------------------|---------|
| `xaxis.labels.formatter`                       | `(value: string\|number, timestamp?, opts?) => string` | string  |
| `yaxis.labels.formatter`                       | `(value: number, opts?) => string`                     | string  |
| `tooltip.y.formatter`                          | `(val: number, opts?) => string`                       | string  |
| `dataLabels.formatter`                         | `(value: number\|string, opts?) => string\|number`     | str/num |
| `plotOptions.pie.donut.labels.value.formatter` | `(val: string) => string`                              | string  |
| `plotOptions.pie.donut.labels.total.formatter` | `(w: {globals, config}) => string`                     | string  |

**Common mistake:** `tooltip.y.formatter` only gets `(val, opts)`, not `(val, timestamp, opts)`.

## 3. Theme / Colors

- [ ] All hex colors include `#` prefix (e.g. `'#FF5733'`, not `'FF5733'`)
- [ ] Monochrome theme config: `{ mode: 'dark', monochrome: { enabled: true, color, shadeTo, shadeIntensity } }`
- [ ] Per-segment colors via `colors` array + `distributed: true` (bar), or `labels` (donut)
- [ ] No hardcoded color strings outside the TOKENS/PALETTE constants

## 4. Non-Axis Chart Config Hygiene

Donut, pie, polarArea, radialBar, and gauge are **non-axis charts**. These fields are ignored by the renderer and should not be passed (or at minimum should not be relied upon):

- `xaxis`, `yaxis`, `grid`, `markers` — meaningless for non-axis charts
- Passing them is not a bug (ApexCharts ignores them) but adds config noise
- The `Required<ApexOptions>` defaults pattern populates all fields (including axis ones) for non-axis charts; document that these are type-system placeholders, not functional config

## 5. chart.id

- [ ] Does the config set `chart.id`? ApexCharts auto-generates one, but an explicit id enables:
    - `ApexCharts.exec()` for external control
    - SSR hydration matching
    - Debug identification in multi-chart pages

## 6. yaxis Configuration

- [ ] Single series → single yaxis object is fine
- [ ] Multiple series → `yaxis` MUST be an array with `seriesName` mapping per entry
- [ ] Current data model may be single-series only; note if multi-series support is planned

## 7. Tooltip Config

- [ ] `tooltip.shared` and `tooltip.intersect` are mutually exclusive — never both `true`
- [ ] `tooltip.theme: 'dark'` matches the chart theme mode
- [ ] Tooltip formatter return types are strings (never `undefined`)

## 8. Responsive / Sizing

- [ ] `width: '100%'` set in chart config (prevents overflow)
- [ ] `height` is dynamic based on data point count for bar charts: `Math.max(250, labels.length * 50 + 80)`
- [ ] No `max-height` on chart container that could clip tall charts
- [ ] Global `* { max-width: 100% }` override in `styles.css` (not component CSS)
- [ ] `overflow: hidden` on wrapper may clip tooltips — consider `overflow: visible` or test tooltip edge cases

## 9. Angular Wrapper Usage

- [ ] `ng-apexcharts` module imported (not raw ApexCharts)
- [ ] Types imported from `ng-apexcharts`, not `apexcharts` directly
- [ ] `ApexOptions` return type on `buildOptions` (not `any`)
- [ ] `Required<ApexOptions>` via defaults pattern (no `as Required<>` cast — all 23 fields in defaults object)
- [ ] `apexOptions` is a public getter for test access
- [ ] Caching strategy: options rebuilt only when input changes (by id or reference)

## 10. Accessibility

- [ ] Chart title has an `id` attribute for `aria-labelledby`
- [ ] `<apx-chart>` element has `aria-labelledby` pointing to title id
- [ ] Or: `role="img"` with `aria-label` on the chart element
- [ ] `description` field rendered for screen readers (footnote context)

## 11. Test Coverage

Unit tests (vitest):

- [ ] All chart kinds tested (each switch branch)
- [ ] Real article data tested (extract charts from articles, verify options)
- [ ] Edge cases: single data point, custom color override, empty data
- [ ] Caching test (same reference on repeated access)
- [ ] fmt () edge cases: negative values, NaN, Infinity, boundary values (999, 1000, 0.01)
- [ ] Default/fallback case tested
- [ ] Axis-labels section rendering (xLabel, yLabel, lowerIsBetter)
- [ ] `ApexOptions` type annotation test (not `any`)

E2E tests (Playwright):

- [ ] Chart canvas visible on page
- [ ] Bar segments have non-zero SVG path `d` attribute
- [ ] Donut segments visible (`.apexcharts-pie-area`)
- [ ] Chart title rendered correctly
- [ ] Multi-chart pages (all charts render)

## 12. Test Environment

- [ ] `ResizeObserver` polyfill in test setup (ApexCharts needs it in jsdom):
  ```typescript
  // In test setup or beforeEach
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  ```
- [ ] No console noise from missing browser APIs in test output

## 13. fmt () Formatter Consistency

When `fmt()` is shared across chart types:

- [ ] Dollar prefix `$` only appears for values < 1 (small costs) — not for all values
- [ ] Large values use `K` suffix (>= 1000)
- [ ] Mid-range values show plain numbers
- [ ] Consider separating currency formatter from general number formatter if the component renders both cost and non-cost charts

## Severity Levels for Findings

| Severity   | Criteria                                                     |
|------------|--------------------------------------------------------------|
| Critical   | Wrong series data format (chart renders blank or crashes)    |
| Major      | Missing global CSS override (bars render zero-width)         |
| Major      | Wrong formatter signature (type mismatch, potential runtime) |
| Minor      | Unused config on non-axis charts (noise, not breakage)       |
| Minor      | Missing chart.id (works, but limits external control)        |
| Minor      | Missing accessibility attributes                             |
| Suggestion | fmt() inconsistency, test coverage gaps, overflow concerns   |
