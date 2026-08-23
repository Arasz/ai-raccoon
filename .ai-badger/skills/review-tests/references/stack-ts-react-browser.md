# Stack — TypeScript / React 19 / browser

Layer 3. Every rule below specialises an L1 (`T1-*`) or L2 (`T2-*`) rule named in
`parent:`. The pre-consolidation id survives only in `absorbs:` (governance.md,
id scheme). Scope: what is browser- and React-specific. Naming, one-behaviour-
per-test, mutation proof and coverage-vs-assertion live at L1/L2 — this file adds
only the frontend-specific check.

Reference implementation validated against a React 19.2 / TypeScript 6 app on
`bun:test` + happy-dom for units, Playwright + Chromium for e2e, MSW for the
network, TanStack Query, react-hook-form + zod, and `jest-axe` /
`@axe-core/playwright` for accessibility.

**Query/accessibility tool residue.** `F-QRY-01..07`'s behavioural rules
generalise to `kind-accessibility.md` (framework-agnostic: query priority,
accessible-name assertion, per-state scanning, association-not-presence). Only
the tool names stay here: `jest-axe` for unit-lane scans, `AxeBuilder` /
`@axe-core/playwright` for the one full-ruleset browser scan, `getByRole`/
`getByLabelText` as the concrete React Testing Library queries. `parent:` on
every rule in this file that specialises accessibility points at the
`kind-accessibility.md` id, not restated here.

## Runner and tooling facts
- **Commands:** `bun test <path>` to scope a lane; `bun run typecheck` (`tsc -b`); `bunx playwright test`.
- **Packages:** `@testing-library/react` + `user-event`; `msw`; `@testing-library/jest-dom`; `jest-axe`
  (unit) and `@axe-core/playwright` (e2e); zod for boundary parsing; Playwright + Chromium for e2e.
- **Blind spots:** happy-dom and jsdom have **no layout engine** — `getBoundingClientRect`,
  `scrollHeight`, `offsetWidth`, `clientHeight` and computed colour all return zero or a stub
  regardless of applied styles, and CSS `content:` does not exist under either shim (`T3-REACT-15`).
  `matchMedia` is a stub, so media queries, `:focus-visible` and `prefers-*` cannot be observed there
  either. bun's fake timers do not fake `Date` unless a shim is installed. MSW only intercepts what a
  handler covers — an unhandled request must fail loudly, or the "error" the test asserts on is a
  fallback render nobody meant to test.

## Which runner observes which behaviour

