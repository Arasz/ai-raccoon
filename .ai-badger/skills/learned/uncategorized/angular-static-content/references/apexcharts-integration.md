# ApexCharts Integration in Angular

## Why ApexCharts over custom SVG

Custom inline SVG charts were initially built but failed to produce acceptable visual quality. ApexCharts provides:

- Built-in monochrome theme (`theme.monochrome`)
- Responsive sizing with `width: '100%'`
- Built-in accessibility (ARIA attributes, keyboard nav)
- Interactive tooltips, animations
- ~130KB gzipped (reasonable for full-featured library)

## Setup

```bash
cd frontend && npm install apexcharts@~5.16.0 ng-apexcharts@^2.4.0
```

**IMPORTANT:** `ng-apexcharts@2.4.0` has peerDependency `apexcharts@^5.10.3`. Do NOT install `apexcharts@6.x` — it breaks horizontal bar rendering. Pin to `~5.16.0` (latest 5.x). Check with `npm ls apexcharts ng-apexcharts` for `invalid`
warnings.

Import in component:

```typescript
import {NgApexchartsModule, ApexOptions} from 'ng-apexcharts';
```

**Always import types from `ng-apexcharts`, NOT from `apexcharts` directly.** The two packages re-export their own type copies; importing from `apexcharts` causes type mismatch errors when versions diverge.

## Critical Pattern: AfterViewInit + updateOptions

**ApexCharts does NOT react to Angular computed signal inputs.** The chart initializes once and ignores subsequent signal changes.

**Correct pattern:**

```typescript
@Component({
  imports: [NgApexchartsModule],
  template: `<apx-chart #apexChart [series]="opts.series" ...></apx-chart>`
})
export class ChartRendererComponent implements AfterViewInit {
  readonly chart = input.required<ChartBlock>();
  @ViewChild('apexChart') apexChart!: ChartComponent;
  private _apexOptions!: Required<ApexOptions>;
  private ready = false;

  get apexOptions(): Required<ApexOptions> { return this._apexOptions; }
  get opts() { return this.apexOptions; }  // No cast — buildOptions returns Required<ApexOptions>

  constructor() {
    effect(() => {
      this._apexOptions = this.buildOptions(this.chart());
      if (this.ready && this.apexChart) {
        this.apexChart.updateOptions(this._apexOptions, true, true);
      }
    });
  }

  ngAfterViewInit(): void {
    this.ready = true;
    if (this.apexChart) {
      this.apexChart.updateOptions(this._apexOptions, true, true);
    }
  }

  private buildOptions(block: ChartBlock): Required<ApexOptions> { /* see defaults pattern below */ }
}
```

Key points:

- `buildOptions` returns `Required<ApexOptions>` natively via the defaults pattern (see below)
- `opts` getter has NO cast — returns `this.apexOptions` directly
- `effect()` updates the property AND calls `updateOptions` if view is ready
- `ngAfterViewInit` handles the initial render (effect runs before ViewChild is available)

## Defaults Pattern for Required\<ApexOptions\>

Define a `defaults` object typed as `Required<ApexOptions>` that fills ALL 23 top-level fields. Each switch case spreads `...defaults` and overrides only what differs.

```typescript
const defaults: Required<ApexOptions> = {
  chart: {...common, type: 'bar', height: 200},
  series: [],
  labels: [],
  colors: [...colors],
  theme,
  title: {text: block.title, style: titleStyle},
  tooltip,
  legend: {show: false},
  plotOptions: {},
  dataLabels: {enabled: false},
  xaxis: {categories: [...labels], labels: labelsConfig},
  yaxis: {labels: labelsConfig},
  grid,
  fill: {opacity: 1},
  stroke: {width: 0},
  markers: {size: 0},
  annotations: {},
  noData: {text: ''},
  responsive: [],
  forecastDataPoints: {},
  states: {},
  subtitle: {text: '', align: 'left'},
  parsing: {},
};
```

Then in each case:

```typescript
case 'donut':
  return { ...defaults, series: values, chart: {...common, type: 'donut', height: 240}, ... };
case 'bar-horizontal':
  return { ...defaults, series: [{...}], chart: {...common, type: 'bar', height}, ... };
```

Non-axis chart types (donut, pie) inherit axis-related defaults (xaxis, yaxis, grid, markers)
which ApexCharts ignores. This is correct — the defaults satisfy the type system.

## Monochrome Theme Config

```typescript
const TOKENS = {
  surface0: '#0b0b0c', surface1: '#151517', surface2: '#1e1e21',
  borderSubtle: '#26262a', borderDefault: '#3a3a3f', borderStrong: '#6b6b70',
  textPrimary: '#f4f4f5', textSecondary: '#b4b4b8', textMuted: '#838388',
  textDisabled: '#4b4b4f',
};

const MONO_PALETTE = [
  TOKENS.textPrimary, TOKENS.textSecondary, TOKENS.textMuted,
  TOKENS.borderStrong, TOKENS.textDisabled, TOKENS.borderDefault,
];

