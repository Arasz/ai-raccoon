# The eight failure modes, worked

Each case below is real. They are recorded with enough mechanism that you can recognise the shape
in a different language, and each carries the control that actually closes it.

---

## 1. The circular benchmark

**What happened.** A deterministic prefix-matching filter was credited with 50/50 recall against a
corpus of "noise" strings. The corpus was built from the same source the filter matched — one
harness's background-process log lines. The filter could not lose to itself. That 50/50 then
became the argument for *deleting* the alternative approach, and a later review reused the same
corpus to argue the alternative was worthless.

Both directions were wrong for the same reason. The corpus measures the corpus.

**The tell.** Ask, of any evaluation set: *who generated these examples, and did the thing being
scored have access to that generator?* If the answer is "the same place the rule came from", the
score is a tautology with error bars.

**Control — held-out evaluation by family (required, not advised).**

1. **Name the family variable.** It is whatever generated the examples, not whatever labels them:
   tool family, source repository, operator, document type, endpoint, tenant. Two samples from the
   same generator are not independent, so a random split leaks.
2. **Partition by family, not by row.** Random 80/20 over rows puts siblings on both sides and
   inflates every metric.
3. **Leave one family out, rotate, report the spread.** A single held-out split can be lucky. The
   spread across folds is the number that matters; a mean that hides a fold at chance is a mean
   that will be quoted.
4. **Report in-sample and held-out side by side, always.** They are different claims.
5. **A number that does not survive the rotation does not ship.** On the source session a linear
   probe measured 0.946 AUC in-sample, on one operator and four repositories. It was not shipped;
   a leave-one-tool-family-out study was run first, and the detector waited for its result. Twice
   before, that project had shipped a detector validated against the shape it was built for.
6. **Watch for the memorisation signature.** A leader-follower centroid learner on the same
   traffic produced 1,583 clusters from 1,940 items — 93% singletons. A model that emits one
   cluster per example has memorised, and every in-sample metric will look excellent.

**Related trap: the corpus that cannot fail.** A gate corpus with 0 of 761 rows carrying a
structure vector cannot exercise the structure modality at all — every gate over it passes
regardless. The check is not "does the corpus look real" but *"if I deliberately break the thing
this corpus is supposed to test, does a gate go red?"* If not, the corpus is decoration.

---

## 2. The vacuous gate

Three shapes, all observed:

- **Assertion weaker than the measurement.** A metrics test computed nDCG, MRR and recall for
  every query and asserted only that each was *in range [0, 1]*. Every value was 0. The suite was
  green, the metrics were dead, and the report looked complete.
- **Always-false compared against always-false.** A corpus-integrity test compared a legacy
  `section` column — populated on 0 of 2518 rows — against a stale hash map. Both sides were
  empty. The comparison could only ever succeed.
- **The benchmark that discards its own results.** A write-performance benchmark wrote 50 valid
  records and threw away every return value, asserting only that 50 *invalid* ones were
  intercepted. A filter rejecting 100% of all input would have passed it unchanged — while its
  report printed `Rejection Accuracy: 100%`.

**Control.** Break the thing the gate exists to catch, watch it go red, restore it byte-identical,
watch it go green. Paste both. A gate nobody has watched fail is indistinguishable from one whose
comparison has a single answer.

**Two-step RED when the test cannot compile yet.** For a gate over an API that does not exist:
(1) land the member hardcoded to the wrong value, run the test, paste RED; (2) wire the behaviour,
paste GREEN. Without this, "the test could not compile so we skipped the red phase" quietly
becomes a gate nobody watched fail.

**Also check the assertion's floor, not only its ceiling.** A chunking gate asserting "no chunk
exceeds 254 tokens" is silent about the failure where an entire 3,899-character block collapses to
three tokens because the tokenizer treats newline as a non-separator — that chunk sails through
the ceiling check with a *tiny* count. Gates over a measured quantity usually need bounds on both
sides.

---

## 3. The specification that encodes the defect

Four separate cases on one session. Two representative:

- A `.feature` scenario, *"code fences are not split"*, asserted atomicity at a budget the fence
  could not fit. It was a transcription of what the chunker did, promoted to a requirement. The
  fix turned it red.
- A test named `ReadOptionsAsync_UnknownProviderValue_DefaultsToS3` asserted that a misspelled
  provider name silently resolved to a default backend — which is exactly the defect (a typo
  either shipped data to the wrong backend or made sync a silent no-op).

**Why it is dangerous.** The reflex when a fix turns a spec red is to restore the spec. That
re-lands the bug and now it has a signed contract.

**The adjudication step — do it explicitly, and record it.** For each red assertion ask:

