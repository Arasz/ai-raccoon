# review-tests — `ab-instrument-arms` (two new tests)

- **Task:** `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `ab-instrument-arms`
- **Date:** 2026-09-30
- **Verdict:** PASS-WITH-FINDINGS — **0 blocker, 4 major, 0 minor**

## Scope

Exactly the two tests the step added (uncommitted in the working tree — taken from dispatch;
no git command was run, per this worktree's house rules):

| target | file:line |
|---|---|
| `test_custom_arm_names_round_trip_blind_and_restore` | `scripts/tests/test_ab_compare.py:317` |
| `test_named_arms_write_per_arm_json_with_per_arm_dll_root` | `scripts/tests/test_run_threshold_eval.py:431` |

Code under test: `scripts/retrieval_tuning/ab_compare.py`,
`scripts/retrieval_tuning/run_threshold_eval.py`.

Step goal judged against the task-graph AC (`ab-instrument-arms` ac1/ac2): a custom-named pair
(chunk254/chunk1022) runs end-to-end with per-arm dll + data root, the position→arm mapping is
restored only after grading, the blind payload surface never names the arms and the default
threshold/off contract is unchanged.

**Out of scope, per dispatch:** the implementer's RED-first evidence and the 2 reported mutation
kills were accepted as reported at dispatch time; this pass did not re-verify them.

## Method and environment

- Runner: `python3 -m pytest scripts/tests/test_ab_compare.py scripts/tests/test_run_threshold_eval.py -q`
  (Python 3.14.7, pytest 9.1.1).
- Baseline on a read-only copy: **24 passed, 1 skipped in 0.09s** (the skip is the env-gated live
  smoke, `test_smoke_live_two_queries_two_arms`, `test_run_threshold_eval.py:350`).
- Every mutation below was applied to a **copy under `/tmp/qa-ab-review`** and reverted there;
  the worktree was never modified. `run?` entries are labelled accordingly.
- Gate note (Pass 0): the project's configured `commands.test` is `dotnet test`, which does not
  run Python. The only CI job that runs `scripts/tests` is `scripts-harness` in
  `.github/workflows/build.yml:497-529`, with a hand-typed file list.

## What the tests already prove (positive controls — not findings)

These are the mutations that reddened, i.e. evidence the tests are not tautological:

| probe | mutation (applied to /tmp copy) | result |
|---|---|---|
| P1 | `ab_compare.py:259` — `assign_positions(query_ids, seed, arm_a_name, arm_b_name)` → `assign_positions(query_ids, seed)` (fall back to the fixed threshold/off defaults) | RED at `test_ab_compare.py:345`, `1 failed` |
| P2 | `ab_compare.py:288` — append `first-arm: <name>` to the archived payload | RED at `test_ab_compare.py:355`, `1 failed` |
| P3 | `ab_compare.py:300` — invert `pick_arm` (`second_arm` for `"first"`) | RED at `test_ab_compare.py:348`, `1 failed` |
| P4 | `run_threshold_eval.py:616` — drop `spec.data_root` before the client | RED at `test_run_threshold_eval.py:455`, `1 failed` |
| P5 | `run_threshold_eval.py:623` — force the artifact's `arm` field to `"threshold"` | RED (`ValueError` from the distinct-names guard, `run_threshold_eval.py:531`), `1 failed` |

P3 is worth stating explicitly: even though the mapping assertion at
`test_ab_compare.py:347-348` writes its expected value in terms of other SUT-produced fields, it
is **not** a mirror tautology — an inverted restoration fails it.

## Findings

| id | file:line | rule | severity | the mutation | run? | what it means |
|---|---|---|---|---|---|---|
| F1 | `scripts/tests/test_ab_compare.py:353-355` | `T1-SCO-02` | major | `ab_compare.py:292` — append `first-arm: <name>` to the payload passed to `build_grader_argv` only (archive left blind) | applied+reverted on /tmp copy — **survived** (5 passed) | the blind-surface check reads the archive, never the grader's own argv; a leak to the only audience the blindness protects passes the whole file |
| F2 | `scripts/tests/test_ab_compare.py:344-348` + `:68` | `T1-ORC-03` (enabled by `T1-STR-02`) | major | `ab_compare.py:284` — source `first_chunks` from the opposite arm | applied+reverted — **survived** (1 passed) | the fixture builds identical chunk content for both arms; no assertion can see the rendered payload diverge from the recorded `firstArm`/`secondArm` |
| F3 | `scripts/retrieval_tuning/run_threshold_eval.py:206`, `:199`, `:647` | `T1-CST-06` (`T1-SCO-06`) | major | (a) `default_arm_specs` `check_markers=True`→`False`; (b) its env `dict(ARMS[name])`→`{}`; (c) `arm_env_for` drops `env.update(spec.env)` | applied+reverted — all three **survived** (24 passed, 1 skipped) | the default threshold/off pair's env + marker wiring through `default_arm_specs`/`arm_env_for` has no non-skipped test; the pinned `arm_env` helper has zero production callers left |
| F4 | `.github/workflows/build.yml:529` | `T1-PRF-04` | major | none applicable — literal membership grep over the only CI job that runs `scripts/tests` | unverified (static reasoning) — grep executed, CI itself not runnable here | neither target file is in the CI gate's file list; breaking either new test leaves CI green after merge |

Counts: 0 blocker, 4 major, 0 minor. F4's CI consequence is labelled `unverified (static
reasoning)`; its membership premise is an executed grep.

## Detail

### F1 — the blind-surface assertion covers the archive, not the grader's input

The test reads `ab-forms/<id>.payload.txt` (`test_ab_compare.py:353`) and asserts the arm names
are absent (`:355`). Production passes a separate string to the grader at `ab_compare.py:292`
(`build_grader_argv(runner_cmd, payload, ...)`) — currently the same variable, but nothing in the
test constrains that call site. `FakeRunner` already records every argv
(`test_ab_compare.py:106-114`), so `runner.calls[...][-1]` is the payload the grader actually
received; no assertion ever looks at it.

Probe (applied to `/tmp/qa-ab-review`, then reverted):

```text
mutation: ab_compare.py:292
  build_grader_argv(runner_cmd, payload, ...)
