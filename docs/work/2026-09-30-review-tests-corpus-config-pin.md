# Review-tests: corpus-config-pin — live-tree band re-pin (262 files / 2 202 170 bytes at 73b0b0c0)

Date: 2026-09-30 · Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step
`corpus-config-pin` (commit `c47bc531`).

Target: `scripts/tests/test_corpus_config.py` — `MEASURED_FILES` 199→262, `MEASURED_BYTES`
1 608 970→2 202 170 (both now dated 2026-09-30 / tree `73b0b0c0` in the comment), the band comment
rewritten, and the two in-band assertion messages corrected. Code under test: `scripts/src/corpus_config.py`
(`select` — include globs intersected with `git ls-files`).

Step goal under review: the watched defect `test_byte_total_stays_in_band` was red against the live tree
and goes green at the true measurement, with the ±35% band unchanged and the pin's provenance honest.

**Verdict: PASS-WITH-FINDINGS — 0 blocker, 2 major, 1 minor.** The re-pin is exact: I reproduced
`select()` at tree `73b0b0c0` and got 262 files / 2 202 170 bytes, matching both constants and the
comment's arithmetic. The band still detects drift — old-pin bytes red, half/double-pin red in both
directions, restored green — and the watched defect reproduces exactly (bytes red, files green: +31.7%
stayed inside ±35%, +36.9% did not). The corrected messages now describe a live-tree pin rather than the
bank build, and the stale bank is disclosed. The two majors: the only test guarding the selection is not
wired into any gate, so main drifted past the old band with no CI signal; and the ±35% band admits a
complete loss of the `.ai-badger/skills/*/SKILL.md` glob (53 files / 546 815 bytes — 24.8% of bytes) with
every test in the file staying green, while the rewritten comment claims any stopped glob fails here.
One minor: "wide enough to absorb ordinary doc growth" is contradicted by the recorded +36.9% delta that
made the previous pin red.

## Scope and method

Out of scope, stated before starting: `docs-memory.db`, `CorpusFixtureGuardTests.cs` / the .NET fixture
guard (untouched, independently byte-pinned — I did not audit what it pins), the fixture-rebuild branch,
and the other eight tests in the file beyond their interaction with the changed constants. This is a test
verdict, not a production review.

- `.ai-badger/config.json:30` — `commands.test` = `dotnet test`. The `scripts-harness` CI job
  (`.github/workflows/build.yml:529`) runs an explicit list of Python test files; **this file is not on
  it** (see F1). No pre-push hook, lefthook, Makefile or gate script invokes pytest in this worktree
  (checked). `pyproject.toml:33-34` supplies `pythonpath = ["scripts/src"]` and `testpaths = ["scripts/tests"]`.
- Per the dispatch constraint, the real tree was not edited. Mutation runs used a hardlink clone of the
  worktree at `/tmp/cc-mut`: the two constants (or, for F2, one include glob) were changed after
  `rm`+`cp` broke the hardlink, and the clone's copy of the reviewed file was restored bit-for-bit.
  Verified: the real `test_corpus_config.py` sha256 is `515bda90…` before and after, and the real
  `corpus_config.py` sha256 is `181287a7…` before and after.
- Collection and baseline on the clone and the real tree are identical:

  ```
  $ python3 -m pytest scripts/tests/test_corpus_config.py --collect-only -q
  9 tests collected in 0.01s
  $ python3 -m pytest scripts/tests/test_corpus_config.py -q        # real tree, new pin
  .........                                                        [100%]
  9 passed in 0.14s
  clone live selection: 262 files, 2202170 bytes
  ```

## What the test does well

- **The new pin is the true measurement.** `git archive 73b0b0c0` extracted to `/tmp/tree73`, then the
  real `enumerate_files` + `corpus_config.INCLUDE_GLOBS` + `git ls-tree -r --name-only 73b0b0c0`
  reproduction of `select()`:

  ```
  73b0b0c0 selection: 262 files, 2202170 bytes  (pin 199→262 / 1608970→2202170)
  ```

  and `73b0b0c0..HEAD` touches no selected file (`scripts/tests/test_corpus_config.py` and
  `scripts/verify-809-report.py` are outside every include glob), so the live tree equals the pinned
  measurement today. The comment's arithmetic is exact too: 63 files / 593 200 bytes; +31.658% / +36.868%
  → the written +31.7% / +36.9%.
