# kind-bdd

Applies to a Gherkin/Cucumber/Reqnroll feature file and its step definitions. The runner is the
BDD framework itself, but the finding this kind exists to catch is upstream of any runner: whether
the scenario was written *before* the code as a captured conversation, or *after* it as test
automation wearing Gherkin's syntax. `T2-BDD-09` is this kind's instance of `T1-STR-03` for a
dormant scenario specifically — see `parent:` for the full body.

**`T2-BDD-01` — RETIRED 2026-08-22: evidence withdrawn — cross-role authorship isn't diff-observable; the cited authority is one source, not two independent ones.**
- *design:* n/a — retired.
- *review:* n/a — retired.
- *check:* retired — not applicable.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-01`
- *retired:* 2026-08-22 — no diff-checkable falsifier; the cited authority is one source (Cucumber docs and its author), not two independent ones; see `governance.md` "Retiring a rule".
- *cites:* Cucumber docs; Hellesøy "world's most misunderstood collaboration tool".
- *meta:* pass=3 order=14

**`T2-BDD-02` — RETIRED 2026-08-22: evidence withdrawn — duplicates `T2-BDD-03`'s diff-checkable falsifier (scenario-vs-implementation commit order) with no independent signal of its own.**
- *design:* n/a — retired.
- *review:* n/a — retired.
- *check:* retired — not applicable.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-01`
- *retired:* 2026-08-22 — subsumed by `T2-BDD-03`'s falsifier; see `governance.md` "Retiring a rule".
- *cites:* Cucumber docs Myths; Dan North via Liz Keogh.
- *meta:* pass=3 order=14

**`T2-BDD-03` — Writing or automating a scenario before the production code exists is what makes it BDD; writing scenarios after the code is done is test automation wearing Gherkin.**
- *design:* write the scenario, watch it fail for the right reason, then write the code that turns it green.
- *review:* a feature file added after a PR's implementation commit is, by definition, not the practice its syntax implies.
- *check:* argued — compare the scenario's commit date against the implementation's.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-01`
- *cites:* Cucumber docs Myths; Hellesøy.
- *meta:* pass=8 order=30

**`T2-BDD-04` — RETIRED 2026-08-22: evidence withdrawn — discovery-session attendance and role composition are not observable from a diff.**
- *design:* n/a — retired.
- *review:* n/a — retired.
- *check:* retired — not applicable.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-01`
- *retired:* 2026-08-22 — no diff-checkable falsifier for a real-world discovery-composition claim; see `governance.md` "Retiring a rule".
- *cites:* Cucumber docs (who-does-what).
- *meta:* pass=3 order=14

**`T2-BDD-05` — Write Gherkin declaratively — describe behaviour, not implementation.**
- *design:* apply the test "will this wording need to change if the implementation does?" — rewrite if yes.
- *review:* a step naming a button, a field, or a URL is implementation coupling wearing Gherkin's syntax; it is the same defect `T1-SCO-03` names for code.
- *check:* does a step's text name a UI element, selector, URL, or button label instead of describing behaviour? Evidence: grep the `.feature` file for `button`, `click`, a CSS selector, or a literal URL inside step text — any hit is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-SCO-03`
- *cites:* Cucumber docs (better-gherkin).
- *meta:* pass=3 order=15

**`T2-BDD-06` — Push interaction mechanics down into step definitions; keep the scenario text implementation-agnostic.**
- *design:* the scenario names the behaviour; the step definition owns which button, which field, which selector.
- *review:* mechanics in the scenario text is the same coupling `T2-BDD-05` names, at the syntax level rather than the wording level.
- *check:* `grep -rn 'button\|selector\|css\|xpath'` against the `.feature` file's own step text (not the step-definition body) — interaction mechanics leaking back into the scenario text is the violation.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-SCO-03`
- *cites:* Cucumber docs (better-gherkin).
- *meta:* pass=3 order=15

**`T2-BDD-07` — BDD/Gherkin pays off when scenarios stay tied to explicit business goals, not just feature-level examples.**
- *design:* trace each feature file back to a stated business goal (e.g. via Impact Mapping) rather than letting it float as an isolated example set.
- *review:* a feature with no traceable business goal is a candidate for demotion to a cheaper test.
- *check:* does the feature file's header or the linking PR/issue name a business goal it traces to? Evidence: read the `Feature:` description block — no stated goal and no PR/issue link is the violation.
- *rationale:* weak — search-derived summary, not a first-party primary source; caps at `major`.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-CST-02`
- *cites:* Gojko Adzic, Impact Mapping.
- *meta:* pass=7 order=29

**`T2-BDD-08` — RETIRED 2026-08-22: evidence withdrawn — team-composition-over-time is a team-process opinion with no diff-checkable falsifier.**
- *design:* n/a — retired.
- *review:* n/a — retired.
- *check:* retired — not applicable.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-STR-01`
- *retired:* 2026-08-22 — an opinion about team composition over a project's lifetime, not a rule with a checkable falsifier; see `governance.md` "Retiring a rule".
- *cites:* Cucumber docs (who-does-what).
- *meta:* pass=7 order=28

**`T2-BDD-09` — A dormant (`@ignore`/skipped) scenario's step bindings are unverified code; un-ignoring is a required step of the change that lifts the block, not a follow-up.**
- *design:* when re-enabling a scenario, run it and fix what it finds in the same change — never merge the un-skip alone.
- *review:* bindings written while a scenario was ignored have never executed and are frequently subtly broken in ways only running reveals.
- *check:* auto — a diff that flips `@ignore`/`Skip` with no other change in the same PR.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-STR-03`
- *cites:* ai-badger `dotnet-bdd-testing/SKILL.md`; ruling C11.
- *meta:* pass=7 order=28
