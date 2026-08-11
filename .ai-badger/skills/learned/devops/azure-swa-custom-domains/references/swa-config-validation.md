# staticwebapp.config.json validation pitfalls

## Brace expansion in exclude paths

**Symptom:** SWA deploy fails with:

```
Encountered an issue while validating staticwebapp.config.json:
Found an exclude path with multiple wildcard characters '*' in the 'exclude' property.
A route can have at most one '*' character.
```

**Root cause:** Azure SWA does not support brace expansion (`{a,b,c}`) in glob patterns. The error message is misleading — `*.{html,css,js}` has only one `*`, but the validator treats brace expansion as multiple implicit wildcards.

**Fix:** Replace the brace-expanded glob with individual entries:

```jsonc
// BAD — brace expansion
"exclude": ["/assets/*", "*.{html,css,js,ico,png,svg}"]

// GOOD — individual entries
"exclude": ["/assets/*", "*.html", "*.css", "*.js", "*.ico", "*.png", "*.svg"]
```

**Test maintenance:** If tests assert the config contains the old brace-expanded string, update them to assert the individual entries. The test intent (".html files excluded from fallback") is preserved; only the assertion shape changes.

## Prefixed glob patterns vs bare globs in test assertions

**Symptom:** Tests assert `.toContain("*.html")` but the config uses `/*.html` and/or
`**/*.html` — the exact string `"*.html"` is not in the array.

**Root cause:** Azure SWA supports prefix-scoped globs:

- `*.html` — matches at any level (broadest)
- `/*.html` — matches root-level only
- `**/*.html` — matches at any depth (recursive)

A config may use the more specific `/*.html` + `**/*.html` pair instead of bare `*.html`. Tests using `.toContain("*.html")` will fail because neither `/*.html` nor `**/*.html`
equals the exact string `"*.html"`.

**Fix:** Replace exact-match assertions with a predicate that checks whether ANY exclude pattern covers the file type:

```typescript
// BAD — exact string match
expect(navigationFallback?.exclude).toContain("*.html");

// GOOD — checks if any pattern covers .html files
expect(navigationFallback?.exclude?.some((p) => p.includes(".html"))).toBe(true);
```

The intent (".html files are excluded from the fallback rewrite") is preserved; the assertion is now pattern-shape-agnostic.

### Observed

- jsaa SWA deploy 2026-08-11: `src/frontend/public/staticwebapp.config.json` had
  `*.{html,css,js,ico,png,svg,jpg,jpeg,gif,webp,woff,woff2,json,txt,xml}` in
  `navigationFallback.exclude`. Fixed by expanding to 16 individual entries including
  `/*.html` + `**/*.html`. Two vitest assertions in `staticwebapp-config.test.ts`
  needed updating from `.toContain("*.html")` to `.some(p => p.includes(".html"))`.