| Behaviour under test | Runner | Why that one, and not the cheaper one |
|---|---|---|
| A pure function, formatter, reducer, zod schema, query-key builder | **bun test** (no DOM) | Nothing renders. Fastest signal; put exhaustiveness and rejection cases here. |
| Component renders the right text/roles for given props | **bun + happy-dom** | The a11y tree exists in the shim; layout does not. Assert roles and names, never geometry. |
| A form: validation, wire body, absent-vs-zero, error recovery, input preserved | **bun + happy-dom + MSW + user-event** | The whole loop (typing → RHF/zod → wire → render) is DOM-only; no browser needed. |
| Data states: empty / loading / error / partial / success | **bun + happy-dom + MSW** | MSW makes each state a handler, deterministically. Use a latch, not a delay (`T3-REACT-08`). |
| A poll, debounce, retry cap, auto-dismiss | **bun + happy-dom + fake timers** | Deterministic under `advanceTimersByTime`; real time is flaky (`T3-REACT-11`). bun does not fake `Date` — install the shim. |
| Correct ARIA wiring, roles, names, duplicate ids, label association | **bun + jest-axe** (per state) | Static a11y rules need no layout; cheap enough to run on every state. |
| Contrast, target size, reflow, visible focus ring, full axe ruleset | **Playwright + @axe-core/playwright** | These rules need computed style and layout — they cannot fire under a shim. |
| Anything with a width, an overflow, a scroll container, a sticky header, a viewport breakpoint, a `content:` pseudo-element, a `color-mix()`/`oklab()` value | **Playwright (real Chromium)** | The shim has no layout engine at all; `getBoundingClientRect` is all-zero (`T3-REACT-15`). |
| Keyboard-only navigation through a composite widget | **Playwright**, or bun+user-event if the widget is shim-safe | Real focus/modality behaviour (`:focus-visible`) only exists in a browser; simple tab-order can stay in the shim. |
| Routing, deep links, reload persistence, unknown route, SPA fallback, lazy chunks | **Playwright e2e** | These involve the server's fallback config and real navigation; a memory router cannot see them. |
| Auth gating and redirect on 401 | **Playwright e2e** with a routed identity stub | Depends on real navigation; stub the identity endpoint, never a live backend. |
| A full user journey across ≥2 routes ending in a persisted change | **Playwright e2e** | The only layer that proves the pieces compose. Keep the set small (`T3-REACT-32`). |
| A generated file, wire shape, or exported type is current | **type test / `--check` script** | The compiler and a regeneration check are cheaper and stricter than any runtime test. |
| A type-level guarantee (exhaustiveness, no widening to `any`) | **type test** (`@ts-expect-error` / `expectTypeOf` / `never` default) | No runtime test can observe it (`T3-REACT-38`). |
| A design-system rule expressed in `className` strings | **bun source-scan guard** | Neither the browser nor the shim can see "no palette utility anywhere"; a source scan can. |
| Pixel-identical appearance | **Playwright screenshots — only under a pinned image** | Otherwise skip it and assert the computed property instead (`T3-REACT-34`, `T3-REACT-35`). |

**Playwright component testing is deliberately absent from this table.** It has
been experimental since 2022 with no stable timeline and a between-minor-version
API. Where a component genuinely needs a real browser, evaluate a Playwright e2e
spec against a route, or Vitest browser mode (stable from v4) instead — not
Playwright CT. This is a recorded design choice, not an oversight (invariant
`record-deliberate-design-choices`).

---

## Interaction & async

