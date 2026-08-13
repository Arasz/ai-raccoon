# TUI/CLI Design Patterns for Web Content

Research from studying Glow, Frogmouth, and Lipgloss for monospace blog/article styling.

## Key Patterns

### Glow (charmbracelet/glow, 17k+ stars)

- Headings: bold + distinct sizing, not just weight
- Generous vertical spacing between sections (double blank lines)
- Horizontal rules (`─` characters) between major sections
- Width-limited to ~100 cols for comfortable reading
- Indented block content with subtle background distinction

### Frogmouth (Textualize/frogmouth)

- Navigation sidebar (TOC) alongside content
- Bookmarks + history stack
- Scroll position indicator
- Heading hierarchy with strong visual weight differences

### Lipgloss (charmbracelet/lipgloss)

- Borders/padding around sections
- Block-level content gets background + padding treatment
- Headings use bold + color shift (not just size)

## Box-Drawing Characters Reference

```
┌─ Section heading
│
│ Body text here
│
├─ Next section heading
│
│ More body text
│
└─────────────────────────────
```

## Readability Research

- **Line length**: 50-75 characters per line (CPL), 66 CPL optimal
- **Monospace for long-form**: Needs extra line-height (1.75 vs 1.5 for proportional)
- **Letter-spacing**: 0.01-0.02em improves monospace readability
- **Contrast**: At least 4.5:1 for body text
- **Body text size**: At least 16px, preferably 18px

## CSS Patterns

### Title Rule (═══ double line)

```css
.blog-article__title::after {
  content: '';
  display: block;
  width: 100%;
  height: 3px;
  background:
    linear-gradient(var(--text-primary), var(--text-primary)) top / 100% 1px no-repeat,
    linear-gradient(var(--text-primary), var(--text-primary)) bottom / 100% 1px no-repeat;
  margin-top: var(--space-3);
}
```

### Section Separator (├─)

```css
.blog-article__body h3::before {
  content: '├─';
  display: block;
  color: var(--border-strong);
  font-size: var(--text-sm-size);
  margin-bottom: var(--space-2);
  letter-spacing: 0;
}
```

### Left Rail (│)

```css
.blog-article__body {
  border-left: 1px solid var(--border-default);
  padding-left: var(--space-5);
}
```
