# Review-tests: eval-reanchor — composite-label contract + pinned-copy integration + P3 skip disposition

Date: 2026-09-30 · Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `eval-reanchor`
(commit `ab5b789c`).

Target: the two test files the step changed, against `scripts/retrieval_tuning/build_eval_corpus.py` and
the decision record `docs/work/2026-09-30-eval-reanchor-decisions.md`:

| File | Changed |
|---|---|
| `scripts/tests/test_retrieval_tuning_eval_corpus.py` | +62 — `test_composite_label_families_resolve_in_copy` (copy-independent), `test_every_adr_family_resolves_in_the_pinned_copy` (copy-gated integration leg) |
| `scripts/tests/test_p3_cli_parity.py` | +27 — `_has_composite_sections` probe + copy-conditioned `pytest.skip` before the legacy-parity harness |

**Verdict: PASS-WITH-FINDINGS — 0 blocker, 0 major, 2 minor.** The composite unit contract was seen to
fail against two different mutant matchers (whole-string revert; loose prefix) and go green restored. The
copy-gated half was run against the exact pinned copy: 13 passed, 0 skipped; with the wrong present copy
the E1 gate skips with a reason naming both hashes. The P3 skip fires with the exact named reason, and I
independently reproduced the frozen `12a72dfb` generator refusing COPY1022 (`…0004… context`, exit 1, no
payload) and `cb99fe6e` (`…0060… context`, exit 1). The plan's AC1 `.queries[].text` check is provably
vacuous and the honest `.queries[].query` variant is the real gate — I re-ran both directions of that
mutation proof. `test_refresh_corpora.py` deliberately untouched leaves no coverage hole (verified by
running the registry gate against COPY1022). The two findings are in the P3 skip: its probe is wider than
the refusal it stands for, and its skip metadata names no tracking id or re-enable condition.

## Scope and method

