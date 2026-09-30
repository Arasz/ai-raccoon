# Review-tests: copy-tool-pin — target WAL checkpoint + sha256 sidecar

Date: 2026-09-30 · Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `copy-tool-pin`.

Target: `scripts/tests/test_retrieval_tuning_copy.py`, the three tests the step names —

| Test | Changed |
|---|---|
| `test_sidecar_records_sha_counts_settings` | new |
| `test_target_has_no_pending_wal_frames` | new |
| `test_strict_read_only_live_source` | added as a PIN (the plan referenced a test that never existed) |

Code under test: `scripts/retrieval_tuning/make_memory_copy.py` — `checkpoint_target`
(`wal_checkpoint(TRUNCATE)` + `journal_mode=DELETE`), `sha256_file` (1 MiB chunked), `write_sidecar`,
`read_settings_snapshot` (credential-key filtering), `run_copy_and_verify`, `open_readonly`.

Step goal under review: a sanctioned copy is WAL-checkpointed on the TARGET and immediately
sha256-pinned in a sidecar (sha/counts/settings), and the live bank is only ever opened `mode=ro`.

**Verdict: PASS-WITH-FINDINGS — 0 blocker, 4 major, 2 minor.** The production code today is
correct and the three tests have real teeth (five of nine mutations I applied were killed,
including the PIN's writable-open and `mode=rw` controls). The gaps are all in what the fixtures
let the assertions observe, plus one gate-wiring gap.

## Scope and method

Out of scope, stated before starting: the file's other 16 tests; the implementer's RED-first
output (`KeyError: 'journal_mode'`, pin `.exists()` False) and reported 3 mutation kills (noted at
dispatch, not re-credited here); production-code security/layering/performance judgement. This is a
test verdict, not a production review.

- `.ai-badger/config.json:30` — `commands.test` = `dotnet test`. The .NET runner cannot see Python
  files; the only scripts coverage is the CI `scripts-harness` job (`.github/workflows/build.yml:529`),
  which names its test files explicitly. No pre-push hook, lefthook, Makefile or gate script runs
  pytest in this worktree (checked).
- Every mutation was applied to a byte-identical copy at `/tmp/rt-review` (structure preserved,
  `orig/` kept pristine), run there, and reverted; `cmp` confirms both worktree files match the
  reviewed copies. Nothing in the worktree was edited except this file.
- Baseline on the copy: `python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q` →
  `19 passed in 0.06s`.
- The mutations recorded in the table and detail below; bytecode caching disabled (`PYTHONDONTWRITEBYTECODE=1`) and
  `__pycache__` cleared before each run — an earlier same-size mutation round was served stale
  bytecode, so every result here is from a clean import.

## What the suite does well

- **The sha oracle is independent.** `test:379` recomputes
  `hashlib.sha256(target.read_bytes()).hexdigest()` from the final file; it does not call
  `sha256_file`. The constant-hash mutation (documented below) reddened exactly there, so this is a check that has
  been seen to fail:
  ```
  >  assert pin["sha256"] == hashlib.sha256(target.read_bytes()).hexdigest()
  E  AssertionError: assert '000000000000...' == '5ffb2d1bae96...'
  FAILED ...::test_sidecar_records_sha_counts_settings
  ```
- **The PIN has teeth.** A writable `sqlite3.connect(live_path)` inserted into
  `run_copy_and_verify` (the writable-open mutation) and a `mode=ro`→`mode=rw` change in
  `open_readonly` (the mode=rw mutation) both reddened
  `test_strict_read_only_live_source`. It also asserts a non-zero subject count
  (`assert source_opens`, `test:111`) before it can pass, which is `T1-PRF-03` done correctly.
- **The WAL test's durable-mode assertion is a real pin.** Replacing `journal_mode=DELETE` with a
  plain mode read (the no-mode-switch mutation) reddened it. This is the assertion that defeats the
  close-time-checkpoint artifact, and it does so because the mode lives in the database header.
- **The source-frames precondition is explicit** (`test:342`: the `-wal` file exists and is
  non-empty before the copy), so the test proves it had something to checkpoint.
- Seeded `random.Random(...)` everywhere, `tmp_path` isolation, no sleeps, no wall-clock
  assertions, no reflection into privates, and the `sqlite3.connect` spy delegates to the real
  function (an honest recording double, not a fake).

## Findings

| id | file:line | rule | severity | the mutation | run? | what it means |
|---|---|---|---|---|---|---|
| F1 | `scripts/tests/test_retrieval_tuning_copy.py:1` · `.ai-badger/config.json:30` · `.github/workflows/build.yml:529` | `T1-PRF-04` | major | none — wiring absence | gate trace (grep), not a mutation | No automated gate runs this file: `dotnet test` never sees Python, and the CI lane's explicit file list omits it. Every pin here is manual-only until wired. |
| F2 | `test:379` · `make_memory_copy.py:269` | `T2-UNIT-03` | major | hash the temp **before** `checkpoint_target` and pin that value | applied+reverted | The sidecar fixture never has a WAL source, so the checkpoint is a byte-no-op there; the WAL source test never asserts the pin. A pre-checkpoint sha survives the suite and produces a pin that does not match the real (WAL) copy — proven. |
| F3 | `test:379` · `make_memory_copy.py:200-206` | `T1-STR-02` | major | `sha256_file` digests only the first chunk (loop removed) | applied+reverted | Every fixture target is ~16 KB, far below the 1 MiB `chunk_size`; the chunk loop never iterates twice, so a multi-chunk hashing regression is invisible and the 848 MB bank's pin would be wrong. |
| F4 | `test:368,392` · `make_memory_copy.py:54,135` | `T1-STR-02` (privacy; cf. `T1-ORC-03`) | major | `_SECRET_KEY_MARKERS` narrowed to `("apikey",)` | applied+reverted | The fixture plants exactly one secret key (`embedding.apiKey`) — one of six declared markers. Five markers can stop matching with the suite green, and `embedding.api_key` (snake case) is **not** filtered by production today while the test is green. |
| F5 | `test:350,352` · `make_memory_copy.py:220` | `T1-ORC-01` | minor | delete `wal_checkpoint(TRUNCATE)`, keep `journal_mode=DELETE` | applied+reverted | `wal_frames == 0` cannot discriminate: the target never accumulates WAL frames, and `-1` is normalized to 0. The mode assert is the real tooth. The comment claiming a `mode=ro` open cannot create a shm/WAL is factually false. |
| F6 | `test:375-392` · `make_memory_copy.py:268,273,278` | `T1-SCO-07` | minor | pin `"verified": False` | applied+reverted | `pin["verified"]`, `sourcePath` and `vecEntries` are part of the sidecar contract but asserted nowhere; the pin can record `verified: false` with the whole suite green. |

`run?` caveat, stated once: F1 is the only non-mutation row — its evidence is the gate trace, not
an apply/run/revert, because the defect is an absent invocation rather than a production
expression. F2–F6 are all `applied+reverted`; outputs are quoted in the detail below.

## Detail

### F1 — major — the file is not in any gate

`commands.test` is `dotnet test` (`.ai-badger/config.json:30`). The one CI lane that runs
`scripts/tests` enumerates its files, and this file is not one of them:

```
$ grep -c "test_retrieval_tuning_copy" .github/workflows/build.yml
0
$ sed -n '529p' .github/workflows/build.yml | tr ' ' '\n' | grep -c '^scripts/tests/test_'
33            # 33 named files, this one absent
```

No `.lefthook/`, pre-push hook, Makefile or gate script invokes pytest in this worktree. The
practical effect: the two new tests and the PIN will only run when someone types the command by
hand, so the step's guarantees have no enforcer. That is `T1-PRF-04` (a gate whose failure blocks
nothing) applied at file granularity.

