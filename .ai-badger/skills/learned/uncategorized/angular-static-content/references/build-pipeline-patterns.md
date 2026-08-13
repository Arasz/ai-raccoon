# Build Pipeline Patterns

## npm build chain

```json
{
  "scripts": {
    "build-articles": "cd .. && node scripts/build-articles.mjs",
    "build": "npm run build-articles && ng build && npm run generate:rss"
  }
}
```

`build-articles` runs before `ng build`, so generated TS files are always fresh.

## build-articles.mjs pipeline

```
1. Find docs/blog/*.md with YAML frontmatter
2. For each:
   a. Parse frontmatter → ArticleMeta
   b. Parse body → blocks (headings, paragraphs, tables, lists, code, callouts, charts)
   c. Group blocks into sections/subsections
   d. Scan generated TS for which helpers are called
   e. Emit .data.ts with dynamic imports (only used helpers)
3. Generate articles.provider.ts barrel file
4. Run eslint --fix on all generated files
```

## Parser safety pattern

Every line-by-line parser must advance `i` even when a line doesn't match:

```javascript
while (i < lines.length) {
  const line = lines[i].trim();
  
  if (line.startsWith('##')) { /* heading */ i++; continue; }
  if (line.startsWith('|')) { /* table */ i += tableLines.length; continue; }
  if (line.startsWith('> ')) { /* callout */ i++; continue; }
  
  // Paragraph (default)
  const paraLines = [];
  while (i < lines.length && lines[i].trim()) {
    const s = lines[i].trim();
    if (s.startsWith('##') || s.startsWith('|') || s.startsWith('> ') || s.startsWith('<!--')) break;
    paraLines.push(s);
    i++;
  }
  if (paraLines.length) {
    blocks.push({ type: 'paragraph', content: parseInline(paraLines.join(' ')) });
  } else {
    i++; // CRITICAL: advance past unhandled line
  }
}
```

Without the `else { i++; }`, unhandled lines (HTML comments, HTML tags, etc.) cause infinite loops.

## Dynamic import scanning

```javascript
const allHelpers = ['bold', 'code', 'link', 'text', 'paragraph', 'heading', 'bulletList', 'callout', 'chart', 'hr', 'table', 'cell', 'row', 'item'];
const tsContentRaw = sections.map(s => tsSection(s, 2)).join('\n');
const usedHelpers = allHelpers.filter(h => tsContentRaw.includes(`${h}(`));
```

This prevents ESLint `no-unused-vars` errors in generated files.

## ESLint --fix step

```javascript
const { execSync } = await import('node:child_process');
execSync(`npx eslint --fix ${generatedFiles.join(' ')}`, {
  cwd: join(PROJECT_ROOT, 'frontend'),
  stdio: 'pipe',
});
```

Catches formatting issues and unused imports automatically.

## Testing

```bash
node --test scripts/tests/test-build-articles.mjs  # Node.js built-in test runner
```

Tests cover: frontmatter parsing, heading conversion, table conversion, inline content (bold/italic/code/link), list conversion, code block conversion, callout conversion, unicode escaping, dynamic imports, provider generation, and full
article round-trip.
