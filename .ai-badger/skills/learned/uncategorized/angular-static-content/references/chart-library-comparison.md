# Chart Library Comparison for Angular (2026)

## Recommendation: ApexCharts

For projects with strict visual theming (monochrome, design tokens), ApexCharts is the best choice.

## Comparison Matrix

| Library                        | Bundle (gzipped) | Angular 22    | Monochrome Theme                | Customization                | Output        | License         |
|--------------------------------|------------------|---------------|---------------------------------|------------------------------|---------------|-----------------|
| **Chart.js + ng2-charts**      | ~70 KB           | ✅ standalone | Manual (per-element colors)     | Every element                | Canvas        | MIT             |
| **ApexCharts + ng-apexcharts** | ~130 KB          | ✅ standalone | **Built-in** `theme.monochrome` | Every element, CSS `--apx-*` | SVG           | MIT             |
| **ECharts + ngx-echarts**      | ~100-300 KB      | ✅ standalone | Theme system (custom)           | Extremely deep               | SVG or Canvas | Apache 2.0      |
| **AG Charts**                  | ~135 KB          | ✅ standalone | Theme overrides                 | Rich theming API             | SVG           | MIT (community) |

## Why ApexCharts Wins for Monochrome Terminal Aesthetic

1. **Built-in monochrome theme** — one config line: `theme: { monochrome: { enabled: true, color: '#838388', shadeTo: 'light', shadeIntensity: 0.6 } }`
2. **CSS custom properties** (v6) — `--apx-*` tokens map to existing design tokens
3. **SVG output** — works with CSS audits (no canvas hex colors to audit)
4. **Dark mode built-in** — `theme: { mode: 'dark' }`
5. **Responsive** — auto-resizes with `width: '100%'`
6. **Accessible** — built-in ARIA attributes, keyboard navigation
7. **5 chart types needed** — bar (horizontal/vertical), donut, scatter, line/timeline — all supported

## Why NOT the Others

- **Chart.js**: Lightest but Canvas-based. Harder to audit colors, no SVG accessibility.
- **ECharts**: Most powerful (geo, sankey, treemap, sunburst) but heavy and complex theming.
- **AG Charts**: Enterprise-grade but community edition has fewer chart types.

## Installation

```bash
cd frontend && npm install apexcharts ng-apexcharts
```

SSR chunk: `ng-apexcharts` imports `apexcharts/ssr` dynamically. Shows ~183KB lazy chunk in build but doesn't execute in browser. Expected behavior.

## References

- ApexCharts themes: https://apexcharts.com/docs/options/theme/
- ApexCharts colors: https://apexcharts.com/docs/colors/
- ng-apexcharts: https://www.npmjs.com/package/ng-apexcharts
- Angular integration: https://apexcharts.com/docs/angular-charts/
