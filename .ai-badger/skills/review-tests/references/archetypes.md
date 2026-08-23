# Bug archetype catalogue (18)

The benchmark seed table. Every row is stated as an **injectable mutation** — that is what makes
it a seed rather than a worry, and what `design-tests` Step 2 reads as its checklist ("which of
the 18 can this thing suffer?"). Merges L3a §6 (`B1`–`B10`) and L3b §3 (`B01`–`B10`); four pairs
were the same archetype at different layers and are merged; `A17`/`A18` are present in memories
but in neither lane list, so they are added here directly.

| id | Archetype | Layer | Rule demanding the guard | Proof mutation | Runner that can see it |
|---|---|---|---|---|---|
| A01 | Boundary off-by-one (incl. pagination: page 2 repeats page 1's last item; last page unreachable on an exact multiple) | domain / API / UI | `T1-ORC-02`, `T2-UNIT-03` | flip `>=`↔`>`; flip `(page-1)*size`→`page*size` | unit; unit+network-stub for the requested offset |
| A02 | Null / empty / single-element collection (`.First()` on empty → 500; a duplicate matcher with one element per side) | domain | `T1-STR-02` | delete the empty guard; replace `.Single()` with `.First()` | unit |
| A03 | Timezone / DST shift (a date-only value through a UTC-midnight `Date`; a duration spanning a DST transition; `Kind.Unspecified` round-tripped) | domain / UI | `T1-ISO-04`, `T2-TIME-01` | swap `GetUtcNow()`→`GetLocalNow()`; render a date-only string via `toLocaleDateString()` | unit **with a pinned clock and a non-UTC TZ** |
| A04 | Culture-sensitive parse/format (`decimal.Parse("1,5")`; Turkish-locale case compare) | domain | `T1-ISO-04` | delete the `IFormatProvider` argument | unit under a comma-decimal culture |
| A05 | Illegal state transition accepted (an interview added to a pre-interview application) | domain | `T1-SCO-04`, `T1-SCO-05` | delete the guard | unit |
| A06 | Transition without its companion side effect — the state moves, the record the state's meaning requires is never written | domain | `T1-SCO-04`, `T2-ARCH-06` | delete the companion write | unit — **only if the test asserts the aggregate, not the flag** |
| A07 | Race on shared state (two writers; check-then-act; an orchestration scheduled *before* the write it depends on, whose early-return then **reports success having done nothing**) | infra | `T1-DBL-02`, `T1-ISO-01` | remove the concurrency token or the lock | integration with N concurrent ops asserting the invariant, never the timing |
| A08 | Stale read after a newer write — the slower first response resolves last and overwrites the newer one | UI / client | `T2-ETE-01`, `T1-ISO-02` | remove the abort / query-key discrimination | unit + network stub with **two latches**: start A, start B, release B, release A |
| A09 | Over-broad catch / swallowed failure (a bare `catch`, a widened filter, a handler logging "skipped" after it had already acted) | any | `T1-ORC-05` | broaden the filter to the bare type; delete the error branch | unit — a negative test throwing a *different* instance of the same type through the same path |
| A10 | Unbounded retry (no attempt cap, no ignore-count, a permanently-failing activity retried forever) | infra | `T1-ORC-03`, invariant `bounded-retry-loops` | raise the cap to `int.MaxValue` | unit + fake clock asserting the **attempt count**, never elapsed time |
| A11 | Idempotency violated / double submit (replay creates a duplicate; a second poll pass re-shares; the submit button is not disabled while in flight) | domain / UI | `T1-ORC-03`, `T1-DBL-02` | delete the dedupe key check; remove `disabled={isPending}` | unit + latch, asserting **exactly once** |
| A12 | Partial write on failure / on cancellation (the second of two writes throws; a cancelled request leaves a half-built entity) | domain | `T1-SCO-04` | remove the transaction/compensation | unit with a fake throwing on the **second** write — the first is the safe direction |
| A13 | Stale closure / missing dependency (a handler captures a value at mount; a memo omits a dep and keeps showing the previous computation) | UI | `T2-TIME-04`, `T1-ORC-07` | drop an entry from a dep array; capture `props.id` in a once-declared interval | unit + fake timers — **change the prop, then advance**, and assert the new id |
| A14 | Broken ARIA association — the message renders but `aria-describedby`/`htmlFor` points at a stale or absent id | UI | `T2-ACC-07`, `T2-ACC-03` | change one id suffix | DOM shim + label/description queries + an axe scan |
| A15 | Keyboard path broken while the mouse path works (`<div onClick>`; a dialog stops trapping focus; a dropped `onKeyDown`) | UI | `T2-ACC-01`, `T2-ACC-06` | remove `tabIndex`/`onKeyDown`; swap `<button>` for `<div>` | keyboard-only test; a real browser for `:focus-visible` and focus order |
| A16 | Unreachable error state — the branch exists but no code path reaches it (caught upstream, flag on different state, boundary on the wrong route) | UI / API | `T1-SCO-05` | narrow the error predicate until the branch is dead | a test per reachable state; e2e forcing a render throw |
| A17 | **Inert rule** — a filter/allowlist/predicate is logically correct and nothing in production populates its input | any | `T1-SCO-06` | **unwire the input**, not the rule — the rule's own tests stay green either way | a test driving the real production input-building path |
| A18 | **Vacuous gate** — an architecture rule, scan or scoped quality run whose subject set is empty and which therefore passes | gate | `T1-PRF-03`, `T2-ARCH-01` | plant a real violation; separately, point the scope at a directory with no matching files | the gate itself, run against the planted violation |

**Coverage of L1 by archetype.** Every `blocker` in `universal.md` except `T1-CST-03`,
`T1-CST-05`, `T1-ISO-06` and `T1-PRF-01` has at least one archetype above. Those four are
suite-level or process-level and are measured by the benchmark's harness rather than by a seeded
defect — stated here rather than inventing a defect for them.

**Benchmark note.** A17 and A18 are the two defect classes the installed test-quality tooling
cannot see at all — `dotnet-test:test-gap-analysis` mutates *production expressions*, and both
survive every production mutation because the production code is correct; what is broken is what
feeds it. Per the synthesis's ruling on held-out scope, A17/A18 are shipped as rules and run in the
benchmark, but scored into their own `detect_enumerated` column — never pooled into the ranking
key — because this ruleset itself enumerates them, and seeding them in the scored set would
measure whether the skill can read its own checklist rather than whether it can find a bug.
