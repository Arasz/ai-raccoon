# kind-end-to-end

Applies to a browser-driven journey test (Playwright, Cypress, or equivalent) that exercises the
real app shell — routing, persistence across reload, the error boundary. The runner is the real
browser, which is exactly why this kind is the most expensive lane and the rules below exist to
keep it spent only where nothing cheaper reaches (`T1-CST-02`) and never blind to its own races
(`T1-ISO-02`). Tool-specific API names (`waitForTimeout`, `page.route`, `AxeBuilder`) live in the
stack file; these are the framework-agnostic forms.

*Deliberate note: this file's own filename and rule-id group avoid the digit in "e2e"
(`kind-end-to-end.md`, `T2-ETE-*`) rather than repeat V3's dangling-citation defect — see the W1b
report for the full reasoning and the cross-lane fix this implies for `archetypes.md`.*

**`T2-ETE-01` — Web-first, retrying assertions only.**
- *design:* assert on a locator's own retrying matcher (`toBeVisible`, `toHaveText`), never a manual read-then-compare.
- *review:* only the web-first form waits and retries; the manual form is a race written as an assertion.
- *check:* auto — a manual boolean/text read compared outside the assertion call.
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-02`
- *meta:* pass=2 order=11

**`T2-ETE-02` — No duration-based wait, and no network-quiet signal, as a readiness check.**
- *design:* wait on a locator or a response, never on a fixed duration or on the network going idle.
- *review:* a duration wait is a wall-clock assertion in disguise; a network-quiet signal is unreliable with polling, websockets, or a refetching client.
- *check:* `grep -rn 'waitForTimeout\|networkidle' <e2e dir>` — any hit is the violation; the fix is a locator- or response-based wait.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-02`
- *meta:* pass=2 order=11

**`T2-ETE-03` — Locate by user-visible role or label, not CSS or XPath.**
- *design:* prefer a role/label-based locator; reach for a structural selector only when no accessible one exists.
- *review:* role/label is the same surface a screen reader uses; CSS/XPath locates the implementation, which is `T1-SCO-03`'s coupling defect at the locator layer.
- *check:* auto-unless-listed — count structural selectors against role/label locators.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-SCO-03`
- *meta:* pass=3 order=15

**`T2-ETE-04` — Each spec is fully isolated: its own context, its own stubs, no ordering dependency.**
- *design:* give every spec a fresh browser context and its own stub setup; never mutate shared logged-in state across specs.
- *review:* run shuffled and with each spec repeated — a spec that only passes after another one ran is this kind's instance of `T1-ISO-05`.
- *check:* run the suite shuffled and with each spec repeated in isolation — does any spec fail alone, or pass only after another one ran first? Evidence: the shuffled/isolated re-run's pass/fail delta against the normal run.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-ISO-05`
- *meta:* pass=2 order=11

**`T2-ETE-05` — Third-party and backend calls are stubbed at the route/transport level.**
- *design:* intercept at the network boundary for every external and backend call; never depend on a live backend or external site.
- *review:* an e2e suite bound to a real backend measures the backend's availability, not the frontend's correctness — this is `T1-DBL-03`'s lowest-boundary rule applied to the browser lane.
- *check:* auto — count of route-level intercepts against external/backend call sites.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-DBL-03`
- *meta:* pass=5 order=21

**`T2-ETE-06` — Trace on first retry; retries are bounded and are not a flake-hiding device.**
- *design:* configure a trace capture on the first retry, cap retries at one or two, and file a defect for any spec that only passes on retry.
- *review:* retry is a diagnostic (a trace on first retry); quarantine with an expiry is the disposition — retries never substitute for it (ruling C18).
- *check:* does the runner config cap `retries` at 1–2 and set a first-retry trace capture? Evidence: `grep -n 'retries\|trace' <playwright config>` — no cap, or no first-retry trace, is the violation; a spec passing only under retry with no filed defect id is the same finding.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-ISO-08`
- *cites:* ruling C18.
- *meta:* pass=2 order=12

**`T2-ETE-07` — E2E covers journeys, not fields.**
- *design:* keep the e2e suite to the smallest set of full journeys exercising routing, persistence, auth gating and the error page; push field-level validation to the unit suite.
- *review:* e2e is the most expensive confidence available — spending it on a single field's format is buying a container for nothing (`T1-CST-02`).
- *check:* argued — count specs asserting a single field's format or a single label's text.
- *rationale:* weak — testing-trophy cost-curve argument, not a measured local instance; caps at `minor` per source severity.
- **severity:** minor · **evidence:** weak · **flag:** argued · **parent:** `T1-CST-02`
- *meta:* pass=7 order=29

**`T2-ETE-08` — Routing, unknown routes, and the error boundary each have an e2e test.**
- *design:* deep-link into every top-level route, reload on a nested one, hit an unknown route, and force a render throw.
- *review:* these are states a user reaches by accident, and each is `T1-SCO-05`'s reachable-state rule at the journey layer.
- *check:* auto-unless-listed — do route/unknown-route/error-boundary specs exist?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-SCO-05`
- *meta:* pass=6 order=24

**`T2-ETE-09` — E2E sources carry a typed lint that fails on a missing `await`.**
- *design:* enable a floating-promise lint rule scoped to the e2e source tree, and keep it in the typecheck build.
- *review:* a missing `await` on a driver call is a test that asserts nothing and passes — invisible without this rule, and the same defect class as `T1-ORC-01`'s zero-assertion body.
- *check:* auto — is the floating-promise rule enabled for the e2e path, and is that path in the typecheck build?
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ORC-01`
- *meta:* pass=1 order=5