const theme = {
  mode: 'dark' as const,
  monochrome: {
    enabled: true,
    color: TOKENS.textMuted,
    shadeTo: 'light' as const,
    shadeIntensity: 0.6,
  },
};
```

Use `colors: MONO_PALETTE` with `distributed: true` for per-bar colors. The monochrome theme applies shade variations automatically.

## Responsive Width

Always set `width: '100%'` in the chart config:

```typescript
const common = {
  fontFamily: 'JetBrains Mono, monospace',
  background: 'transparent',
  width: '100%',
  toolbar: {show: false},
  animations: {enabled: true, speed: 400},
};
```

CSS container:

```css
.chart-svg-wrap { max-width: 100%; overflow: hidden; }
.chart-svg-wrap apx-chart { display: block; width: 100%; max-width: 100%; }
```

## Template Bindings (defaults pattern)

Since `buildOptions` returns `Required<ApexOptions>`, the `opts` getter needs no cast. Bind directly — Angular gets non-optional types automatically:

```html
<apx-chart
  [series]="opts.series"
  [chart]="opts.chart"
  [labels]="opts.labels"
  [colors]="opts.colors"
  [theme]="opts.theme"
  [title]="opts.title"
  [tooltip]="opts.tooltip"
  [legend]="opts.legend"
  [plotOptions]="opts.plotOptions"
  [dataLabels]="opts.dataLabels"
  [xaxis]="opts.xaxis"
  [yaxis]="opts.yaxis"
  [grid]="opts.grid"
  [fill]="opts.fill"
  [stroke]="opts.stroke"
  [markers]="opts.markers"
></apx-chart>
```

This works because `buildOptions` returns `Required<ApexOptions>` natively — every field is populated via the defaults object. No cast needed.

## Label Style Nesting

ApexCharts `xaxis.labels` and `yaxis.labels` expect `{show?, rotate?, style?, formatter?, ...}`. The common label style object is the INNER `style` property:

```typescript
const labelStyle = {colors: TOKENS.textMuted, fontSize: '10px', fontFamily: FONT};
const labelsConfig = {style: labelStyle};  // Wrap it!

// Correct:
xaxis: {categories: [...labels], labels: labelsConfig}
yaxis: {labels: {...labelsConfig, formatter: (val: number) => fmt(val)}}

// WRONG — labelStyle is a style sub-object, not a labels config:
xaxis: {categories: [...labels], labels: labelStyle}  // ← type error with ApexOptions
```

## Chart Type Configs

### Bar (horizontal/vertical)

```typescript
{
  series: [{name: title, data: values}],
  chart: {...common, type: 'bar', height: Math.max(250, labels.length * 50 + 80)},
  plotOptions: {bar: {horizontal, borderRadius: 2, barHeight: horizontal ? '45%' : undefined, columnWidth: horizontal ? undefined : '50%', distributed: true}},
  dataLabels: {enabled: true, style: {fontSize: '10px', fontFamily: FONT, colors: [TOKENS.textPrimary]}, offsetX: horizontal ? 6 : 0, formatter: (val: number) => fmt(val)},
  xaxis: {categories: [...labels], labels: labelsConfig, axisBorder: {color: TOKENS.borderSubtle}},
  yaxis: horizontal
    ? {title: {text: xLabel, style: {color: TOKENS.textMuted, fontSize: '10px'}}, labels: {...labelsConfig, formatter: (val: number) => fmt(val)}}
    : {labels: {...labelsConfig, formatter: (val: number) => fmt(val)}},
  colors: [...colors],
  legend: {show: false},
}
```

Note: categories always go on `xaxis` — ApexCharts internally swaps axes for horizontal bars.

### Donut

```typescript
{
  series: values,  // Just numbers, not {name, data}
  chart: {...common, type: 'donut', height: 240},
  labels,  // Category names go here, not in xaxis
  plotOptions: {pie: {donut: {size: '65%', labels: {show: true}}}},
  stroke: {width: 2, colors: [TOKENS.surface1]},
  legend: {show: true, position: 'bottom'},
}
```

### Scatter

```typescript
{
  series: [{name: title, data: values.map((v, i) => ({x: labels[i], y: v}))}],
  chart: {...common, type: 'scatter', height: Math.max(200, labels.length * 36 + 60)},
  xaxis: {labels: labelsConfig, axisBorder: {color: TOKENS.borderSubtle}},
  markers: {size: 6, colors: [...colors], strokeColors: TOKENS.surface1, strokeWidth: 2},
  dataLabels: {enabled: false},
  plotOptions: {},
}
```

### Timeline (horizontal bar with max)

```typescript
{
  series: [{name: title, data: values}],
  chart: {...common, type: 'bar', height: Math.max(160, labels.length * 56 + 80)},
  plotOptions: {bar: {horizontal: true, borderRadius: 4, barHeight: '35%', distributed: true}},
  xaxis: {categories: labels, min: 0, max: maxVal, labels: {...labelsConfig, formatter: (v: string) => `${v}d`}},
  dataLabels: {enabled: true, formatter: (val: number) => `${val}d / ${maxVal}`, offsetX: 6},
}
```

## Testing

`apx-chart` doesn't render real charts in unit tests. Test the options object instead:

```typescript
import {ApexAxisChartSeries, ApexOptions} from 'ng-apexcharts';  // NOT from 'apexcharts'