Fix: add the file to the CI list at `build.yml:529` — or better, derive that list from a directory
listing (`git ls-files 'scripts/tests/*.py'` filtered by the stdlib-runnable set) so the next new
file cannot be forgotten; the project already carries the `derive-or-delete-the-list` invariant.
Gate: remove the entry from the derived list and confirm the lane's collected count drops by the
file's tests, then restore. (Wiring, not a mutation run.)

### F2 — major — the sidecar sha is never verified against a checkpointed (WAL-derived) copy

`test_sidecar_records_sha_counts_settings` builds its source with `_make_fixture_live` in SQLite's
default DELETE mode (`test:360-368`); it never sets `journal_mode=WAL`. On that source the backup
target is already in DELETE mode, so `checkpoint_target` changes no bytes and a pin taken before
the checkpoint equals one taken after. `test_target_has_no_pending_wal_frames` does use a WAL
source, but it asserts only the mode/frames/read — never the pin sha.

Mutation applied (on the copy):

```python
    pre_sha = sha256_file(str(tmp_target))        # inserted before checkpoint_target
    wal = checkpoint_target(str(tmp_target))
    ...
    "sha256": pre_sha,                            # was: sha256_file(str(target))
```

```
$ python3 -m pytest ...::test_sidecar_records_sha_counts_settings -q
.                                                                        [100%]
1 passed in 0.03s
```