→ build_grader_argv(runner_cmd, payload + f"\nfirst-arm: {first_arm}\n", ...)

$ cd /tmp/qa-ab-review && PYTHONPATH=scripts/src \
    python3 -m pytest scripts/tests/test_ab_compare.py -q
.....                                                                    [100%]
5 passed in 0.10s
```

Why it matters: an arm-identity leak to the grader invalidates the blind A/B conclusion while
every test stays green — the exact class of defect AC5.1/AC2 exist to prevent, on the one channel
nobody watches. Fix direction: assert `"chunk254"`/`"chunk1022"` absent from every
`runner.calls[i][-1]` as well as from the archive.

### F2 — the fixture's arms are content-identical, so the "round trip" cannot be observed

`_arm_json` (`test_ab_compare.py:68`) derives chunk hashes and snippets only from the query/chunk
index — never from `arm_name` (hash construction at `:79`). Both custom arms therefore carry
byte-identical
chunk lists; only the top-level `"arm"` field differs. The test's mapping assertions
(`:344-348`) prove the *recorded* `firstArm`/`secondArm`/`pickArm` bookkeeping, but nothing
compares the rendered lists to the arm each is labelled with.

Probe:

```text
mutation: ab_compare.py:284
  first_chunks = (arm_a if first_arm == arm_a_name else arm_b)[qid]["chunks"]
→ first_chunks = (arm_b if first_arm == arm_a_name else arm_a)[qid]["chunks"]

$ cd /tmp/qa-ab-review && PYTHONPATH=scripts/src \
    python3 -m pytest scripts/tests/test_ab_compare.py::test_custom_arm_names_round_trip_blind_and_restore -q
