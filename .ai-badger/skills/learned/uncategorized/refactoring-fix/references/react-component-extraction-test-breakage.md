# React Component Extraction: UI Pattern Change Breaks Parent Tests

## The Pattern

When extracting a sub-component from a parent that **changes the display pattern** (e.g., flat content → accordion sections, always-visible → collapsed-by-default), existing parent-component tests that assert on the now-extracted content will break. The tests expect the text to be immediately visible, but it's now behind an interaction gate (click to expand, hover to reveal, etc.).

## How It Manifests

```
TestingLibraryElementError: Unable to find an element with the text: /expect a system-design deep dive/i
```

The parent test file (`interviews-accordion.test.tsx`) fails because it asserts `getByText(...)` on content that is now inside an accordion section with `defaultValue={[]}` (all collapsed).

## Root Cause

The extraction changed the **UI contract**: content that was previously always-visible became collapsed-by-default. The parent tests were written against the old contract.

## Detection

After GREEN (your new component tests pass), always run the **full test suite**, not just the new file:

```bash
npx vitest run           # full suite
# NOT just:
npx vitest run path/to/new-component.test.tsx
```

Parent integration breakage shows up as `TestingLibraryElementError` in existing test files — not in your new component's tests.

## Fix Pattern

Update the existing parent tests to interact with the new UI before asserting content:

```tsx
// BEFORE (flat content, always visible):
expect(screen.getByText("deep dive")).toBeInTheDocument();

// AFTER (must expand accordion first):
await user.click(screen.getByRole("button", { name: /expectations/i }));
expect(screen.getByText("deep dive")).toBeInTheDocument();
```

Key: use `screen.getByRole("button", { name: /section name/i })` to find the accordion trigger, then `user.click()` to expand, then assert content.

## Verification

After fixing the parent test, run both files together:

```bash
npx vitest run app/path/to/new-component.test.tsx app/path/to/parent-component.test.tsx
```

Then the full suite to catch any other transitive breakage.

## Radix UI Accordion: Collapsed-by-Default

Radix UI `Accordion` with `type="multiple"` defaults to all items open unless you pass `defaultValue={[]}`:

```tsx
<Accordion type="multiple" defaultValue={[]}>
  <AccordionItem value="section-1">
    <AccordionTrigger>Section 1</AccordionTrigger>
    <AccordionContent>
      {/* Content only rendered in DOM when expanded */}
    </AccordionContent>
  </AccordionItem>
</Accordion>
```

With `defaultValue={[]}`, accordion content is **not in the DOM** when collapsed (Radix renders content lazily). This means `screen.queryByText(...)` returns `null` — it's not hidden via CSS, it's truly absent.

## TDD Sequence for Component Extraction

1. **RED** — Write failing tests for the new sub-component
2. **GREEN** — Implement the sub-component
3. **Commit GREEN** (separate from integration)
4. **Integrate** — Replace inline code in parent with `<NewComponent ... />`
5. **Fix parent tests** — Update assertions that now hit the new UI pattern
6. **Run full suite** — Confirm zero regressions
7. **Commit integration**

The critical gap is between steps 2 and 3: a GREEN sub-component does NOT mean the integration is correct. Steps 4-6 catch the contract change.