Out of scope, stated before starting: the files' pre-existing tests beyond the diff; the production fix
itself (`build_eval_corpus.py`'s matcher is judged only through what the tests can observe); the decisions
doc's MEASURED tables beyond the claims the task names; the pre-existing project-half debris pin
(23 ids vs 14 observed) — not re-measured here. This is a test verdict, not a production review.

- `.ai-badger/config.json:30` — `commands.test` = `dotnet test`; it cannot see Python. The only CI lane
  for `scripts/tests` is `scripts-harness` (`.github/workflows/build.yml:529`), which runs an explicit
  file list. `test_retrieval_tuning_eval_corpus.py` **is** on it; `test_p3_cli_parity.py` is a documented
  opt-in local gate (`.github/workflows/build.yml:505-512`). No hook runs pytest in this worktree.
- All mutations were applied to copies under `/tmp/er-mut` (production generator copy, `scripts/src`
  symlinked to the real package), run there, and restored; the real `build_eval_corpus.py` was never
  edited. Nothing in the worktree was edited except this file.
- The copy-gated runs used the real pinned copy
  `/Users/arasz/ai-raccoon-eval/copies/memory-1022-2026-09-30.db` via `AI_RACCOON_EVAL_COPY`.
- `PYTHONDONTWRITEBYTECODE=1` and `-p no:cacheprovider` on every run.

Runs and their outputs:

```
$ AI_RACCOON_EVAL_COPY=…/memory-1022-2026-09-30.db python3 -m pytest \
      scripts/tests/test_retrieval_tuning_eval_corpus.py -q -rs
.............                                                            [100%]
13 passed in 7.12s

$ python3 -m pytest scripts/tests/test_retrieval_tuning_eval_corpus.py -q -rs   # default env → cb99fe6ecbe0.db
........sss.s                                                            [100%]
SKIPPED … copy at …/cb99fe6ecbe0.db is not the snapshot the committed artifact pins
        (d23ee28e52d1... != cb99fe6ecbe0...)
9 passed, 4 skipped in 0.33s

$ P3_CLI_PARITY=1 AI_RACCOON_EVAL_COPY=…/memory-1022-2026-09-30.db python3 -m pytest \
      "scripts/tests/test_p3_cli_parity.py::TestRefreshParity::test_generators_reproduce_the_legacy_payload" -q -rs
SKIPPED [1] scripts/tests/test_p3_cli_parity.py:281: frozen P3_BASE 12a72dfb generator rejects composite
        section labels, which …/memory-1022-2026-09-30.db carries — no copy both generators process is
        available; disposition: skip, not rescope
1 skipped in 0.15s
```

Note on "unskipped copy-gated tests": the diff adds 62 lines and removes none — no test was unskipped in
code. The unskip effect is the corpus pin move (`e0434a72…` → `d23ee28e…`, the sha of the available
copy): with the pinned copy supplied the four copy-gated tests now run instead of skipping; with any
other present copy they still skip with both hashes named (E1, as the file's own `_snapshot_mismatch_reason`
docstring says).

## What the suite does well

- **The composite contract has teeth in both directions.** Two mutants on the `/tmp` generator copy:

  ```
  M1 — _section_matches reverted to whole-string (return _label_matches(section, family)):
  >  assert mod._section_matches("0008 | Context | Decision", "context"), \
  E  AssertionError: a composite label must match its Context component
  FAILED …::test_composite_label_families_resolve_in_copy

  M2 — context matched by loose prefix (label.startswith("C")):
  >  assert not mod._section_matches("Consequences | Non-Goals (explicit)", "context")
  E  AssertionError: assert not True
  FAILED …::test_composite_label_families_resolve_in_copy
  ```

  Restored pristine generator: `1 passed in 0.02s`. M1 reproduces the decisions doc's RED-first message
  verbatim; M2 shows the test's negative assertions are not decoration, which matter because a
  component-wise matcher can be made too loose in exactly that direction.
- **The integration leg ran against the real pin and its anti-vacuity guard held.** In the 13-passed run,
  `test_every_adr_family_resolves_in_the_pinned_copy` resolved all 25 files × 3 families and
  `composite_hits > 0` did not fire — the composite path is genuinely exercised by COPY1022, which is
  what the guard exists to prove.
- **The E1 gate is precise.** The skip reason quotes the committed pin and the actual copy sha
  (`d23ee28e52d1... != cb99fe6ecbe0...`), so a wrong copy is diagnosed in the pytest line, not inferred.
- **The P3 disposition's factual basis was independently reproduced** (MEASURED here, read-only):

  ```
  frozen P3_BASE generator on COPY1022: RuntimeError: no chunk of 0004-dual-vector-structure-signal.md
        matches family 'context'   (exit 1, no payload written)
  frozen P3_BASE generator on cb99fe6e: RuntimeError: no chunk of 0060-an-unrecognised-verb-must-not-
        launch-anything.md matches family 'context'   (exit 1)
  ADR-source composite rows: COPY1022 = 527, cb99fe6e = 111
  ```

  So "no available copy is one both generators process" is measured, not argued — for the copies that
  exist.
- **The reason string is a disposition, not a shrug.** It names the frozen base commit, the rejection
  mechanism, the copy, and the decision ("skip, not rescope"); the pre-existing project-half red is
  disclosed in the comment and the decisions doc rather than silently absorbed.

## Known claims checked

### AC1's `.queries[].text` jq check — CONFIRMED vacuous; the mutation proof is sound

Reproduced on the real artifacts (before = `ab5b789c^`, after = `ab5b789c`, plus a mutated after-copy
with `" MUTATED"` appended to E001's `query`):

```
$ jq '[.queries[] | has("text")] | map(select(.)) | length' after.json
0
$ diff <(jq -S '[.queries[].text]'  before.json) <(jq -S '[.queries[].text]'  after.json)   → empty, exit 0
$ diff <(jq -S '[.queries[].query]' before.json) <(jq -S '[.queries[].query]' after.json)   → empty, exit 0
# same checks against mutated.json:
.text check:  exit 0   (mutation invisible)
.query check: exit 1   (…"what does ADR-0011 decide"? MUTATED")
```

The `.text` key does not exist on any entry, so both sides are 100 nulls and any query-text change
passes. The `.query` variant is the real zero-change gate, and on the committed artifacts it is green:
0 query-text changes. The step's commit message and decisions doc correctly record this; the check itself
is a plan-graph artifact, not a test file, so it produces no finding against this diff.

### `test_refresh_corpora.py` left unmodified — judged, no coverage hole

The refresh tests are pin-driven per corpus, not constant-driven, so the pin move needed no test edit.
Verified against COPY1022:

```
$ AI_RACCOON_EVAL_COPY=…/memory-1022-2026-09-30.db python3 -m pytest \
      "scripts/tests/test_refresh_corpora.py::TestRegistry::test_pinned_copy_reproduces_every_committed_corpus" -q
2 passed in 4.68s

direct refresh (records by name):
project-corpus-100.json    status=snapshot-mismatch  matchesCommitted=None queryCount=None
eval-set-100.json          status=clean              matchesCommitted=True queryCount=100
wrapper end state exitCode: 2
```

`test_pinned_copy_reproduces_every_committed_corpus` is parametrized per corpus and asserts the honest
outcome of whichever pin applies: the eval-set's new pin now gets the byte-reproduction branch (green),
and the deliberately untouched project-corpus pin gets the reported snapshot-mismatch branch (exit 2
mixed, exactly the end state the commit claims). Nothing about the pin move is unasserted.

## Findings

| id | file:line | rule | severity | the mutation | run? | what it means |
|---|---|---|---|---|---|---|
| F1 | `scripts/tests/test_p3_cli_parity.py:41,280` | `T1-ORC-03` | minor | none — the predicate itself was run against a synthetic copy whose only pipe label sits outside every ADR `source_file`; it returns True | run (synthetic DB); the resulting mis-skip needs a copy that does not exist → unverified (static reasoning) | `_has_composite_sections` probes the whole `entries` table (`section LIKE '%|%'`), while the frozen generator's refusal is allowlist-scoped. A future copy with rogue composites outside the allowlist would be skipped although the parity test could run; the skip message's "no copy both generators process" would then over-claim. Both available copies do carry ADR composites (527 / 111 rows), so today's skip is correct. |
| F2 | `scripts/tests/test_p3_cli_parity.py:281-285` | `T1-STR-03` | minor | none — grep of the new skip string | unverified (static reasoning) | The skip carries a reason and a disposition but no tracking id (`#809`) and no re-enable condition in the test itself; the disabled code half executes on no available copy, so it is untested code wearing a test's clothes until one exists. |

F1's `run?` caveat, stated once: the predicate behaviour was executed (`_has_composite_sections` on a
plain-only DB → `False`; on a DB whose only pipe label is a non-ADR row → `True`; on a DB without an
`entries` table → `OperationalError: no such table: entries`). What is **not** executed is the material
effect — no real copy exists where the probe is wrong. That half rests on the frozen generator's source
(`git show 12a72dfb:scripts/retrieval_tuning/build_eval_corpus.py:402-430`: the whole-string matcher is
applied only inside `_resolve_adr_target`, which reads only `source_file LIKE '%/docs/adr/{filename}'`
for the allowlist). The row is labelled accordingly.

## Detail

### F1 — minor — the skip probe is wider than the refusal it stands for

The frozen generator fails when an **allowlisted ADR** resolves no family because the label is composite.
The probe returns True on **any** composite label anywhere in the copy:

```
$ python3 - (helper extracted from the module; synthetic DBs)
plain-only copy          -> False
rogue composite present  -> True      # e.g. "Unrelated table | with pipe", no ADR source_file
no-entries-table         -> OperationalError: no such table: entries
```

The helper's own docstring claims the true case means "the post-1022 shape the frozen P3_BASE generator
refuses (0004-context on COPY1022; 0060-context already on the 254-era cb99fe6e), so no present copy is
one both generators process". That is correct for both copies in hand (their ADR composites are measured
above), but the generalisation is the probe, not the refusal. The over-trigger direction is the risky
one: it skips a test that could run, i.e. hides coverage, rather than failing loudly. Scoping the probe
to ADR sources (`AND source_file LIKE '%/docs/adr/%'`) — or, better, letting the code-under-test's own
refusal decide — would make the predicate match the claim. The missing-table error mode is the safe
direction (loud, not silent).

