# Governance — id scheme, adding/retiring a rule, stack extension, L4

## Id scheme and the L1 cap

Ids are `T<layer>-<GROUP|KIND|STACK>-<nn>`, layer 0–4, allocated once, never reused, never
renumbered; retirement writes an in-place tombstone (below). Project-local rules use the `TX-`
prefix (§L4).

**L1 is capped at 40, not fixed at 40.** `scripts/rules_index.py --check` fails when the live
(non-tombstoned) L1 count in `universal.md` *exceeds* 40. It does not assert equality. "Exactly
40" is a hand-maintained number that goes stale the moment a rule is retired — a tombstone reduces
the live count and an equality assertion would go red for the *right* change. A cap is a rule; an
exact count is a mirror, and `derive-or-delete-the-list` is explicit about which of those survives.

Lane ids from the source lanes (`U-*`, `K-*`, `N-*`, `F-*`, `P1..P8`) survive **only** inside
`absorbs:` fields. That is what makes the consolidation auditable and lets a reviewer holding
`U-ASR-13` resolve it to `T1-ORC-07`.

An `absorbs:` entry is validated by shape, not against a hand-maintained list: it must match the
source-lane id grammar (`[UKNF]-[A-Z]{2,4}-\d{2}` or `P[1-8]`) or resolve to a known rule id — the
checker derives nothing from a hand list.

## Adding a rule

A candidate becomes a rule only with all five:

1. **One sentence, imperative.** If it needs a paragraph to state, it is two rules or a principle.
2. **A check someone who did not write it can run** — a command, a grep with a stated exception
   class, or a question plus how to settle it. No check → it is L0, or it is not a rule.
3. **Evidence.** Either (a) a **proven failure**: a green check that missed a real defect, or a
   planted defect that survived, cited to a file/memory/PR with a date — this is the "proven by a
   failure" bar, and the only route to `strong`; or (b) **two or more independent authorities** —
   first-party docs or peer-level sources, not two blogs quoting each other. Anything else is
   `weak`, which caps severity at `major` and forces `flag: argued` (see `universal.md`'s field
   key).
4. **A layer, decided by a mechanical test**, not by taste:
   - the check names a tool, package, runner or API → **L3** (`stack-*.md`)
   - the check only makes sense once you know the test's kind → **L2** (`kind-*.md`)
   - neither → **L1** (`universal.md`)
   - it has no check → **L0** (`principles.md`), and it needs a conflict it settles or it is cut
   - it names this project's own incident and nothing else → **L4** (§L4 below)
5. **A `parent:` if it is L2 or L3.** A stack or kind rule with no parent is either a new universal
   rule (promote it and give it a parent-less L1 id) or a tooling note, which belongs in a skill
   body, not in the ruleset. This is the rule that stops a stack file becoming a cookbook.

Plus one deletion test, run before the addition: **does an ai-badger invariant already say this?**
If yes, the rule is not written; the nearest existing rule gains a `cites:` line instead. Same for
an installed plugin's own catalogue — cite `dotnet-test:detect-static-dependencies`, do not
re-derive its list of statics.

## Retiring a rule

- Ids are never reused and never renumbered. Retirement writes a tombstone in place:
  `T1-XXX-nn — RETIRED <date>: superseded by T1-YYY-mm because <one line>.`
- The tombstone stays in the index, so a stale citation in an old PR resolves to an explanation
  rather than to nothing.
- A rule is retired when its defect class becomes unrepresentable (the language, framework or a
  type-level fix removed it), when it is absorbed, or when its evidence is withdrawn. **Never**
  because it is inconvenient or because a project has many violations — that is an L4 exception
  with a written reason, and the exception itself expires (§L4).
- **Evidence decay.** Every `strong` rule citing a measurement carries the measurement's date. A
  figure older than six months is quoted as "measured `<date>`", never as current (`T1-PRF-02`). A
  rule whose *only* evidence is a measurement that no longer reproduces is demoted to `weak` before
  it is considered for retirement.

## How stacks (and kinds) extend

A stack a project does not use costs it one pruned directory and zero context — the fragment's job
is the index entry and the file pointer, not the rules; the rule bodies live in
`references/stack-<stack>.md`, which ships with `review-tests`/`design-tests` and is loaded
conditionally:

```
features/common/skills/review-tests/extensions/<stack>/
  extension.json    {"skill":"review-tests","extension":"<stack>","requires":["stacks=<stack>"]}
  extension.md       # merged into review-tests/SKILL.md at its own EXT anchor
features/common/skills/design-tests/extensions/<stack>/
  extension.json    {"skill":"design-tests","extension":"<stack>","requires":["stacks=<stack>"]}
  extension.md
```

`requires` supports OR (`["stacks=ts||stacks=react"]`) — but see the trap this framework already
proved: `react` requires `["ts","node"]` and the expanded closure is what `detect.py` writes into
`config.stacks`, so `stacks=node`/`stacks=ts`/`stacks=js` **all match every React project**. Gate a
frontend fragment on the framework-framework stack (`react`, `vue`, `angular`), never on `ts`/`js`/
`node` alone.

Rules for a stack fragment:
- every rule carries `parent:` (see "Adding a rule" #5 above);
- every rule's check runs from the project root with the project's own commands;
- version-pinned claims name the version and the date they were verified;
- where an installed plugin already carries the catalogue, the fragment is a **pointer**, gated
  with a `when the plugin is installed` clause so `skills_lint` rule 8 passes.

## `rules.json` — the one condition it exists under

`scripts/rules_index.py` generates `rules.json`, `references/walk-review.md` and
`references/walk-design.md` from this directory's markdown; markdown is the source of truth and
`rules.json` is never edited by hand and never carries prose — the moment a rationale is written
into it, it is a second source of truth and must be deleted. `rules.json` earns its place because
of exactly **one** cross-repo consumer: the `skill-bench` benchmark harness, which joins
`archetypes[]` against rule ids to report which rules a seeded defect exercised. `--check` and the
walk generator both work fine off an in-memory parse without it. **If the benchmark link is ever
cut, `rules.json` goes with it** — regenerate the walks from an in-memory parse and delete the
file, rather than keeping a JSON artifact with no consumer.

## Reference filenames

Every file under either skill's `references/` matches `^[a-z][a-z-]*\.md$` — lowercase letters and
hyphens only, **no digits**. `badger_lib.py`'s `SIBLING_REFERENCE_RE` (the mechanism that lets
`design-tests` cite `../review-tests/references/<file>.md` as a co-install dependency) only
recognises that shape; a filename with a digit — `kind-a11y.md` was the proposed and rejected one —
still ships via `scaffold.py`, but the citation to it becomes invisible to the catalog's
dangling-reference guard — the same dangling-reference class this governance file exists to close.
This is why the accessibility kind file is `kind-accessibility.md`, never `kind-a11y.md`.

## L4 — project-local rules

Not shipped; this ruleset only says how a project adds its own.

- Location `.ai-badger/test-rules/local.md`, prefix **`TX-`**, so a project id can never collide
  with a shipped one and a reviewer can always tell where a finding's authority comes from.
- Same record schema as `universal.md`, same evidence bar, **plus one extra required field:
  `incident:`** — the dated event that produced it. A project-local rule with no incident is a
  preference, and preferences belong in an instructions file, not in a ruleset that produces
  blockers.
- Two kinds of L4 entry:
  1. **A new rule** — a defect class this project has and the framework has not seen.
  2. **An exception** — `TX-EXC-nn: <shipped id> does not apply to <path glob> because <reason>,
     expires <date>`. Exceptions expire; an expired exception is a finding in its own right, and
     `--check` fails on one. This is what keeps a C13-shaped ruling honest — a project may carve
     out a rule, but not quietly and not forever.
- **Promotion path.** A `TX-` rule that has fired in two different projects, or whose incident is
  reproducible outside this repo, is a `feed-badger` candidate: it is proposed as L1/L2/L3 with its
  incident as the evidence, and it must pass "Adding a rule" above unchanged. The framework never
  absorbs a project rule on volume alone.
- The project's learned skills and memories are the **source** of L4 rules, not a parallel
  ruleset: when a memory produces a rule, the rule cites the memory file and the memory gains a
  line naming the rule id. Otherwise the two drift and nobody notices, which is the failure this
  whole layering exists to prevent.