And the artifact-level impact probe, running the mutated production end-to-end with a WAL source
with pending frames:

```
M7 impact: pin sha == final file sha: False
M7 impact: pin sha: e39e97ef4ba4acf0 | final: d6f46c8943244dfc | report ok: True
```

A companion probe on a WAL-derived target shows why the fixture matters — the checkpoint really
does rewrite the bytes there:

```
C. checkpoint_target on WAL-derived target: {'journal_mode': 'delete', 'wal_frames': 0}
C. pre-checkpoint sha == post-checkpoint sha: False
C. bytes changed: True
```

So the suite stays green while the pin records a sha of an intermediate artifact that does not
match the file it names — exactly on the real live bank, which is WAL. The tests vary source mode
and pin content one axis at a time (`T2-UNIT-03`); the intersection is uncovered.

Fix: build the sidecar fixture's source in WAL mode with a pending frame (reuse the WAL test's
setup), so the sidecar assertions run against a style of copy whose bytes the checkpoint changes.
Gate: with that fixture, apply the mutation above; `test:379` must redden; restore and it must go
green.

### F3 — major — multi-chunk hashing is unobservable at fixture size

`sha256_file` reads in 1 MiB chunks (`make_memory_copy.py:200-206`) because the live bank is
~848 MB. Every fixture target is ~16 KB, so the loop body executes once and the loop's second
iteration — the only place a chunking bug can live — never runs.

Mutation applied: 

```python
    with open(path, "rb") as handle:
        digest.update(handle.read(chunk_size))    # loop removed
    return digest.hexdigest()
```

```
$ python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q
...................                                                      [100%]
19 passed in 0.06s
```

The mutation is a genuine defect, not an equivalent mutant — on an input larger than one chunk it
produces a different digest:

```
M6 impact: mutant sha == true sha on 2MiB+7 file: False
```

Consequence: a regression that truncates the read (first chunk only) or otherwise breaks chunk
accumulation ships green, and the sidecar pin for the real 848 MB copy is wrong. This is
`T1-STR-02`'s degenerate-fixture shape (rule severity is blocker; calibrated to major here because
the oracle is independent for single-chunk inputs and today's code is correct).

Fix: add a direct test of `sha256_file` over a file larger than one chunk with an explicit small
`chunk_size` (e.g. a 64-byte file hashed with `chunk_size=7`) against a hand-computed digest; the
existing end-to-end assertion stays. Gate: apply the mutation above; the new test must redden
while the 16 KB end-to-end test stays green, proving the new test is the one that closes the gap.

### F4 — major — one marker and one key spelling are tested, not the filter's declared set

Production declares six credential markers (`make_memory_copy.py:54`:
`apikey`, `secret`, `token`, `password`, `passwd`, `credential`). The fixture plants exactly one
secret-named key, `embedding.apiKey` (`test:368`), and the assertion checks that exact key name
(`test:392`).

Mutation applied: `_SECRET_KEY_MARKERS = ("apikey", "secret", "token", "password", "passwd", "credential")`
→ `_SECRET_KEY_MARKERS = ("apikey",)`.

```
$ python3 -m pytest ...::test_sidecar_records_sha_counts_settings -q
.                                                                        [100%]
1 passed in 0.03s
```