it('should build bar-horizontal options', () => {
  setup(makeChart({kind: 'bar-horizontal', data: [{label: 'A', value: 10}]}));
  const opts = component.apexOptions;
  expect(opts.chart!.type).toBe('bar');           // Non-null assertion — test data is controlled
  expect(opts.series).toEqual([{name: 'BC', data: [10, 20]}]);
  expect(opts.plotOptions!.bar!.horizontal).toBe(true);
});

it('apexOptions has type-safe ApexOptions shape', () => {
  setup(makeChart({kind: 'bar-horizontal', data: [{label: 'A', value: 1}]}));
  const opts: ApexOptions = component.apexOptions;  // Type check — no 'any'
  expect(opts).toBeTruthy();
});
```

Make `apexOptions` a public getter (not protected) so tests can access it. Use `!` non-null assertions on optional properties since test data is controlled.

## Pitfalls

1. **Computed signals don't trigger render** — use AfterViewInit + updateOptions pattern
2. **`width: '100%'` required** — otherwise chart renders at parent natural width, causes scrollbar
3. **Donut series = numbers** — not `{name, data}` objects. Labels go in the `labels` property.
4. **SSR chunk is lazy-loaded** — `ng-apexcharts` imports `apexcharts/ssr` dynamically. Shows ~183KB in build but doesn't execute in browser. Don't try to remove it.
5. **`async ngAfterViewInit` breaks** — `updateOptions` is synchronous. Don't use `await`.
6. **`effect(async () => {...})` breaks dependency tracking** — Angular's `effect()` only tracks signals synchronously. Using `async` makes the callback return a Promise immediately; Angular can't track signal reads inside the async body.
   The effect never re-fires. Always use a synchronous callback: `effect(() => { ... })`.
7. **Global `* { max-width: 100% }` breaks ApexCharts SVG** — Many Angular projects have a global CSS reset with `* { max-width: 100% }`. This bleeds into ApexCharts' internal SVG elements (`foreignObject`, `canvas`, bars), constraining
   their explicit pixel widths. Horizontal bars are especially affected — they render as zero-width because the internal layout depends on explicit widths.

   **CRITICAL: The override MUST be in the global `styles.css`, NOT in component CSS.** Angular ViewEncapsulation adds `[_ngcontent-xxx]` attribute selectors to component styles. ApexCharts internal elements don't have that attribute, so a
   component-scoped override will NEVER match. The `*` rule in `styles.css` is global; the fix must also be global.

   **Fix in `src/styles.css`** (right after the `* { max-width: 100% }` rule):
   ```css
   .apexcharts-canvas,
   .apexcharts-canvas svg,
   .apexcharts-canvas foreignObject,
   .apexcharts-svg {
     max-width: none !important;
     width: auto !important;
   }
   ```
   Debug by inspecting the `foreignObject` element — if its computed `max-width` is smaller than its `width` attribute, the global rule is constraining it.
8. **`ng-apexcharts` version pinning** — `ng-apexcharts@2.4.0` has peerDependency `apexcharts@^5.10.3`. Installing `apexcharts@6.x` breaks horizontal bar rendering — bars simply don't appear. Always check `npm ls apexcharts ng-apexcharts`
   for `invalid` warnings after any dependency update. Pin `apexcharts` to `~5.16.0` (latest 5.x) until `ng-apexcharts` publishes a 6.x-compatible release.
9. **`labelStyle` nesting** — The `{colors, fontSize, fontFamily}` object is a *style* sub-object, not the labels config. ApexCharts `xaxis.labels` expects `{show?, rotate?, style?, formatter?, ...}`. Wrap it:
   `const labelsConfig = {style: labelStyle}`. Passing `labelStyle` directly to `labels` worked with `any` return type but fails with `ApexOptions` typing.
10. **`buildOptions` return `Required<ApexOptions>` with defaults** — Don't cast `opts` with `as Required<ApexOptions>`. Instead, make `buildOptions` return `Required<ApexOptions>` natively via the defaults pattern (see "Defaults Pattern"
    section above). All 23 fields populated, each case spreads `...defaults`.
11. **No cast on `opts` getter** — Since `buildOptions` returns `Required<ApexOptions>`, the getter is simply `get opts() { return this.apexOptions; }`. The `Required<T>` type flows naturally from the defaults object.
12. **Test imports: `ng-apexcharts` not `apexcharts`** — Import `ApexOptions`, `ApexAxisChartSeries` from `ng-apexcharts`. The two packages re-export their own type copies; importing from `apexcharts` causes type mismatch when versions
    diverge. Use `!` non-null assertions in tests since data is controlled.