- **The band detects drift in both directions.** Three constant mutations on the `/tmp` clone, each run
  against the two band tests and restored afterwards:

  ```
  V1 — old pin (199 / 1_608_970, i.e. the watched defect):
       FAILED ::test_byte_total_stays_in_band
         E  AssertionError: 2202170 bytes selected; … pin is 1608970.
         E  assert 2202170 <= (1.35 * 1608970)
       1 failed, 1 passed            # byte red, file green — the recorded red→green is exactly this
  V2 — pin halved (131 / 1_101_085):
       2 failed                      # 262 > 1.35×131 and 2 202 170 > 1.35×1 101 085
  V3 — pin doubled (524 / 4_404_340):
       2 failed                      # 262 < 0.65×524 and 2 202 170 < 0.65×4 404 340
  restore pristine copy → 9 passed
  ```

  The watched defect is reproduced honestly: only the byte test caught the +36.9% growth; the file count
  at +31.7% was still inside `[129.35, 268.65]` under the old file pin.
- **The corrected messages say what is actually measured.** The pin is compared against the live tree
  (`select(REPO_ROOT)`, `test:45`), not against the committed bank, and the message now says so. The
  comment separately discloses that the committed `docs-memory.db` is still the 2026-08-22 build — its
  last commit is `00d381dd 2026-08-22 13:45`, so the disclosure is dated correctly.
- **The band bounds are computed from the pinned centre and checked with independent literals** (no
  oracle from the code under test); `select()` is deterministic (fixed globs, tracked-file intersection,
  sorted enumeration), so the test is not sampling the machine.

## Findings

| id | file:line | rule | severity | the mutation | run? | what it means |
|---|---|---|---|---|---|---|
| F1 | `scripts/tests/test_corpus_config.py:44-55` · `.github/workflows/build.yml:529` | `T1-PRF-04` | major | none — wiring absence | gate trace (grep/read), not a mutation | No automated gate runs this file: the CI lane enumerates its files and omits this one, and no hook invokes pytest. The band that exists to catch selection drift only executes when someone types the command — which is how the drift was in fact found (`.ai-badger/state.json:449,466`: "main was already over it"). |
| F2 | `scripts/tests/test_corpus_config.py:46,53` · `scripts/src/corpus_config.py:28` | `T1-ORC-03` | major | remove `".ai-badger/skills/*/SKILL.md",` from `INCLUDE_GLOBS` on the `/tmp` clone | applied+reverted | The whole reviewed file stays green at 209 files / 1 655 355 bytes while 53 skill docs / 546 815 bytes vanish from the corpus. The band's lower bound only catches the `docs/adr/*.md` loss (control: removing that glob reddens both band tests and both family tests); 13 of 14 single-glob losses pass the band, and the family check passes too (`.ai-badger` 54 > 50). The rewritten comment claims "a glob which stopped matching … fails here". |
| F3 | `scripts/tests/test_corpus_config.py:21-25` | `T1-CST-04` | minor | none — arithmetic on the comment's own recorded delta | unverified (static reasoning) | "wide enough to absorb ordinary doc growth" is contradicted by the very delta the comment records: the previous pin went red because bytes grew +36.9% in 39 days, above the +35% band. The band is a tripwire that will force periodic re-pinning, not a tolerance that absorbs this repo's observed growth. |

F2's per-glob sensitivity, computed from the exact `73b0b0c0` selection (only the changed rows shown):

```
glob                                   files      bytes   after loss: files/bytes        in band?  family checks?
docs/adr/*.md                            125    1182839        137 / 1019331              NO        docs>50 NO
.ai-badger/skills/*/SKILL.md              53     546815        209 / 1655355              yes       badger>50 yes (54)
.ai-badger/invariants/*.md                41      23392        221 / 2178778              yes       yes
everything else (11 globs)              ≤10    ≤183695        ≥252 / ≥2018475            yes       yes
docs/work (excluded by construction)     543   19963019        —                          —         (ingestion would be > upper bound)
```

F2 is a survivor by execution, not by reading: the mutant clone ran the full reviewed file green. The
control mutation (drop `docs/adr/*.md`) fails four tests — the band's own message plus both family
guards — which proves the clone harness reddens when the mechanism is observable, so M3's green is the
mutant surviving, not the harness failing to run.

## Detail

### F1 — major — the selection's only guard is not in any gate

`commands.test` is `dotnet test` (`.ai-badger/config.json:30`); it cannot see Python. The one CI lane
that runs `scripts/tests` enumerates 34 files and this one is absent:

```
$ grep -c "test_corpus_config" .github/workflows/build.yml
0
$ sed -n '529p' .github/workflows/build.yml | grep -o 'scripts/tests/[A-Za-z0-9_]*\.py' | wc -l
34            # named files on the single uncommented pytest run line
```

No `.lefthook/`, pre-commit config, Makefile or gate script invokes pytest. The practical effect:
`test_file_count_stays_in_band`, `test_byte_total_stays_in_band` and the family guards only run by hand,
so the step's fix has no enforcer — and the need for the fix was itself discovered manually
(`state.json` item 3: "re-measure test_corpus_config MEASURED_BYTES band (…main was already over it)").
That is `T1-PRF-04` at file granularity. Fix: add the file to `build.yml:529` — or, better, derive that
list from `git ls-files 'scripts/tests/*.py'` (invariant `derive-or-delete-the-list`); gate: remove the
entry and confirm the lane's collected count drops by the file's 9 tests, then restore. Wiring, not a
mutation run.

### F2 — major — the ±35% band admits a whole-glob loss, and the comment says otherwise

The rewritten comment (`test:25-26`) keeps the original clause: "narrow enough that a glob which stopped
matching, or one that started swallowing docs/work, fails here instead of quietly hollowing out every
rank gate downstream." Measured per-glob, the clause holds for exactly one glob. The skills glob is the
sharpest counterexample because of what it is: 53 `SKILL.md` files carrying the framework guidance every
future fixture bank is built from.

Mutation applied on the clone (the real `corpus_config.py`'s sha unchanged — `181287a7…`):

```diff
 INCLUDE_GLOBS: list[str] = [
     "docs/adr/*.md",
     …
-    ".ai-badger/skills/*/SKILL.md",
     ".ai-badger/delegation.md",
```

```
$ python3 -m pytest scripts/tests/test_corpus_config.py -q     # whole file, mutant clone
.........                                                      [100%]
9 passed in 0.13s
mutated selection: 209 files, 1655355 bytes
```

Nothing in the file reddens: both bands hold (209 ∈ [170, 354]; 1 655 355 ∈ [1 431 411, 2 972 930]),
`test_both_document_families_are_present` holds (`.ai-badger` 54 > 50), `test_adrs_are_the_backbone`
holds, and `test_skill_reference_files_are_not_selected` checks only `/references/` paths. The fixture
guard (`.NET`, byte-pinned) would see it only if the bank were rebuilt — a separate action this test is
supposed to precede. Fix (test side): assert per-family floors, not just >50 for the whole
`.ai-badger` tree (e.g. `len(skills) > 40` alongside the two existing family counts), or partition the
band by include glob per `T1-ORC-03` rather than widening one tolerance; gate: the skills-glob mutation
above must redden the new assertion, then restore and go green.

### F3 — minor — "absorbs ordinary growth" vs its own +36.9%

The comment's justification reads "wide enough to absorb ordinary doc growth". The same comment records
that bytes grew +593 200 (+36.9%) since the 2026-08-22 pin — above the +35% upper band — which is why
this commit exists. The band did not absorb the last observed growth; it tripped on it. That is a fine
design for a tripwire (it forces a deliberate re-measure every few weeks), but the sentence should say
that, not claim the width covers growth this repo demonstrably produces. The sentence is inherited from
the previous comment; the rewrite was the moment to correct it.

## Noted, unverified (static reasoning)

- The .NET fixture guard's exact pin (`tests/AiRaccoon.Tests/Integration/BaselineMetricsTests.cs` reads
  the committed bank) was confirmed to exist but not read line-by-line; F2's mitigation note ("only at
  rebuild time") rests on that file existing, which `grep` confirms.
- F1's fix proposal's count-drop gate was not exercised (wiring, not a mutation).

## Verdict

**PASS-WITH-FINDINGS.** 0 blocker, 2 major, 1 minor (F1–F3).

The re-pin itself is correct and honest: constants equal the reproduced measurement at `73b0b0c0`, the
arithmetic and the stale-bank disclosure check out, the band still goes red against drift in both
directions, and the watched defect reproduces exactly (bytes red at the old pin, files green). What the
step leaves behind is the shape of the guard, not its centre: the selection has no automated enforcer at
all (F1), and the band's coarse tolerance — combined with the `>50` family floor — cannot see the loss of
the 53-file skills glob it is advertised to catch (F2). Neither hides a defect on today's tree; both are
about what reddens before the tree drifts.