The suite does catch the filter being removed entirely (that mutation, `_is_secret_key → False`,
reddens — matching the implementer's own kill), and it catches the planted camelCase key. What it
does not catch: a regression that narrows the marker set, so `token`/`secret`/`password`/
`credential`-named settings flow into the plain-text sidecar with the suite green. A probe against
the real function shows the spelling hole concretely:

```
secret-marker probe:
  embedding.apiKey       -> filtered=True
  embedding.api_key      -> filtered=False
  embedding.hfToken      -> filtered=True
  auth.password          -> filtered=True
  x.credential           -> filtered=True
```

`embedding.api_key` is credential-shaped, reaches the sidecar unfiltered by production today, and
no test can see it. The production spelling gap belongs to the implementer/`code-reviewer`, not to
a test edit; the test-side gap is that the assertion is key-shaped and single-instance instead of
value-shaped over the declared domain.

Fix (test side): give the fixture one key per declared marker plus a snake-case variant, assert
each absent, and assert the secret **value** (`sk-secret-must-not-leak`) is absent from the whole
serialized sidecar (`pin_path.read_text()`), which catches a re-keyed leak too. Gate: apply the
marker-narrowing mutation and the snake-case fixture together; the value-level assertion must
redden; restore and go green.

### F5 — minor — `wal_frames == 0` cannot fail, and the `mode=ro` comment is false

`test_target_has_no_pending_wal_frames` claims three guards. Only one of them has teeth:

- `report["journal_mode"] == "delete"` (`test:349`) — real. Removing the mode switch
  (`journal_mode=DELETE` → plain mode read) reddens it: `E assert 'wal' == 'delete'`. This is what defeats the close-time-checkpoint
  artifact, because the mode is durable in the header.
- `report["wal_frames"] == 0` (`test:350`) — inert. Deleting `wal_checkpoint(TRUNCATE)` and keeping
  the mode switch leaves the test green. Structurally the value is 0 whether or not the checkpoint
  ran: the backup writes the destination's pages directly so the target accumulates no WAL frames,
  and `checkpoint_target` normalizes the `-1` "no WAL at all" result to 0. Removing the checkpoint
  is an equivalent mutant (the mode switch checkpoints), so the suite is right to stay green — but
  the assertion reads as proof that checkpointing happened and does not provide it.
- the `mode=ro` read (`test:353`) — the comment says "a mode=ro open cannot create a shm/WAL". A
  clean probe on a WAL-flagged target with no WAL file contradicts that:

  ```
  probe B: files before ro read: ['src.db', 'tgt.db']
  probe B: ro read count: 1 journal_mode: wal
  probe B: files after ro read : ['src.db', 'tgt.db', 'tgt.db-shm', 'tgt.db-wal']
  ```

  The read succeeds and creates `-wal`/`-shm`. It therefore does not discriminate WAL state; the
  `journalMode` assertion is the only guard, and it fires first. No defect escapes here.

Fix: correct the comment, and either drop `wal_frames` or say what it actually proves (that the
checkpoint call returned without error); if the "main file alone" property is to be asserted, check
the directory after the read.

### F6 — minor — three sidecar fields are unasserted

Mutation applied: `"verified": report["ok"]` → `"verified": False` in the pin dict.

```
$ python3 -m pytest scripts/tests/test_retrieval_tuning_copy.py -q
...................                                                      [100%]
19 passed in 0.06s
verified-field mutation: pin['verified'] is: False (suite has no assert on it)
```

`verified` is the field the work doc says makes a bad artifact traceable; a pin can record
`verified: false` next to a healthy report with the suite green. `sourcePath` (`:268`) and
`vecEntries` (`:273`) are likewise never asserted. Fix: assert `pin["verified"] is True`,
`pin["sourcePath"] == str(live.resolve())` (the resolver bug is the shape this catches) and
`pin["vecEntries"] is None`; gate: the `verified: False` mutation must redden.

## Noted, unverified (static reasoning)

Reported for completeness, not counted in the summary — no mutation was run for either:

- `write_sidecar`'s atomicity (temp file + `os.replace`) is claimed but untested; no test observes
  a reader during a write.
- The `pin_path` override is never exercised; the work doc's own Open items section flags the
  `--pin` path as first-execution-only. A one-line test passing a custom `pin_path` would close it.

## Verdict

**PASS-WITH-FINDINGS.** 0 blocker, 4 major, 2 minor (F1–F6).

The three tests pin real behaviour: the checkpoint's durable DELETE mode reddens when removed, the
read-only URI contract kills a writable open and a `mode=rw` regression, and the sidecar sha is
checked against an independent full-file hash. What the fixtures cannot see is the part of the
contract the real input exercises — a WAL-derived copy (F2), a multi-chunk file (F3), the filter's
other markers and a differently-spelled credential key (F4) — and one of the three advertised WAL
guards is inert (F5). F1 means none of this runs automatically today. None of the findings is a
blocker: the code under test is correct as committed, and each gap has a named mutation that
currently survives and would die under the proposed fix.

`run?` summary: F2–F6 applied+reverted on the /tmp copy with outputs quoted above; F1 is a gate
trace (grep/read), not a mutation. The RED-first evidence and the implementer's three kills at
dispatch were not re-verified here and are out of scope.