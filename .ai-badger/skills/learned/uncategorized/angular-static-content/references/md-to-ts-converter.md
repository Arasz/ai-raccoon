# Markdown-to-TypeScript Article Converter

A Python script (`scripts/md-to-article.py`) converts markdown files with YAML frontmatter into TypeScript data files that export `Article` objects using the helper functions from `models/article.ts`.

## Location

- Script: `scripts/md-to-article.py`
- Tests: `scripts/tests/test_md_to_article.py` (pytest, 37 tests)

## Usage

```bash
python3 scripts/md-to-article.py <input.md> [output.ts]
# If output.ts is omitted, writes to input.ts
```

## Input format

Markdown with YAML frontmatter:

```markdown
---
title: "My Article"
subtitle: Optional subtitle
slug: my-article
publishedAt: 2026-07-20
updatedAt: 2026-07-23
author: Author Name
description: SEO description
tags: [tag1, tag2]
status: draft
---

## Section Heading

Paragraph with **bold**, *italic*, `code`, and [link](https://example.com).

### Subsection

| Header | Header |
|---|---|
| **Bold cell** | Normal cell |

- Bullet item
- **Bold item**

> **Note:** Callout text

```python
print("code block")
```

#### Sub-heading (h4)

<!-- CHART: chart-id -->

```

## Output format

TypeScript file using helper functions:

```typescript
import type { Article } from '../../models/article';
import { text, bold, code, link, paragraph, heading, bulletList,
         callout, chart, hr, table, cell, item } from '../../models/article';

export const myArticle: Article = {
  meta: { title, subtitle, publishedAt, updatedAt, author, slug,
          description, tags, readingTimeMinutes, status },
  sections: [
    {
      heading: "Section Heading",
      id: "section-heading",
      blocks: [
        paragraph(text("..."), bold("..."), ...),
        heading(4, text("Sub-heading")),
        table([cell(...)], [[cell(...), ...]]),
        bulletList(item(...), item(...)),
        callout("note", bold("Note:"), text("...")),
        { type: "code", language: "python", code: "..." },
        chart("chart-id", "bar-horizontal", "TODO: Chart Title", []),
        hr(),
      ],
      subsections: [
        { heading: "Subsection", id: "subsection", blocks: [...] },
      ],
    },
  ],
};
```

## Mapping rules

| Markdown               | TypeScript                                               |
|------------------------|----------------------------------------------------------|
| `## Heading`           | `ArticleSection` (top-level section)                     |
| `### Heading`          | `ArticleSubsection` (nested under current section)       |
| `#### Heading`         | `heading(4, ...)` block in current section/subsection    |
| `**text**`             | `bold("text")`                                           |
| `*text*`               | `{ type: "italic", text: "text" }` (no helper fn)        |
| `` `text` ``           | `code("text")`                                           |
| `[text](url)`          | `link("text", "url")`                                    |
| `\| A \| B \|` table   | `table([cell(...)], [[cell(...)]])`                      |
| `- item`               | `bulletList(item(...))`                                  |
| `> **Note:** text`     | `callout("note", bold("Note:"), text("..."))`            |
| `> **Warning:** text`  | `callout("warning", ...)`                                |
| `> plain text`         | `callout("note", text("..."))` (default variant)         |
| ` ```lang\ncode\n``` ` | `{ type: "code", language: "lang", code: "..." }`        |
| `---`                  | `hr()`                                                   |
| `<!-- CHART: id -->`   | `chart("id", "bar-horizontal", "TODO: Chart Title", [])` |

## Design decisions

1. **Helper functions over raw objects**: The converter uses the same helper functions (`text`, `bold`, `callout`, `table`, `cell`, `item`) that the existing hand-authored data files use. This keeps generated output consistent with the
   codebase convention.

2. **No `italic()` helper**: The `article.ts` model has no `italic()` factory function, so italic content uses inline object literals `{ type: "italic", text: "..." }`.

3. **Chart placeholders**: `<!-- CHART: id -->` comments become minimal
   `chart("id", "bar-horizontal", "TODO: Chart Title", [])` calls. The chart kind, title, and data must be filled in manually after conversion.

4. **Section IDs**: Generated via slugify (lowercase, hyphenated). `TL;DR` →
   `tldr`, `The Agents Tested` → `the-agents-tested`.

5. **Reading time**: Calculated from word count at 200 wpm, excluding code blocks and markdown syntax.

6. **Const name**: Derived from slug via camelCase. `my-article` → `myArticle`.

## Pitfalls

- **Italic has no helper function**: Unlike `text()`, `bold()`, `code()`,
  `link()`, there is no `italic()` export from `article.ts`. The converter emits `{ type: "italic", text: "..." }` inline. This is valid TypeScript but less clean than the other inline types.

- **Chart kind is always placeholder**: The `<!-- CHART: id -->` comment only provides the chart ID. The `kind`, `title`, and `data` fields use placeholder values (`"bar-horizontal"`, `"TODO: Chart Title"`, `[]`). These MUST be filled in
  manually.

- **No nested inline parsing**: The regex-based inline parser handles `**bold**`
  and `*italic*` separately but does NOT support nesting (e.g., `**bold *nested
  italic***` may produce unexpected results). Keep inline formatting flat.

- **Frontmatter dates as strings**: YAML `2026-07-20` is kept as the string
  `"2026-07-20"`, not parsed to a Date object. This matches the TypeScript
  `ArticleMeta.publishedAt: string` type.

- **No `row()` helper**: The existing hand-authored data files sometimes define a local `row()` helper for table rows. The converter uses `[cell(...), cell(...)]`
  arrays directly (the imported `table()` helper from `article.ts` accepts this).

## TDD pitfall (code generation tests)

When writing tests for code that generates output in another language/format:

**The assumed-output trap**: Tests assert raw target structure (`type: "table"`,
`variant: "note"`) but the implementation emits idiomatic helper calls (`table(...)`, `callout("note", ...)`). Tests fail in bulk — not because behavior is wrong, but because assertions targeted the wrong output pattern.

**Fix**: Study the target ecosystem's conventions from existing files BEFORE writing tests. If the target uses helper functions, test for helper function patterns. Example:

```python
# WRONG — asserts raw object literal
assert 'type: "table"' in ts_output

# RIGHT — asserts helper function call
assert 'table(' in ts_output
```

This session hit 8 test failures from this mismatch. The fix was updating assertions, not the implementation.
