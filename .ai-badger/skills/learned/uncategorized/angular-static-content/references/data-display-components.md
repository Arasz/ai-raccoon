# Data-Display Angular Components

Patterns for standalone Angular components that display structured data (chart blocks, metric cards, stat grids) with dark-themed card styling.

## Component skeleton

```typescript
// src/app/components/<name>/<name>.ts
import { Component, Input } from '@angular/core';
import { SomeModel } from '../../models/article';

@Component({
  selector: 'app-<name>',
  standalone: true,
  templateUrl: './<name>.html',
  styleUrls: ['./<name>.css'],
})
export class <Name>Component {
  @Input({ required: true }) data!: SomeModel;
}
```

Key points:

- Always `standalone: true` — no module declarations needed
- `@Input({ required: true })` enforces the parent must pass data
- Import the model from `../../models/article` (or wherever the interface lives)
- Template uses Angular 22 `@if`/`@for` (never `*ngIf`/`*ngFor`)

## Dark card CSS pattern

```css
.card {
  background: #1a1a2e;        /* dark navy base */
  border: 1px solid #2a2a3e;  /* subtle border, not invisible */
  border-radius: 12px;
  padding: 1.5rem;
  color: #e0e0e0;
  font-family: inherit;
}
```

### Badge styling (for types, categories, tags)

```css
.badge {
  display: inline-block;
  padding: 0.2rem 0.6rem;
  border-radius: 6px;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  background: #2d2d5e;       /* purple-tinted dark */
  color: #a5b4fc;            /* light indigo text */
  white-space: nowrap;
}
```

### Data list items (label: value pairs)

```css
.item {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.4rem 0.6rem;
  border-radius: 6px;
  background: #16162a;       /* slightly darker than card */
  font-size: 0.9rem;
  transition: background 0.15s;
}
.item:hover { background: #1f1f3a; }
.item .label { flex: 1; color: #d1d5db; }
.item .value { font-weight: 600; font-variant-numeric: tabular-nums; color: #e5e7eb; }
```

### Color dot (for data series)

```css
.color-dot {
  width: 10px; height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}
```

### Section separator (for optional footnotes/descriptions)

```css
.description {
  margin: 0;
  padding-top: 0.75rem;
  border-top: 1px solid #2a2a3e;
  font-size: 0.85rem;
  line-height: 1.5;
  color: #9ca3af;
}
```

## Template pattern with @if/@for

```html
<article class="card">
  <header class="card-header">
    <h3 class="card-title">{{ data.title }}</h3>
    <span class="badge">{{ data.kind }}</span>
  </header>

  <section class="data-section">
    @if (data.xLabel || data.yLabel) {
      <div class="axis-labels">
        @if (data.xLabel) { <span>X: {{ data.xLabel }}</span> }
        @if (data.yLabel) { <span>Y: {{ data.yLabel }}</span> }
      </div>
    }

    <ul class="data-list">
      @for (point of data.points; track point.label) {
        <li class="item">
          <span class="color-dot" [style.background]="point.color || '#6b7280'"></span>
          <span class="label">{{ point.label }}</span>
          <span class="value">{{ point.value }}</span>
        </li>
      }
    </ul>
  </section>

  @if (data.description) {
    <p class="description">{{ data.description }}</p>
  }
</article>
```

## Model-first workflow

1. Read the model interface first (e.g. `models/article.ts`)
2. Design the template around the interface properties
3. Use `@Input({ required: true })` — forces the parent to provide all data
4. Add `[style.background]` binding for optional color fields with a fallback
5. Use `track point.label` (or `track point.id` if available) in `@for`
