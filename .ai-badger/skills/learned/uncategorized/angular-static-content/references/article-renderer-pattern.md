# Article Renderer Component Pattern

Full implementation details for the universal `app-article-renderer` component that renders `Article` model objects into HTML.

## Model overview (`models/article.ts`)

```
Article
  └─ meta: ArticleMeta (title, subtitle, author, date, tags, etc.)
  └─ sections: ArticleSection[]
       ├─ heading: string (→ h2)
       ├─ id?: string
       ├─ blocks: ContentBlock[]
       └─ subsections?: ArticleSubsection[]
            ├─ heading: string (→ h3)
            ├─ id?: string
            └─ blocks: ContentBlock[]
```

`ContentBlock` is a union: `ParagraphBlock | HeadingBlock | TableBlock |
ListBlock | CalloutBlock | HorizontalRuleBlock | CodeBlock | SeparatorBlock |
ChartBlock | ProseWithChartBlock`

`InlineContent` is a union: `{type:'text', text} | {type:'bold', text} |
{type:'italic', text} | {type:'code', text} | {type:'link', text, href, title?}`

## Required imports in the component

```typescript
import {NgTemplateOutlet, DatePipe, UpperCasePipe} from '@angular/common';
import {ChartPlaceholderComponent} from '../chart-placeholder/chart-placeholder';
import {Article, ContentBlock} from '../../models/article';
```

**Pitfall — missing NgTemplateOutlet**: The new `@switch`/`@for`/`@if` control flow is compiler-built-in and needs no imports. But `*ngTemplateOutlet` is still a directive from `@angular/common`. Without importing `NgTemplateOutlet`, the
build fails with template binding errors.

## Flattening strategy

The `flatBlocks` computed signal converts the nested section/subsection tree into a flat `ContentBlock[]`. Section headings become synthetic `HeadingBlock` entries with `level: 2`; subsection headings get `level: 3`. This lets the template
use a single `@for` + `@switch` loop without nested section containers.

```typescript
protected readonly flatBlocks = computed<ContentBlock[]>(() => {
  const article = this.article();
  const blocks: ContentBlock[] = [];
  for (const section of article.sections) {
    blocks.push({ type: 'heading', level: 2,
      content: [{type: 'text', text: section.heading}], id: section.id });
    blocks.push(...section.blocks);
    if (section.subsections) {
      for (const sub of section.subsections) {
        blocks.push({ type: 'heading', level: 3,
          content: [{type: 'text', text: sub.heading}], id: sub.id });
        blocks.push(...sub.blocks);
      }
    }
  }
  return blocks;
});
```

## Template structure

### Reusable `ng-template` fragments

Two `ng-template` blocks sit OUTSIDE the main `<article>` element:

1. **`#inlineContent`** — renders `InlineContent[]` (text→span, bold→strong, italic→em, code→code, link→a). Used everywhere inline text appears.
2. **`#listItemTpl`** — renders `ListItem[]` recursively. References itself via `*ngTemplateOutlet` for nested `children`.

### Block type dispatch

```html
@for (block of flatBlocks(); track $index) {
  @switch (block.type) {
    @case ('paragraph') {
      <p class="block-paragraph">
        <ng-container *ngTemplateOutlet="inlineContent"
                      context: {$implicit: block.content}"/>
      </p>
    }
    @case ('heading') {
      @switch (block.level) {
        @case (2) { <h2 [id]="block.id">...</h2> }
        @case (3) { <h3 [id]="block.id">...</h3> }
        @case (4) { <h4 [id]="block.id">...</h4> }
      }
    }
    @case ('table') { /* thead with th from headers.cells, tbody from rows */ }
    @case ('list') { /* ol or ul based on block.ordered, recursive list items */ }
    @case ('callout') { <aside [class]="'block-callout block-callout--' + block.variant"> }
    @case ('chart') { <app-chart-placeholder [chart]="block"/> }
    @case ('code') { <pre><code>{{ block.code }}</code></pre> }
    @case ('hr') { <hr/> }
    @case ('separator') { <hr class="block-separator"/> }
    @case ('prose-with-chart') { /* before prose + chart + after prose */ }
  }
}
```

### Table rendering

```html
<div class="block-table">
  @if (block.caption) { <caption>{{ block.caption }}</caption> }
  <table>
    <thead><tr>
      @for (cell of block.headers.cells; track $index) {
        <th [style.text-align]="cell.align ?? 'left'">
          <ng-container *ngTemplateOutlet="inlineContent"
                        context: {$implicit: cell.content}"/>
        </th>
      }
    </tr></thead>
    <tbody>
      @for (row of block.rows; track $index) {
        <tr>
          @for (cell of row.cells; track $index; let i = $index) {
            @if (block.rowHeaders && i === 0) {
              <th [style.text-align]="cell.align ?? 'left'">...</th>
            } @else {
              <td [style.text-align]="cell.align ?? 'left'">...</td>
            }
          }
        </tr>
      }
    </tbody>
  </table>
</div>
```

## CSS class naming convention

BEM-style with `block-` prefix for content blocks:

```
.block-paragraph          .block-heading--2/3/4
.block-table              .block-list
.block-callout            .block-callout--note/warning/tip/important
.block-code               .code-header
.block-hr                 .block-separator
.block-chart              .block-prose-with-chart
.article__header          .article__title          .article__meta
.article__tags            .article__tag            .article__body
```

**Pitfall**: After writing template and CSS separately, grep class names in the HTML and verify each exists in CSS. Mismatched names cause silent styling failures.

## ChartPlaceholderComponent

Exists at `src/app/components/chart-placeholder/` with:

- Selector: `app-chart-placeholder`
- Input: `@Input({ required: true }) chart!: ChartBlock`
- Renders chart title, kind badge, data points list, axis labels, description
- Use it directly: `<app-chart-placeholder [chart]="block"/>`
- For `prose-with-chart` blocks, pass `block.chart` instead

## Callout variant styling

```css
.block-callout {
  padding: 1rem 1.25rem;
  border-left: 4px solid;
  border-radius: 6px;
  background: var(--surface-raised, #1a1a2e);
}
.block-callout--note     { border-color: #60a5fa; }
.block-callout--warning  { border-color: #fbbf24; }
.block-callout--tip      { border-color: #34d399; }
.block-callout--important { border-color: #f87171; }
.callout-label {
  font-size: 0.75rem; font-weight: 700;
  text-transform: uppercase; letter-spacing: 0.06em;
}
```
