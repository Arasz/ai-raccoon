# TypeScript Data File Authoring Patterns

When converting markdown articles or content to TypeScript data objects for the
`Article` model, follow these patterns to avoid common pitfalls.

## Helper factories (avoid verbose object literals)

Instead of inline object literals for every table cell/list item, define helper factories at the top of the data file:

```typescript
import type { Article, InlineContent, ContentBlock, TableBlock, TableCell, ListItem } from '../../models/article';
import { bold, code, link, text, paragraph, bulletList, callout, chart } from '../../models/article';

// ── Helper factories ───────────────────────────────────────────
function table(headers: TableCell[], rows: TableCell[][]): TableBlock {
  return {
    type: 'table',
    headers: { cells: headers },
    rows: rows.map(cells => ({ cells })),
  };
}

function row(...cells: TableCell[]): TableCell[] {
  return cells;
}

function cell(...content: InlineContent[]): TableCell {
  return { content };
}

function item(...content: InlineContent[]): ListItem {
  return { content };
}
```

**Usage:**

```typescript
table(
  [cell(bold("Model")), cell(bold("Cost")), cell(bold("Tokens"))],
  [
    row(cell(text("mimo")), cell(text("$0.10")), cell(text("988M"))),
    row(cell(text("Claude")), cell(text("$0.06")), cell(text("19.4B"))),
  ],
)
```

This is much cleaner than inline `{ cells: [{ content: [bold('Model')] }, ...] }`.

## String escaping — the apostrophe problem

Single-quoted strings with apostrophes (`'it's broken'`) break the Angular parser. This produces cascading errors that look like structural brace mismatches.

**Solutions (in order of preference):**

1. **Unicode escapes**: `text("it\u2019s broken")` — uses right single quotation mark
2. **Double quotes**: `text("it's broken")` — works but risks conflicts with template syntax
3. **Helper factories with Unicode**: Pre-define common phrases as constants

**Common Unicode escapes:**

- `\u2019` — right single quotation mark (')
- `\u2018` — left single quotation mark (')
- `\u201c` — left double quotation mark (")
- `\u201d` — right double quotation mark (")
- `\u2014` — em dash (—)
- `\u2013` – en dash (–)
- `\u00b7` — middle dot (·)

**Detection**: `npx tsc --noEmit` may pass while `ng build` fails. Always verify with `ng build`.

## Unused imports

The linter flags unused imports. Only import what you actually use. Common mistakes:

- Importing `ContentBlock`, `ListBlock`, `CalloutBlock`, `ChartBlock` (types used internally by the model, not directly in data files)
- Importing `heading`, `hr` (functions that are used inside the model, not in data files)

**Typical data file imports:**

```typescript
import type { Article, InlineContent, TableBlock, TableCell, ListItem } from '../../models/article';
import { bold, code, link, text, paragraph, bulletList, callout, chart } from '../../models/article';
```

## Structure overview

```
export const myArticle: Article = {
  meta: { title, subtitle, publishedAt, author, slug, description, tags, readingTimeMinutes, status },
  sections: [
    {
      heading: "Section Title",
      id: "section-id",
      blocks: [ paragraph(...), table(...), bulletList(...), callout(...), chart(...) ],
      subsections: [
        { heading: "Subsection", id: "sub-id", blocks: [...] },
      ],
    },
  ],
};
```

## Verification

After writing/updating a data file:

```bash
cd frontend && npx ng build 2>&1 | grep "your-file"
npm run lint 2>&1 | grep "your-file"
```
