# Contributing to ai-badger

Thanks for looking. This is a small project — one maintainer, no committee — so the process is
short, but the gates are real and CI enforces them. Read this once and you will not be surprised
by a red build.

If something here does not match what the repository actually does, that is a bug in this file.
Open an issue.

## First time here?

You do not need to understand the whole framework to make a useful change. Good first
contributions:

- A fix to a `features/…/*.md` catalog file (a persona, an invariant, an instruction).
- A missing test for behaviour that already works.
- A documentation correction — especially one that names a file path that no longer exists.

Two things to know before you start, because they shape everything below:

1. **Write the failing test first.** This is not aspirational; a CI gate checks that a code change
   touched tests.
2. **A `git push` is not a release.** Releases are a separate, deliberate step. See
   [`RELEASING.md`](RELEASING.md).

## Setup

Python **3.10+** (the CI floor) and Node (for the two `.mjs` gate scripts).

```bash
git clone https://github.com/Arasz/ai-badger
cd ai-badger
python3 -m venv .venv
.venv/bin/python3 -m pip install -r engine/requirements.txt -r engine/requirements-dev.txt
.venv/bin/python3 -m pip install pytest
```

`engine/requirements.txt` carries the runtime dependencies and `engine/requirements-dev.txt`
carries `pylint`; `pytest` is not pinned in either, so install it separately (CI installs
`pylint pytest jsonschema pyyaml` explicitly).

**Use `.venv/bin/python3` for everything below.** Depending on your machine the system `python3`
may be a version with no pytest installed; every command in this file assumes the venv
interpreter.

Runtime dependencies are deliberately minimal, and the two behave differently on purpose:

- **`jsonschema` is required.** `engine/badger_lib.py` imports it inside the three validation
  functions rather than at module scope, so the eleven entry points that never validate do not
  pay for it — but the `ImportError` is never caught there. Validation that silently no-ops is
  worse than a missing dependency — an unvalidated config would sail straight into the
  scaffolder — so it fails loudly instead.
- **`pyyaml` is optional.** It is imported behind a guard and degrades to a printed note
  (`mcp_index.YAML_MISSING_HINT`), because the features needing it are not on the critical path.

Everything else is standard library. **Do not add a third runtime dependency without a very good
reason**; if you must, decide deliberately which of these two shapes it takes and say so here.
`gates/deps_guard.py` enforces the declaration half of that rule: it parses every `*.py` under
`engine/`, `tooling/`, `features/` and `gates/` and fails on a third-party import — including one
hidden inside a function or a `try:` block — that `engine/requirements.txt` does not declare.

Optionally install the pre-commit hooks, which run seven of the gates locally:

```bash
.venv/bin/python3 -m pip install pre-commit
pre-commit install
```

They are `version-sync`, `index-build`, `plugin-skills-sync`, `docs-guard`, `deps-guard`,
`shipped-paths-guard`, and `pylint` — see
[`.pre-commit-config.yaml`](.pre-commit-config.yaml).