### F2 — minor — the skip names no id and no way back

`pytest.skip(...)` at `:281` carries the frozen base commit, the rejection reason, the copy and the
disposition, but nothing a tracking system can attach to (the task id `#809` appears nowhere in the
file) and no condition under which the test is expected to run again. The decisions doc carries the
rationale; the test line does not. `T1-STR-03` asks for the reason, an id and a re-enable condition at
the skip site, because the code under a skip is unverified until the skip lifts — and on a plain-label
copy the skipped half is the whole legacy-parity payload comparison.

## Noted, unverified (static reasoning)

Reported for completeness, not counted in the summary:

- The pre-existing project-half red (pinned 23 legacy debris ids vs the 14 observed on a vanished copy)
  was **not** re-measured: that needs the legacy project generator over the 848 MB copy, outside this
  review's scope. The P3 disposition's correctness here rests on the composite refusal, which I did
  reproduce.
- The copy-gated tests in `test_retrieval_tuning_eval_corpus.py` (including the new integration leg) do
  not run in CI — the runner has no bank copy. This is the file's declared E1 blind spot, in writing,
  and the copy-independent unit contract is the CI-visible half. Not counted as a finding.

## Verdict

**PASS-WITH-FINDINGS.** 0 blocker, 0 major, 2 minor (F1–F2).

The step's tests do what the step claims: the composite matcher's semantics are pinned by a
copy-independent contract that reddens under two real mutations, the pinned-copy integration leg
exercises the composite path end to end (anti-vacuity guard included), the E1 gate skips precisely when
the copy is wrong and not otherwise, and the P3 skip's factual basis was reproduced from the frozen
generator itself on both available copies. The AC1 `.text` check is as vacuous as recorded — I re-ran
its mutation proof and it is sound — and the `.query` variant is the real, green zero-change gate. The
untouched refresh test carries the eval-set's byte-reproduction branch without modification. The two
findings concern the P3 skip only (probe breadth, skip metadata) and are minor: neither hides a defect
on the copies that exist today.