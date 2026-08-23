# kind-accessibility

Applies to any interactive surface, in any UI stack — Vue, Angular, Blazor and plain HTML need
this kind exactly as much as React does, which is why it lives here rather than under a stack
file. The runner is whatever can query the accessibility tree (a DOM-shim query library, a real
browser plus an axe scan); several of these rules exist precisely because that runner is only
ever partial coverage — axe finds roughly 57% of issues and 30-40% of WCAG criteria — so a green
scan is never treated as "accessible" on its own.

*Deliberate note: `kind-accessibility.md`, not `kind-a11y.md`, and this file's rule-id group
is `T2-ACC-*`, not `T2-A11Y-*` — the id regex `^T2-[A-Z]{3,4}-\d{2}$` admits letters only, so a
literal `A11Y` id would itself be uncheckable. `archetypes.md` has been updated to cite
`T2-ACC-*` accordingly.*

**`T2-ACC-01` — Query by role first; test id is the last resort.**
- *design:* resolve every query through the accessibility tree (role + accessible name), then label, then visible text; reach for a test-id query only when no accessible query can find the element.
- *review:* a control an accessible query cannot find is a control a screen reader cannot find either — the query priority is itself an accessibility assertion.
- *check:* auto-unless-listed — count test-id queries against role queries; a file where test-id outnumbers role is a finding.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-CST-05`
- *cites:* testing-library query priority docs.
- *meta:* pass=0 order=1

**`T2-ACC-02` — Every interactive element under test is asserted to have an accessible name.**
- *design:* for each control the test drives, assert the accessible name as part of the test, not just the selector that finds it.
- *review:* a decorative label with no accessible name is invisible to a structural test and to a DOM shim alike — the runner's blindness and the production bug are the same shape.
- *check:* auto-unless-listed — a role query with no name assertion.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-CST-05`
- *meta:* pass=0 order=1

**`T2-ACC-03` — Accessibility is a failing test, not a review item, and every reachable state of a view is scanned — not just the happy one.**
- *design:* add an automated a11y scan to every view, and render every reachable state (empty, loading, error, populated, dialog-open) under it.
- *review:* a scan of only the populated state misses the states a new user hits first — this kind's instance of `T1-SCO-05`.
- *check:* auto-unless-listed — per view, does an a11y scan exist, and how many of its reachable states does it render?
- **severity:** blocker · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-SCO-05`
- *meta:* pass=6 order=24

**`T2-ACC-04` — At least one axe (or equivalent) scan runs the full ruleset in a real browser.**
- *design:* run one unnarrowed scan somewhere in a real browser; a DOM-shim scan and a `withRules([...])`-narrowed browser scan do not substitute for it.
- *review:* axe rules depending on computed style and layout cannot fire under a DOM shim, and a narrowed scan leaves every other rule unrun in the only environment that can evaluate it — the runner-observability finding `T1-CST-05` names, at the tool-configuration layer.
- *check:* auto — every narrowed scan call site, checked against whether an unnarrowed one exists anywhere.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-05`
- *meta:* pass=0 order=1

**`T2-ACC-05` — Do not treat a green axe run as "accessible".**
- *design:* pair every axe-only a11y suite with at least one hand-written keyboard/announcement test per interactive surface.
- *review:* axe catches roughly 57% of accessibility issues by volume and 30-40% of WCAG success criteria; focus order, meaningful names and live-region timing need an explicit test.
- *check:* auto-unless-listed — per view, does a keyboard/focus test exist alongside the scan?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-CST-05`
- *meta:* pass=0 order=1

**`T2-ACC-06` — The keyboard path is tested separately from the mouse path.**
- *design:* for any control with a custom interaction (menu, combobox, dialog, drag handle, sortable list), write a test that reaches and operates it with the keyboard only.
- *review:* a mouse-driven interaction succeeds on a control whose keyboard path is broken; nothing else in the suite would catch it.
- *check:* argued — for each custom composite, is there a test using keyboard-only interaction rather than a click?
- *rationale:* weak evidence — no first-party citation beyond the accessibility-by-default invariant; caps at `major`.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-SCO-05`
- *meta:* pass=6 order=24

**`T2-ACC-07` — Assert the association, not the presence, of a label or description.**
- *design:* assert the wiring — `getByLabelText`, an accessible-description resolving to the error node — never that the text merely exists on the page.
- *review:* a message rendered in a detached element three sections away from its control passes a mere-presence assertion and fails every real user.
- *check:* auto-unless-listed — text-presence assertions for label/error/description content, checked for a sibling wiring assertion.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-ORC-03`
- *meta:* pass=4 order=17