1. Does any consumer depend on this behaviour? Find one, or accept there is none.
2. Is the assertion the *contract* someone agreed, or a *transcription* of what the code did when
   the test was written? Check the commit that introduced it: a test added alongside the code, in
   the same change, with no separate decision, is a transcription.
3. If it is a transcription, replace it — do not delete it. Split it: the original scenario at a
   parameter where it holds, plus a new scenario stating the corrected behaviour. Name the change
   in the commit message so the next reader sees the ruling, not a mysteriously edited spec.

---

## 4. Join defects

Five on one session, none visible in any branch alone:

- **A seeding helper that could no longer seed.** A schema test built a pre-migration bank by
  dropping the unique indexes, then inserting the duplicates the migration must collapse. Another
  branch added a third index. The helper did not drop it, so the test failed during *arrange* with
  a constraint violation — not during assert, so the failure message pointed nowhere near the
  cause.
- **A C# default-interface-method dispatch trap.** See the dotnet extension; the general shape is
  a member added on one side and a fake extended on the other, compiling cleanly and never being
  called.
- **A silently-dropped test.** A wholesale "take main's file" resolution removed two kill-switch
  tests that existed only on the other side. Nothing failed; the coverage simply left.
- **A reverted exit-code assertion.** The same resolution restored an assertion of exit code 1
  where the branch had deliberately changed it to a distinct code, so a script could tell a typo
  from a broken key. The build was green and the deliberate decision was gone.
- **A stale hash map.** Six test files pinned hashes from a generated map file. Regenerating the
  fixture invalidated all of them; the fix was to derive the expectations from the corpus at test
  time instead (see 8).

**Control.** After each merge, diff every resolved file against **both** parents and account for
every line that disappeared. "It compiles and the suite is green" does not detect a deletion of
something that had no counterpart.

---

## 5. In-sample versus held-out

Covered by 1's control. The additional discipline: **label every metric with its provenance at the
point of use**, not in a footnote. `0.946 AUC (in-sample, one operator, four repositories)` and
`0.946 AUC` are different claims, and only the first one survives being quoted in a decision
record six weeks later.

---

## 6. The finding that is a refutation

The most valuable output of the source session's review was a lane proving a briefed finding
*wrong*: an auto-TTL feature that had shipped with an ADR, a benchmark and a green test suite had
never once written a value. The mechanism was silent — the SQL statement's column list had no
`ttl_days` column and no matching placeholder, and the data-access library **silently drops a
parameter with no matching placeholder**. The feature was inert from the first commit.

It was found because an implementation lane's RED test *refused to go red*, and the lane treated
that as evidence rather than as a broken test.

**Three generalisable rules:**

- **A RED test that will not go red is a finding.** Stop and explain it before adjusting the test.
- **Any silent-drop mechanism deserves a derived gate.** For every statement, assert that each
  property of its parameter object has a matching placeholder in its text. Break it on purpose
  (add a bogus parameter), watch it go red. This is cheap and it closes a whole class.
- **Briefs must invite disagreement.** Say so in words. A lane told to investigate finding F3 will
  investigate F3; a lane told that disproving F3 is a first-class result will check whether F3 is
  true.

---

## 7. Stalled lanes and silent ratchets

**Unpushed work.** A lane can finish and appear to have produced nothing: its commits live only in
its worktree, and the integrating side sees no branch. Require a push after every commit, and
before concluding a lane failed, check its worktree for unpushed commits.

**Ratchets.** A pin on a growth metric — file line count, interface member count, a performance
floor — is a gate that turns silent growth into a decision. It only works if raising it costs
something. On the source session the same ratchet fired twice in one day; the second re-pin
carried an explicit raise history on the constant, recording both raises and saying that the next
person to hit it should do the deferred decomposition instead of raising again.

Rules: the failure message says *"split it, don't raise the cap"*; every raise appends to a history
comment on the constant with its date and reason; two raises in short succession is the signal that
the deferred work is now due.

---

## 8. Derive, don't pin

Every expectation that mirrors another artefact drifts the moment one side changes and nothing
compares them. Worked cases:

- A contract test asserting *"all 17 tools are still listed"* against a surface that had grown to
  25. Fixed by deriving the list from the tool attributes at test time — then proven by deleting a
  row and watching it go red.
- Six test files pinning hashes from a generated map. Fixed by deriving them from the corpus by a
  stable key (`source_file`, `heading_path`), which also survived a later change that altered every
  hash.
- A reference document listing the tool surface by hand, which had drifted to 23 of 25. Gated by a
  test that derives the list from the source of truth.
- A test proving every path-prefix query declares its escape clause, by enumerating the statements
  reflectively rather than listing them.

**The rule.** If you are about to write down something that already exists elsewhere in the repo,
write the derivation instead. If the derivation is impossible, that impossibility is itself the
finding.