.                                                                        [100%]
1 passed in 0.05s
```

Why it matters: a regression that renders the secondArm's chunks under the firstArm label —
inverting every grader's pick in the report — is invisible to the suite. The name claims a
round trip; the body proves only the mapping half. Fix direction: make the fixture's chunk
hashes/snippets depend on `arm_name`, then assert each payload's first/second block matches the
chunks of `query["firstArm"]`/`query["secondArm"]` loaded back from the arm artifacts.

### F3 — the default threshold/off wiring is unobserved by the non-skipped gate

The step rerouted the default pair through `default_arm_specs` (`run_threshold_eval.py:206`) →
`arm_env_for` (`:616`) → `run_eval` (`:647`). The pre-existing env tests pin `arm_env`
(`test_run_threshold_eval.py:133,145`) — but after this step `arm_env` (definition
`run_threshold_eval.py:186`) has **zero production call sites** (repo-wide grep: the only hits
are the tests themselves). The only test that drives `run_eval` without `arms=` is the live
smoke, which skips without env vars. So AC2's "default threshold/off behaviour, filenames and
existing tests unchanged" is not actually observed by the two-file gate.

Probes (each applied and reverted):

```text
(a) mutation: run_threshold_eval.py:206  check_markers=True → False
(b) mutation: run_threshold_eval.py:206  dict(ARMS[name]) → {}
(c) mutation: run_threshold_eval.py:199  drop `env.update(spec.env)`

$ cd /tmp/qa-ab-review && PYTHONPATH=scripts/src \
    python3 -m pytest scripts/tests/test_ab_compare.py scripts/tests/test_run_threshold_eval.py -q
.......................s.                                                [100%]
24 passed, 1 skipped in 0.05s      # identical result for (a), (b) and (c)
```

(a) silently disables the marker-discipline gate for the default pair; (b) and (c) silently run
both default arms under a clean env — the measurement would compare an arm with itself and the
suite would stay green. Fix direction: a sibling of the new test that monkeypatches
`StdioMcpClient`, calls `run_eval` **without** `arms=`, and asserts the `arm-off`/`arm-threshold`
artifacts, each client's env equal to the `ARMS` overrides over a clean base, and marker
enforcement (a threshold client emitting no `[mmr-poc]` line must raise
`MarkerDisciplineError`).

### F4 — neither new test is in the only CI job that runs `scripts/tests`

`scripts-harness` (`.github/workflows/build.yml:497-529`) is described in its own comment as
"the only CI coverage for scripts/tests" and runs a hand-typed pytest file list. Neither
`test_ab_compare.py` nor `test_run_threshold_eval.py` appears anywhere in the workflows:

```text
$ grep -Fn "test_ab_compare.py" .github/workflows/build.yml        # exit 1, no match
$ grep -Fn "test_run_threshold_eval.py" .github/workflows/build.yml # exit 1, no match
$ grep -n "pytest scripts/tests" .github/workflows/*.yml
.github/workflows/build.yml:529: run: python3 -m pytest <hand-typed list> -q
   (plus commented local-gate commands at :508, :511, :514)
```

Both files are stdlib-runnable (they import only `json`/`re`/`pathlib`/`importlib`/`os`/`sys` plus
pytest), so exclusion is not a dependency decision. **unverified (static reasoning):** the
consequence — a PR that breaks either test passes CI — was not run through GitHub Actions
(not runnable locally); the membership premise is executed grep output above. Fix direction: add
both files to the `scripts-harness` list (or a dedicated step); then the check actually blocks a
merge.

## Verdict

**PASS-WITH-FINDINGS — 0 blocker, 4 major, 0 minor (F4's CI consequence is `unverified (static
reasoning)`; everything else was applied and reverted on a /tmp copy).**

Both tests can fail for the right reason (P1–P5) and the step's core claims — custom names
round-trip into the results, per-arm dll/root reach the client, pick→arm restoration is real —
are genuinely pinned. The gaps are all of one shape: each assertion stops one seam short of the
observable it exists to protect (grader input rather than archive, payload content rather than
its label, production env wiring rather than the retained helper, CI execution rather than a
local run). None of them makes the suite's current checks vacuous, and each is closable with a
small assertion or a one-line workflow edit.

Findings go back to the implementation persona (`dotnet-engineer` per the routing; this step's
files are Python but the step is owned by the same implementer).