#### `T3-REACT-01` — Drive interaction with `user-event`, never `fireEvent`.
- *design:* `userEvent.setup()` per test; reach for `fireEvent` only where no user-event API exists, and say why.
- *review:* `fireEvent.click` dispatches one event; a real click is pointerdown/mousedown/focus/pointerup/mouseup/click, and only the full sequence exercises focus management and `:focus-visible`.
- *check:* `grep -rln "fireEvent" app --include="*.test.tsx"` — every hit needs a comment.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-SCO-03` · *absorbs:* `F-INT-01` · *cites:* kentcdodds.com common-mistakes-with-rtl (#12)

#### `T3-REACT-02` — `findBy*` instead of `waitFor(() => getBy*(...))`.
- *design:* prefer the built-in retrying query.
- *review:* identical semantics, a better failure message, one fewer place to hide a side effect.
- *check:* `grep -rn "waitFor(.*getBy\|waitFor(async" app --include="*.test.tsx"`.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-02` · *absorbs:* `F-INT-02` · *cites:* kentcdodds.com common-mistakes-with-rtl (#14)

#### `T3-REACT-03` — A `waitFor` callback contains exactly one assertion and no side effects.
- *design:* no `user.click`, no `server.use`, no mock setup inside the callback; never leave it empty.
- *review:* the callback runs repeatedly — a side effect fires an unknown number of times, and an empty callback waits for nothing.
- *check:* `grep -rn -A4 "waitFor(" app --include="*.test.tsx" | grep -n "user\.\|server\.use\|mock"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-02` · *absorbs:* `F-INT-03` · *cites:* kentcdodds.com common-mistakes-with-rtl (#15-17)

#### `T3-REACT-04` — `queryBy*` only for absence.
- *design:* `getBy*`/`findBy*` for presence; `queryBy*` only inside `expect(...).not.toBeInTheDocument()`.
- *review:* a `queryBy*` used for presence hides the better failure message `getBy*` would have given.
- *check:* `grep -rn "queryBy" app --include="*.test.tsx" | grep -v "not\.\|toBeNull\|toBeFalsy"`.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-01` · *absorbs:* `F-INT-05` · *cites:* kentcdodds.com common-mistakes-with-rtl (#13)

#### `T3-REACT-05` — `act()` appears only around timer advancement, never around render or user-event.
- *design:* `render`/`userEvent.*`/`fireEvent` are already act-wrapped; reach for manual `act` only for `advanceTimersByTime` or a promise chain driven from outside React.
- *review:* an act-wrapped render also silences the warning that would have told you a state update escaped.
- *check:* `grep -rn "act(" app --include="*.test.tsx"` — every hit must contain a timer call or a documented promise flush.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-ISO-03` · *absorbs:* `F-INT-06` · *cites:* kentcdodds.com common-mistakes-with-rtl (#6); fix-the-not-wrapped-in-act-warning

#### `T3-REACT-06` — An act warning fails the test run.
- *design:* configure the setup file to fail on `console.error`; scope any deliberate suppression to the one test that needs it.
- *review:* an act warning is a state update the test did not await — the exact shape of the stale-render and double-submit archetypes, and the loudest ignored signal in a React suite.
- *check:* does the setup file fail on `console.error`?
- **severity:** major · **evidence:** weak · **flag:** argued
- *parent:* `T1-ORC-01` · *absorbs:* `F-INT-07`

#### `T3-REACT-07` — Test the component, not the hook, unless the hook is itself a published unit.
- *design:* `renderHook` only for a hook that is an exported contract (a poll, a form adapter) consumed by more than one component; feature behaviour goes through the rendered component.
- *review:* a `renderHook` test asserts a return-value shape and cannot see that no component ever renders it.
- *check:* `grep -rln "renderHook" app --include="*.test.tsx"`; for each, is the hook exported and multiply consumed?
- **severity:** minor · **evidence:** weak · **flag:** argued
- *parent:* `T1-SCO-03` · *absorbs:* `F-INT-08` · *cites:* kentcdodds.com testing-trophy

#### `T3-REACT-08` — A double-submit / double-click guard has its own test asserting the exact count.
- *design:* prefer `toHaveBeenCalledOnce` / `toHaveBeenCalledExactlyOnceWith` over `toHaveBeenCalled()`.
- *review:* `toHaveBeenCalled()` passes for one call and for two — the archetype is a form that submits twice on a fast double click.
- *check:* `grep -rn "toHaveBeenCalled()" app --include="*.test.tsx"` on mutation spies.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-03` · *absorbs:* `F-INT-09` (archetype B09)

## Network & data

#### `T3-REACT-09` — Stub at the network boundary (MSW), never by mocking `fetch` or the API module.
- *design:* no `vi.mock("@/api/client")`, no `global.fetch = vi.fn()` for feature tests.
- *review:* a mocked module skips the real serialisation, error mapping and query key — the layers where wire bugs live.
- *check:* `grep -rn "mock.module\|stubGlobal(\"fetch\"\|mock(\"@/api" app --include="*.test.tsx"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-DBL-03` · *absorbs:* `F-NET-01` · *cites:* mswjs.io best-practices

#### `T3-REACT-10` — Unhandled requests are an error, not a warning.
- *design:* `server.listen({ onUnhandledRequest: "error" })`; reset handlers after every test.
- *review:* a silently unhandled request turns into a fallback render the test then asserts on — a green test for a screen the user never sees.
- *check:* `grep -n "onUnhandledRequest\|resetHandlers"` in the test setup file.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-PRF-01` · *absorbs:* `F-NET-02` · *cites:* mswjs.io best-practices

#### `T3-REACT-11` — A default handler exists so the fallback is deliberate, and is itself schema-checked.
- *design:* every global default handler's body is parsed by the same schema production uses, pinned by its own test.
- *review:* a hand-written default that no schema validates drifts from the API, and every test built on it becomes fiction.
- *check:* for each default handler, is there a `schema.parse(...)` test?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ORC-02` · *absorbs:* `F-NET-03`

#### `T3-REACT-12` — Assert the effect on the UI, and separately assert the request body — never only that the request fired.
- *design:* one test may do both; "the request fired" alone is not a test.
- *review:* the wire shape has its own defect class (a `0` sent where the field should be absent) distinct from the UI effect.
- *check:* look for tests whose only assertion is on a request spy.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ORC-07` · *absorbs:* `F-NET-04` · *cites:* mswjs.io best-practices

#### `T3-REACT-13` — Route/URL literals are generated, and the generator is verified in CI with a `--check` mode.
- *design:* no string-literal API paths in production code; the generated file's currency is proven, not assumed.
- *review:* a backend rename should break the build, not a runtime call — the derive-or-delete invariant applies to routes too.
- *check:* `grep -rn "\"/api/\|\`\${API_BASE}" app --include="*.ts*" | grep -v mocks|test`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-06` · *absorbs:* `F-NET-06`

#### `T3-REACT-14` — A fresh query client per test, retries off, and no shared cache across tests.
- *design:* build a fresh `QueryClient` per test with `retry: false`.
- *review:* a leaked cache makes test N pass on data test N-1 fetched; default retry backoff turns an error-state test into a timeout.
- *check:* `grep -n "retry\|new QueryClient\|createQueryClient"` in the render helper.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-05` · *absorbs:* `F-NET-07` · *cites:* TanStack Query testing docs

## Time & animation

#### `T3-REACT-15` — Never assert appearance, layout, or overflow under happy-dom/jsdom.
- *design:* no `getBoundingClientRect`, `scrollHeight`, `offsetWidth`, `clientHeight`, computed-colour or `content:` assertion in the unit suite.
- *review:* neither shim has a layout engine; these calls return zero or a stub regardless of applied styles.
- *check:* `grep -rn "getBoundingClientRect\|scrollHeight\|offsetWidth\|clientHeight\|getComputedStyle" app --include="*.test.ts*"`.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-05` · *absorbs:* `F-ENV-01` · *cites:* happy-dom #1416; jsdom #1504

#### `T3-REACT-16` — A class-name assertion is named as a contract test, not implied to be a visual one.
- *design:* name the test for the class contract ("renders the wide-dialog class"), and pair it with a browser test asserting the resulting geometry.
- *review:* conflating the class contract with the rendered result is how a green suite certifies a broken screen.
- *check:* `grep -rn "toHaveClass" app --include="*.test.tsx"` — is there a matching e2e assertion for each visual-intent class?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-CST-05` · *absorbs:* `F-ENV-02`

#### `T3-REACT-17` — Media queries, `focus-visible`, `prefers-*` and scroll behaviour are browser-only.
- *design:* test responsive breakpoints, reduced-motion and focus-ring visibility in Playwright with a set viewport, never in the shim.
- *review:* `matchMedia` under happy-dom is a stub; `:focus-visible` has no heuristic without a real input modality.
- *check:* `grep -rn "matchMedia" app --include="*.test.ts*"`; does any e2e spec set a viewport?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-CST-05` · *absorbs:* `F-ENV-03`

#### `T3-REACT-18` — The DOM-shim bootstrap is itself tested, or at minimum documented as load-bearing.
- *design:* record preload order and which intrinsics are kept from the host realm where the next person will read them.
- *review:* a wrong bootstrap fails as mystery assertion errors, not as a clear environment error — e.g. `instanceof RegExp` silently breaking if a host intrinsic is replaced.
- *check:* does the preload order match the bootstrap file's own comment block?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-CST-01` · *absorbs:* `F-ENV-04`

#### `T3-REACT-19` — Per-file isolation is on, and shared globals are cleared after every test.
- *design:* reset storage, mocks, env stubs and the DOM in a global `afterEach`; run the suite with per-file isolation.
- *review:* with a shared `window`, `localStorage` written by one test outlives it.
- *check:* `grep -n "localStorage.clear\|cleanup()\|restoreAllMocks\|unstubAll"` in the setup file; `grep -n "parallel"` in the package config.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-05` · *absorbs:* `F-ENV-05`

#### `T3-REACT-20` — A skipped test carries the crash and the re-enable condition inline.
- *design:* pair `it.skip`/`todo` with the upstream defect and the version that lifts it; re-enabling is part of that upgrade, not a follow-up.
- *review:* an unannotated skip is unverified code with no path back to verified.
- *check:* `grep -rn "\.skip\|todo(" app --include="*.test.ts*"` — every hit needs a note.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-STR-03` · *absorbs:* `F-ENV-06`

#### `T3-REACT-21` — Time-dependent behaviour is driven by a fake clock, in the runner that owns one.
- *design:* units: `vi.useFakeTimers()` + `advanceTimersByTime` inside `act`. E2E: `page.clock.install()` / `setFixedTime` / `fastForward`. Never a real sleep.
- *review:* a poll, a debounce, a toast auto-dismiss and an idle-logout are deterministic under a fake clock and flaky under a real one.
- *check:* `grep -rn "waitForTimeout\|setTimeout(.*resolve" e2e` (must be zero); `grep -rn "page.clock" e2e`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-03` · *absorbs:* `F-TIME-02` · *cites:* playwright.dev/docs/clock

#### `T3-REACT-22` — Fake timers are restored unconditionally, in a shared `afterEach`.
- *design:* put `useRealTimers()` in the global `afterEach`, not only per-file cleanup.
- *review:* a test that arms fake timers and throws before restoring leaves them armed for every later file in that worker — the leak stalls timer-driven updates and gets misdiagnosed as machine contention.
- *check:* `grep -n "useRealTimers"` in the setup file.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-05` · *absorbs:* `F-TIME-03`

#### `T3-REACT-23` — Animation is disabled in any test that asserts appearance or position.
- *design:* set `animations: "disabled"` and `caret: "hide"` for e2e screenshot and geometry assertions.
- *review:* animations are the single largest source of visual flake — a spinner mid-rotation, a fade at 50%, a menu halfway through a slide.
- *check:* `grep -n "animations"` in the Playwright config.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-01` · *absorbs:* `F-TIME-05` · *cites:* playwright.dev/docs/test-snapshots

## E2E discipline

#### `T3-REACT-24` — Web-first assertions only.
- *design:* `await expect(locator).toBeVisible()`, never `expect(await locator.isVisible()).toBe(true)`.
- *review:* only the web-first form waits and retries; the manual form is a race written as an assertion.
- *check:* `grep -rn "expect(await .*\.\(isVisible\|textContent\|count\)()" e2e`.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T2-ETE-01` · *absorbs:* `F-E2E-01` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-25` — No `waitForTimeout`, and no `networkidle` as a readiness signal.
- *design:* wait on a locator or a response, never on a duration or the network going quiet.
- *review:* `networkidle` is discouraged by Playwright itself and is unreliable with polling, websockets or a refetching query client.
- *check:* `grep -rn "waitForTimeout\|networkidle" e2e`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-02` · *absorbs:* `F-E2E-02` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-26` — Locate by user-visible role/label, not CSS or XPath.
- *design:* prefer `getByRole`/`getByLabel` over `locator("#id")` or an XPath.
- *review:* compare `getByRole` count to `getByTestId` count per spec file — a heavy test-id lean is a finding.
- *check:* `grep -rn "locator(\"\.\|locator(\"#\|//\*\[" e2e`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T2-ACC-01` · *absorbs:* `F-E2E-03` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-27` — Each spec is fully isolated: its own context, its own stubs, no ordering dependency.
- *design:* no shared logged-in state mutated across tests; no test that only passes after another one ran.
- *review:* run with `--repeat-each=2` and a shuffled order; look for module-level mutable state in the fixtures directory.
- *check:* the repeat/shuffle run above.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ISO-05` · *absorbs:* `F-E2E-04` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-28` — Third-party and backend calls are stubbed at the route level.
- *design:* `page.route` for the API and for auth; e2e never depends on a live backend or an external site.
- *review:* an e2e suite bound to a real backend measures the backend's availability, not the frontend's correctness.
- *check:* `grep -rn "page.route" e2e | wc -l`; confirm an auth fixture exists.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-ISO-07` · *absorbs:* `F-E2E-05` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-29` — Trace on first retry; retries are bounded and are not a flake-hiding device.
- *design:* `trace: "on-first-retry"`, `retries` ≤ 1-2; a spec that only passes on retry is filed as a defect, not accepted.
- *review:* trace-never makes a CI failure undiagnosable; retries without triage convert a real race into permanent noise.
- *check:* `grep -n "trace\|retries"` in the Playwright config; are flaky specs tracked as issues?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-ISO-08` · *absorbs:* `F-E2E-06` · *cites:* playwright.dev/docs/best-practices

#### `T3-REACT-30` — E2E covers journeys, not fields.
- *design:* keep the e2e suite to the smallest set of full user journeys that exercise routing, persistence, auth gating and the error page; push field-level validation and copy to the unit suite.
- *review:* count e2e specs asserting a single field's format or a single label's text — each is a demotion candidate.
- *check:* the count above.
- **severity:** minor · **evidence:** weak · **flag:** argued
- *parent:* `T1-CST-02` · *absorbs:* `F-E2E-07` · *cites:* kentcdodds.com testing-trophy

#### `T3-REACT-31` — Routing, unknown routes and the error boundary each have an e2e test.
- *design:* deep-link into every top-level route, reload on a nested route, hit an unknown route, force a render throw.
- *review:* these break in ways only a real navigation reproduces (SPA fallback config, lazy-chunk loading, boundary placement).
- *check:* `ls e2e | grep -i "route\|unknown\|error-boundary"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-SCO-05` · *absorbs:* `F-E2E-08` (archetype B10)

#### `T3-REACT-32` — Assert typed lint on the e2e sources.
- *design:* enable `@typescript-eslint/no-floating-promises` for `e2e/**`, and include the e2e tsconfig in the typecheck build.
- *review:* a missing `await` on a Playwright call is a test that asserts nothing and passes; it is invisible without this rule.
- *check:* `grep -n "no-floating-promises"` in the eslint config.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-PRF-01` · *absorbs:* `F-E2E-09`

## Snapshots & visual

#### `T3-REACT-33` — No DOM snapshot as a behavioural assertion.
- *design:* assert the specific fact instead of `toMatchSnapshot()` over rendered markup.
- *review:* a snapshot diff cannot say which change was the defect, so the reviewer accepts all of them.
- *check:* `grep -rn "toMatchSnapshot\|toMatchInlineSnapshot" app`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T2-SNAP-06` · *absorbs:* `F-SNAP-01` · *cites:* kentcdodds.com testing-trophy

#### `T3-REACT-34` — Snapshot only stable, non-visual, serialised artifacts.
- *design:* legitimate uses: a generated file, a wire body, an error map — never a component tree.
- *review:* argued per snapshot site.
- *check:* for a snapshot call over a rendered component tree, is it capturing markup structure rather than a stable serialised artifact (a generated file, wire body, error map)? Evidence: `grep -B3 'toMatchSnapshot' app` — a snapshot argument built from a `render()`/mount result is the violation.
- **severity:** minor · **evidence:** strong · **flag:** argued
- *parent:* `T2-SNAP-01` · *absorbs:* `F-SNAP-02`

#### `T3-REACT-35` — A pixel screenshot baseline is only worth it under a pinned rendering environment.
- *design:* `toHaveScreenshot` needs a fixed OS/browser/font stack (a container image), `animations: "disabled"`, and a stated `maxDiffPixelRatio` — without all three, don't add it.
- *review:* an unpinned baseline generates a permanent red that gets blindly `--update-snapshots`-ed away, which is worse than no visual test.
- *check:* `grep -rn "toHaveScreenshot" e2e` — if present, confirm a pinned image in the CI workflow.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T2-SNAP-01` · *absorbs:* `F-SNAP-03` · *cites:* playwright.dev/docs/test-snapshots

#### `T3-REACT-36` — Prefer a computed, semantic visual assertion over a pixel diff.
- *design:* assert the property that carries the design intent — contrast ratio, box-inside-viewport, `scrollHeight` vs `clientHeight` — measured in the real browser.
- *review:* it survives a legitimate restyle where a pixel baseline does not; a `color-mix()`/`oklab()` value can serialize such that a regex over rgb components silently reads zeros.
- *check:* for each design-intent claim, is there a computed assertion in e2e?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T2-SNAP-01` · *absorbs:* `F-SNAP-04`

#### `T3-REACT-37` — A design-system rule that lives in `className` strings is guarded by a source scan that first proves it scans something.
- *design:* scan component sources for the palette/token/vocabulary rule, since a CSS-file-only guard cannot see rules expressed in JSX class strings.
- *review:* per the universal vacuity rule, the scan must assert a non-zero matched-file count first — a matcher that finds nothing reports "0 drift" while inspecting zero files.
- *check:* does the guard test assert a non-zero scanned-file count?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-PRF-03` · *absorbs:* `F-SNAP-05`

## Types

#### `T3-REACT-38` — The typechecker is part of the gate, and it is green before the suite is trusted.
- *design:* run `tsc -b` in the same lane as the tests.
- *review:* a green runtime suite cannot substitute for the compiler — a wrong `.d.ts` matcher signature can pass every test and fail typecheck.
- *check:* `grep -n "typecheck"` in the package scripts; confirm the CI lane runs it.
- **severity:** blocker · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-PRF-02` · *absorbs:* `F-TYPE-01` · *cites:* `typescript.instructions.md`

#### `T3-REACT-39` — Wire types are derived from a runtime schema, and parsing happens at the boundary.
- *design:* zod (or equivalent) at the edge; infer TS types from the schema, never maintain both by hand.
- *review:* a hand-maintained parallel type is the derive-or-delete failure — it drifts silently and the compiler certifies the drift.
- *check:* `grep -rn "z.infer\|z.object" app --include="*.schema.ts" | wc -l` against hand-written wire interfaces.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-06` · *absorbs:* `F-TYPE-02` · *cites:* `typescript.instructions.md`

#### `T3-REACT-40` — A contract-parity test proves the schema rejects the wrong shape, not only that it accepts the right one.
- *design:* for every discriminated union or constrained enum, assert `expect(() => schema.parse({...bad})).toThrow()`.
- *review:* a schema that accepts everything passes every acceptance test — a rejection test is the falsifier.
- *check:* `grep -rn "toThrow()" app --include="*.parity.test.ts" --include="*.schema.test.ts"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T2-CTR-06` · *absorbs:* `F-TYPE-03`

#### `T3-REACT-41` — Type-level expectations are asserted, not assumed.
- *design:* pin a type-is-the-deliverable claim with `@ts-expect-error`, `expectTypeOf`/`tsd`, or an exhaustive `switch` with a `never` default.
- *review:* without one, a type widening to `any` breaks nothing and is caught by nothing.
- *check:* `grep -rn "@ts-expect-error\|expectTypeOf\|: never" app --include="*.ts*"`.
- **severity:** minor · **evidence:** weak · **flag:** argued
- *parent:* `T1-ORC-01` · *absorbs:* `F-TYPE-04` · *cites:* `typescript.instructions.md`

#### `T3-REACT-42` — `any` and non-null assertions do not appear in test code either.
- *design:* a cast in a test can make an assertion accept a shape production would reject — apply the same discipline as production code.
- *review:* argued per hit.
- *check:* `grep -rn ": any\|as any\|!\." app --include="*.test.ts*"`.
- **severity:** minor · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-STR-02` · *absorbs:* `F-TYPE-05` · *cites:* `typescript.instructions.md`