Separately, lefthook runs **every** gate on `git push`. That one is worth installing — it is what
stops an unbumped `VERSION` or a failing test from reaching CI. See
[Automating the gates](#automating-the-gates-lefthook).

### Local MCP servers (`code-review-graph`, `hermes`)

`.mcp.json` is **not tracked**. It used to be, and that shipped two permanently-broken
user-global MCP servers to every plugin installer — Claude Code loads a plugin's `.mcp.json` at
*user* scope, and the file's `cwd` only ever resolved on the machine that committed it (issue
#173, [ADR-0014](docs/adr/0014-mcp-support-is-configuration-not-retrieval.md) decision 6).

If you want `code-review-graph` and `hermes` available in this checkout, copy the example and
fill in your own path:

```bash
cp .mcp.json.example .mcp.json
```

Then replace the placeholder `cwd` with the absolute path to this checkout. `.mcp.json` is
gitignored, so the copy stays local — `gates/shipped_paths_guard.py` fails the build if a
machine-specific absolute path (`/Users/…`, `/home/…`, `C:\Users\…`) ever ships in a tracked
file again.

## How the repository is laid out

```
features/{stack|common}/{feature}/   the catalog — skills, personas, invariants, instructions,
                                     hooks, adjustments, templates, mcp
engine/                              the library every bootstrap shim imports: badger_lib,
                                     framework_copies, frontmatter, unsafe_literals
                                     (and requirements.txt)
tooling/                             maintainer catalog and release tooling: index_build,
                                     validate, version_sync, changelog_index,
                                     sync_plugin_skills, install_plugins, retrieval_eval,
                                     fixture_harvest
gates/                               repo gates CI and the pre-push hook run: release_guard,
                                     tdd_guard, docs_guard, deps_guard, shipped_paths_guard,
                                     scaffold_freshness_guard, skills_lint (and gate_report,
                                     the shared finding shape)
.github/scripts/                     CI helpers a workflow calls and nothing else:
                                     conflicting_pr_report
schemas/                             a JSON Schema per *.json model
index.json                           SCRIPT-GENERATED. Never hand-edit it.
tests/                               pytest; tests/js/ holds the node --test suites
docs/                                see docs/README.md
```

That list is prose and drifts; [`docs/scripts.md`](docs/scripts.md) is the one this repo checks
(`tests/test_docs_match_the_catalog.py` fails when a script under `engine/`, `tooling/` or
`gates/` has no entry there).

[`docs/framework-architecture.md`](docs/framework-architecture.md) explains the model,
[`docs/authoring-a-feature.md`](docs/authoring-a-feature.md) is the how-to for adding a catalog
entry, and [`docs/scripts.md`](docs/scripts.md) covers running the scripts.

## The workflow

### 1. Branch. Never push to `main`.

```bash
git checkout -b task/short-description
```

**One task, one PR.** Do not bundle unrelated work into a single change — it makes review and
rollback lose their scope.

The single exception: **the maintainer may ask for a change to be merged locally**, skipping the
PR. That is their call and nobody else's — if you are contributing, or you are an agent working
in this repo, assume the rule is absolute unless you were told otherwise for that specific
change. The exception drops the PR, never the gates: everything in
[step 6](#6-run-every-gate-before-you-ask-for-review) still has to pass before the push, because
the PR was the record, not the safety net.

### 2. Write the failing test first

TDD is a non-negotiable invariant in [`CLAUDE.md`](CLAUDE.md). No production code without a test
that demanded it.

```bash
.venv/bin/python3 -m pytest tests/test_the_thing.py -q   # red
# ...implement...
.venv/bin/python3 -m pytest -q                           # green
```

`gates/tdd_guard.py` checks the one thing a machine can: that a change to `.py` or `.mjs` under
`engine/`, `tooling/`, `features/` or `gates/` came with a change to a test file. It is a signal,
not a proof — it cannot tell a real test from an empty one, so passing it is not the point;
writing the test first is.

There is an escape hatch — `[no-tests]` in a commit message in the range — and it is **printed in
CI output**, so an unjustified one is visible rather than silent. Use it only for changes that
genuinely cannot be tested, and say why in the PR body.

Catalog JSON is covered by `validate.py --all` and documentation by review, so neither counts as
code for this gate.

### 3. Commit small, push often, open a draft PR early

Open a **draft PR from your first commit** so the work is visible in flight rather than arriving
as one large diff at the end.

### 4. Re-scaffold if you touched the scaffolder

`skills/` and `.ai-badger/` hold copies of catalog content that go stale. After touching
`engine/`, `tooling/` or `features/common/skills/welcome-ai-badger/`, regenerate them or
`sync_plugin_skills --check` and pylint will fail:

```bash
.venv/bin/python3 tooling/sync_plugin_skills.py
.venv/bin/python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py \
    --config .ai-badger/config.json --target . --root .
```

`index.json` is likewise generated — run `.venv/bin/python3 tooling/index_build.py` after adding
or removing a catalog entry, and commit the result. Never edit it by hand.

### 5. Decide whether this is a release

Run the release guard. It compares the shipped surface (`features/`, `engine/`, `tooling/`,
`schemas/`, `index.json`, `skills/`) against the **last release tag** — not the previous commit.
`gates/` is not shipped surface: a change confined to the repo gates needs no bump.

```bash
.venv/bin/python3 gates/release_guard.py
```

- **It passes and reports no shipped-surface change** (docs-only, tests-only): do *not* bump
  `VERSION`, do *not* add a changelog entry.
- **It fails**: your change ships. Then, per the invariant in [`CLAUDE.md`](CLAUDE.md):
  1. Bump [`VERSION`](VERSION) — patch for fixes, minor for anything that changes what
     scaffolding *does* to a consumer repo. Pre-1.0, the minor slot is the breaking slot.
  2. Add `docs/changelog/{version}-{slug}.md` describing what changed. **One file per version —
     this repo does not keep a single root `CHANGELOG.md`.** See
     [`docs/changelog/README.md`](docs/changelog/README.md).
  3. `.venv/bin/python3 tooling/changelog_index.py` — regenerates that README's table from the
     entry files. The table is generated; hand-editing it is what issue #160 removed.
  4. Add the version to [`BREAKING_VERSIONS`](BREAKING_VERSIONS) if a re-scaffold is *required*,
     not merely recommended.
  5. `.venv/bin/python3 tooling/version_sync.py` — propagates the version into `plugin.json`,
     `marketplace.json` and `index.json`.
  6. **Re-scaffold this repo against itself, last.** The scaffolder stamps `frameworkVersion`
     into `.ai-badger/manifest.json` and `Scaffolded by ai-badger <v>` into every generated
     agent file, and it reads the version from `index.json` — so this only produces the right
     answer *after* step 5. Skipping it shipped a stale manifest in 3 of the 14 tags between
     0.87 and 0.99; `version_sync.py --check` now catches it.

     ```bash
     AI_BADGER_MCP_AVAILABILITY=all .venv/bin/python3 \
       features/common/skills/welcome-ai-badger/scripts/scaffold.py \
       --config .ai-badger/config.json --target . --root . --no-install --skills ''
     ```

Full detail, including the semver-for-a-catalog rules, is in [`RELEASING.md`](RELEASING.md).

### 6. Run every gate before you ask for review

One command runs all of them:

```bash
.lefthook/pre-push/verify.sh all
```

That is the same script the pre-push hook runs — see
[Automating the gates](#automating-the-gates-lefthook) below. To run them by hand instead, or to
see what each one does, these are exactly what CI runs (`.github/workflows/pylint.yml`), so a
green local run means a green build:

```bash
.venv/bin/python3 -m pylint $(git ls-files '*.py' | grep -v '^tests/')   # 10.00 required
.venv/bin/python3 -m pytest -q
.venv/bin/python3 tooling/index_build.py --check
.venv/bin/python3 tooling/changelog_index.py --check
.venv/bin/python3 tooling/sync_plugin_skills.py --check
.venv/bin/python3 tooling/validate.py --all
.venv/bin/python3 tooling/version_sync.py --check
.venv/bin/python3 gates/docs_guard.py
.venv/bin/python3 gates/deps_guard.py
.venv/bin/python3 gates/release_guard.py
.venv/bin/python3 gates/shipped_paths_guard.py
.venv/bin/python3 gates/scaffold_freshness_guard.py
.venv/bin/python3 gates/tdd_guard.py --base origin/main
.venv/bin/python3 gates/workflow_lint.py
node --test "tests/js/*.test.mjs"
```

What each one is for:

| Gate | Fails when |
|---|---|
| `pylint` | Anything below 10.00 on non-test Python. Tests keep their own conventions and are excluded. The `lint` command in [`CLAUDE.md`](CLAUDE.md) is this same invocation — one lint behaviour everywhere. |
| `pytest -q` | Any test fails. |
| `index_build.py --check` | `index.json` does not match the catalog on disk. |
| `changelog_index.py --check` | The generated table in `docs/changelog/README.md` does not match the entry files. Re-run without `--check`; never hand-edit the table. |
| `sync_plugin_skills.py --check` | The shipped `skills/` copy has drifted from `features/`. |
| `validate.py --all` | Any catalog JSON violates its schema in `schemas/`. |
| `version_sync.py --check` | `plugin.json`, `marketplace.json` or `index.json` disagree with `VERSION` — or the scaffold stamps do, meaning step 6's re-scaffold was skipped. |
| `docs_guard.py` | A relative link or a backticked repo path in the docs no longer resolves, or a changelog entry is missing from `docs/changelog/README.md`. |
| `deps_guard.py` | Code imports a third-party module that `engine/requirements.txt` does not declare. |
| `release_guard.py` | The shipped surface changed since the last release tag without a `VERSION` bump. |
| `shipped_paths_guard.py` | A machine-specific absolute path (`/Users/…`, `/home/…`, `C:\Users\…`) ships in a tracked file outside `docs/`, root `*.md` and `tests/`. The `docs/` exemption stops at generated `.html`/`.htm` pages, which are scanned. |
| `skills_lint.py` | A catalog `SKILL.md` breaks one of the eleven conventions. `validate.py --all` calls it, so CI reports it without a step of its own. |
| `scaffold_freshness_guard.py` | Re-scaffolding this repo against itself would change something other than a version stamp — a `features/**` edit that never reached `.ai-badger/`. |
| `tdd_guard.py` | Code changed and no test changed with it. Runs on branches, not on `main`. |
| `workflow_lint.py` | A `uses:` in `.github/workflows/` is not pinned to a commit SHA, or a workflow runs on the repository's default token scope with no `permissions:` block. |
| `node --test` | A `.mjs` gate script's tests fail. |

**CodeQL** also runs on every pull request and is a required check before merge.

### Automating the gates (lefthook)

[`lefthook.yml`](lefthook.yml) wires the gates to `pre-push`, so the checks above cannot be
skipped by forgetting them. Install it once:

```bash
brew install lefthook   # or: go install github.com/evilmartians/lefthook@latest
lefthook install
.lefthook/pre-push/verify.sh doctor
```

`doctor` reports the interpreter it resolved, whether `pytest`/`pylint`/`jsonschema` import, and
whether the hooks are intact. Run it first when the gate behaves oddly.

All the logic is in [`.lefthook/pre-push/verify.sh`](.lefthook/pre-push/verify.sh), not in the
YAML, so it stays runnable by hand, in CI and by an agent:

| Command | Does |
|---|---|
| `verify.sh all` | Every lane, including the ones a push leaves to CI. |
| `verify.sh pre-push` | The local lanes. A branch deletion runs none. |
| `verify.sh lanes` | Prints what `pre-push` would run, without running it. |
| `verify.sh <lane>` | One lane — `pytest`, `pylint`, `docs`, `release`, `tdd`, `js`, … |
| `verify.sh doctor` | Environment and hook integrity. |

**`pytest`, `pylint` and `journey` are not in the local set.** `.github/workflows/pylint.yml`
runs `verify.sh pytest` and `verify.sh pylint` on every push to every branch, against the Python
this project floors at rather than whatever your machine has — so the local copy took the longest
and proved the least. `.github/workflows/consumer-journey.yml` runs `journey` the same way. Run
any of them by hand (`verify.sh pytest`) when you want it before pushing; `verify.sh all` still
runs all three.

The list is derived, not restated: `CI_ONLY_LANES` names what CI owns and `LOCAL_LANES` is
`$LANES` minus that, so a lane added to `$LANES` joins the push with no second edit. Two tests
hold CI and the hook to the same answer — the `gates` job's skip list must equal `CI_ONLY_LANES`,
and every lane it skips must be run by another push-triggered workflow. `verify.sh --help` prints
the current split rather than a copy of it.

**A push is bounded.** `VERIFY_DEADLINE` (default 1200s) kills the whole lane process *group* and
writes its row to `logs/lefthook.log` before doing so, exiting 124. Killing only the direct child
left an orphaned pytest at PPID 1 that outlived `git push`, lefthook and `verify.sh` — and wrote
no row at all, which is why the log's timings were survivorship-biased for as long as they were.

Only `pre-push` is wired. `pre-commit` deliberately stays with the pre-commit framework: it
already runs eight of these gates and chains to code-review-graph, and `lefthook install` renames
a hook it conflicts with to `.old` and never runs it again. Configuring both would silently kill
that chain.

**It verifies; it never edits.** A pre-push hook must not mutate the tree — by the time it runs,
the commits being pushed are already fixed, so bumping `VERSION` there would leave a dirty
working tree and push a commit without the bump. `release_guard.py` is what makes the bump
unskippable: it fails the push until you bump `VERSION` yourself. Do that in a commit, per
[step 5](#5-decide-whether-this-is-a-release).

When a lane fails it prints how to reproduce it, where the log is, and how to bypass it:

```
VERIFY_SKIP=scaffold git push   # skip one lane
SKIP_VERIFY=1 git push          # skip every lane
git push --no-verify            # skip the hook entirely
```

Use them when a lane is broken for reasons unrelated to your change, and say so in the PR — CI
still runs every gate, so a bypassed push fails there instead. Per-developer overrides go in
`lefthook-local.yml`, which is gitignored.

### 7. Open the PR

State in the body:

- What was red before you implemented, and what is green now.
- **Any pre-existing test you rewrote to a new contract, by name.** A rewritten test is a changed
  promise; it must be visible in review rather than buried in the diff.
- Whether this is a release (and if so, which version and which changelog file).
- Anything you deliberately did not do, and why.

Then wait for CI. Do not merge with a red build.

#### If a required check never reports, the PR is stuck, not slow

`gitleaks` is a required status check. It comes from `Secret scan`, which — like `CodeQL` —
triggers only on `pull_request`. GitHub does not run `pull_request` workflows on a pull request
that conflicts with its base ([events that trigger
workflows](https://docs.github.com/en/actions/using-workflows/events-that-trigger-workflows)):
there is no merge ref to check out, so **no run is dispatched at all**. The mergebox draws that
identically to a queued run, so the PR looks slow while it is in fact unmergeable forever.

What makes it convincing is the asymmetry: `Lint and test` triggers on `push`, so `build (3.10)`
keeps reporting normally. PR #341 sat this way for 37 minutes — three `Lint and test` runs, zero
`Secret scan` runs — until a merge commit started the others.

The fix is to merge `main` into your branch (this repo merges, never rebases) and push. To check
before waiting on the six-hourly [`Stuck PR watch`](.github/workflows/stuck-pr-watch.yml), which
posts this same notice on any stuck PR:

```bash
gh pr view <number> --json mergeable --jq .mergeable   # CONFLICTING means stuck
```

### 8. After merge — tag, if you released

**This step is the release**, and it is easy to forget because a green PR looks finished without
it. From `main`:

```bash
claude plugin tag --push     # creates ai-badger--v{version}
```

Until that runs, the version denotes no commit and `release_guard.py` still compares against the
*previous* tag — which silently disables the guard. That is exactly how this project accumulated
a 32-release gap. Never skip a tag.

Then verify the release shipped by checking **content**, not the CLI's own output — see the
mandatory verification section in [`RELEASING.md`](RELEASING.md).

## Mutation testing (`features/common/retrieval/` only)

[`mutmut`](https://mutmut.readthedocs.io/) is a dev-only dependency, scoped hard to one
directory:

```bash
.venv/bin/python3 -m pip install -r requirements-mutation.txt
.lefthook/pre-push/verify.sh mutation
```

Declared in [`requirements-mutation.txt`](requirements-mutation.txt) at the repo root, not
`pyproject.toml`'s `[project.optional-dependencies]` — that needs a `[project]` table with a
`version` key, which setuptools refuses to build without (verified: `pip install -e ".[dev]"`
fails against a `[project]` table that has no `version`, PEP 621) — and not
`engine/requirements-dev.txt` either, since `engine/` is a shipped path and touching it would
force a `VERSION` bump for a tool nothing ships.

**What it covers:** `features/common/retrieval/` (`bm25.py`, `mcp_matcher.py`, `tokenizer.py`) —
the BM25 ranking and MCP-tool matching logic. Nothing else. That module is where a live run
demonstrably paid: 235 LOC, no subprocess, no repo-tree reads, a 190-mutant run in about 15
seconds.

**What it deliberately does not cover:** everything else. `gates/`, `engine/`, `tooling/` and
`features/**/scripts/` are not configured for mutation at all, and `features/common/hooks/` is
structurally blocked — 28 of this repo's 101 test files spawn a script as a subprocess, and
mutmut's trampoline resolves its config cwd-relatively with no override, so it crashes the
moment a mutated script runs as a subprocess from a temp directory. Scoping around that
destroys the score anyway: `scaffold.py` alone scored 39% with 136 "no tests" purely because its
tests live in 40 *other* files — an artefact of the harness, not a gap in the tests.

**No threshold, no score, no CI job, and it is not in `$LANES`** — `verify.sh mutation` never
runs on push or in CI, and nothing here can fail a build. mutmut's own maintainer has said teams
that put it in CI eventually "throw it all away as it's useless," and the subprocess-trampoline
crash described above would block it regardless. A mutation *score* is not reported
either, matching Google's public reasoning for not publishing one: "it is neither concrete nor
actionable, and it does not guide testing." Treat a run's summary as a lead, not a grade.

**What to do with a survivor:** read it. Each one is either a real gap — write the test that
kills it — or genuine noise, in which case add a `# pragma: no mutate: <one-line reason>`
comment on that line rather than leaving it to resurface on every run (the trailing `: reason`
does not break the match — verified against `mutmut.mutation.pragma_handling` directly).
`[tool.mutmut]` in [`pyproject.toml`](pyproject.toml) already suppresses categories of noise
this repo has (docstrings, exception message text, `@dataclass(frozen=True)`, `pylint:`
comments); the pragma is for the one-off case a pattern would over-suppress. There is no third
option — a survivor that is neither tested nor annotated is exactly what the next run will
report again.

**`no tests` is not an exemption from that rule — it is the same finding stated differently.**
A `survived` mutant means a test ran against it and didn't notice; a `no tests` mutant means
mutmut could not attribute *any* test to the function at all, which is a gap of its own, not a
harness quirk to set aside. If `grep -rn "<function name>" tests/` comes back empty, that is
the reason mutmut says `no tests` — the function is only exercised indirectly through a caller,
and it still needs a test named after *it*.

The pragma comment has one sharp edge: mutmut's CST scan only recognizes a trailing `# pragma:
no mutate` on a statement's *own* line. A line buried inside a multi-line call (a list literal
spanning several lines, for instance) is invisible to it — `do_not_mutate_patterns`' regex is
the only mechanism that reaches those, one entry per confirmed-equivalent mutant, each
commented with why (see the last entry in `[tool.mutmut]` for a worked example).

The first scoped run surfaced 44 `survived` mutants and 3 `no tests` ones. Of the 44: 41 were
real gaps, fixed with tests that pin exact values rather than loose orderings; 1 was confirmed
equivalent and annotated as above; 1 is a real gap deferred to a follow-up issue because a clean
test needs a hand-tuned corpus that ties two documents' scores exactly while their coverage
differs — disproportionate effort for one line. The 3 `no tests` mutants were all
`bm25.fuse_document` — a `grep` for the function name across `tests/` returned nothing; it was
only exercised indirectly through `mcp_matcher.build_corpus`, and it is the field-weighting
mechanism (`name ×3, tags ×2, intent ×1`) the whole retrieval design rests on. Fixed with three
direct tests (a weight actually applied, two fields' tokens summing rather than overwriting, an
empty field contributing nothing) rather than set aside as unrelated. **Adopting the harness and
not reading its first output — all of it, `no tests` included — is how it becomes a tool nobody
runs.**

**Review it after about a month** against one falsifiable question: did any survivor here lead
to a test that would have caught a real defect? If not, delete the config — a tool that finds
nothing worth acting on is not worth carrying.

## Conventions worth knowing

The full list of non-negotiable invariants is in [`CLAUDE.md`](CLAUDE.md). The ones that most
often surprise a new contributor:

- **Screaming architecture.** Name folders and modules after the domain concept, not the
  technical bucket. No catch-all `Services/`, `Controllers/`, `Utils/`. A shared technical
  chassis (logging, DI wiring, cross-cutting middleware) is the one accepted exception.
- **Guard clauses over hand-rolled null checks.** Fail fast at the boundary with a dedicated
  guard helper, so the exception type and message stay consistent.
- **Minimal comments.** A doc comment is 1–3 lines stating the contract. Put the "why" in an ADR
  or a spec document and point at it. Test doc comments are one sentence or none — the test name
  and body carry the intent.
- **No hardcoded secrets**, anywhere, including examples and fixtures. Sample values must be
  obviously fake.
- **No hand-rolled crypto or security orchestration.** Delegate to an audited library.

Scoped instructions live in `.ai-badger/instructions/` — read
`python.instructions.md`, `javascript.instructions.md`, or `documentation.instructions.md`
before editing files of that kind.

### Two concurrent PRs always conflict. Regenerate, never hand-merge.

Every release bumps `VERSION`, regenerates `index.json`, and adds a row to
`docs/changelog/README.md`, so any two PRs open at once collide on all three by construction. On
2026-08-15, eight commits touched each of them and four separate merges conflicted.

They are derived files. Hand-resolving them produces marker damage rather than a resolution, and a
following `git add -A` then stages files still containing `<<<<<<<`. Resolve arbitrarily, then
regenerate from source:

```bash
git checkout --ours VERSION index.json docs/changelog/README.md   # side does not matter
echo "<the version you intend>" > VERSION
AI_BADGER_MCP_AVAILABILITY=all python3 features/common/skills/welcome-ai-badger/scripts/scaffold.py \
    --config .ai-badger/config.json --target . --root . --no-install --skills ''
python3 tooling/version_sync.py && python3 tooling/index_build.py && python3 tooling/changelog_index.py
python3 tooling/version_sync.py          # again: the scaffold stamps CLAUDE.md from the synced manifests
```

Two traps this does not cover:

- **`.ai-badger/config.json` is source, not generated.** Taking either side of a conflict there
  silently drops the other side's `include.skills` entries — no marker, no gate, and nothing
  regenerates them back. Diff it against `main` after any merge.
- **A squash-merged base orphans everything built on it.** The merge-base predates the flattened
  commit, so `git merge origin/main` replays the whole merged branch and produces enormous conflict
  counts over a small real difference — 187 hunks over 19 files, on one occasion. Check
  `git diff origin/main..origin/<branch> --stat` first: if that is small, rebuild rather than merge
  (`git worktree add -b fix/x origin/main`, then `git diff` piped through `git apply`).

## Architecture decisions

Decisions that would otherwise get re-litigated are recorded as ADRs in
[`docs/adr/`](docs/adr/README.md), numbered, MADR-shaped, and **never edited after acceptance** —
a decision that changes gets a *new* ADR that supersedes the old one.

If your change reverses or constrains a recorded decision, add the ADR in the same PR.

## What is in scope

**Wanted:** catalog content that is genuinely project-agnostic; new stack support; tests; bug
fixes with a failing test; documentation that names real file paths.

**Not wanted:** project-specific content dressed up as a framework feature (if it only makes
sense in your repo, keep it in your repo — that is what `.ai-badger/`'s seed-once files are
for); a third runtime dependency; hand-edited `index.json`; changes to `docs/adr/` entries that
have already been accepted.

**Ask first** (open an issue before writing code): anything that changes the scaffold output
shape, a schema, or a hook contract — those are minor-version, blast-radius changes.

## Security

Do not open a public issue for a security problem. See [`SECURITY.md`](SECURITY.md).

## Code of conduct

Participation is governed by [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).
