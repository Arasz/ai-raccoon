# Plan v2.1 — addendum closing the plan review's tail (R13–R17)

`PHASE5-PLAN-v2.md` is the reviewed plan; this addendum closes the five review findings that arrived
after v2 was written. Nothing here changes v2's waves or ordering.

## R16 — the merge marker is in a file P1.1 already edits (F1, formerly OPS-17)

`docs/reference/agent-memory-server.md:212` is exactly `>>>>>>> origin/main`, verified present at
`5bca1900`, and P1.1 edits that same file to add the memory-engine warning. Leaving a committed
conflict marker while editing the surrounding document is not defensible, and the ops review already
prescribed the fix four weeks ago.

**Add to Wave 4 (or fold into P1.1, since the file is already open):** delete line 212.
**Gate:** a repo-wide marker test — `git ls-files` piped through a grep for `^<<<<<<<|^=======$|^>>>>>>>`
must return nothing, with a fixture proving the test can see a planted marker (watched red: it finds
exactly this line today).

## R13 — F62 (the fake that drops `scope`) had no package of its own

v2 removed F62 from the mislabelled docs sweep but did not give it a package. It is a **test-fake
behaviour**: `tests/AiRaccoon.Tests/TestHelpers/FakeMemoryStore.cs:34-36` forwards to
`DeleteAsync(projectId, hash)` and discards `scope`, so the unit lane is blind to the sweep's
scoped-delete invariant — the M4 mutation reddens only the real-store lane.

**Add to Wave 4 (test-infrastructure row):** make the fake forward `scope` (and update
`FakeMemoryStoreTests` that pins the old forwarding as a contract).
**Gate:** re-run the M4 mutation (SweepService deleting scope `"workspace"` instead of `"project"`) and
assert the **unit** lane now reddens; today only `SweepScopeSiblingTests` does. Watched red: the unit
lane stays 7/7 green with the mutation applied.

## R14 — F66's corrected CI-dependency failure is unhandled

The Phase 3 correction (G6) established the real shape: 14 of 51 files are in CI; of the 35 collected
under the CI dependency set, **34 pass and one fails** —
`test_llamaindex_harness_cli.py::test_ingest_runs_as_module` (chromadb absent). v2's gate said "extend
the list or record a per-file reason" without deciding, so the package could land red while its
inventory gate goes green.

**Fix in v2's F66 row:** choose and state one of:
- **(a)** extend the CI list and add `chromadb` to the CI install line (the four heavy files plus this
  one become runnable); or
- **(b)** extend for the 30 files that pass unmodified and record an explicit exclusion reason for the
  five that need chromadb (the 4 documented + this one).

**Gate:** the file-inventory test asserts every `scripts/tests/*.py` is either in the CI list or in a
**bounded** exclusion map whose every entry carries a reason; plus a CI-dependency-set dry run proving
the extended list is green (this is the half v2 was missing — it never required the extension to pass).

## R15 — F63 does not define which package id satisfies "published"

The correction records that six 1.0.x releases live under the former `arasz.ai-raccoon` id while 15
have no package under either id. v2's reconciliation says "every `VERSION`/tag against nuget.org" —
a current-id-only check would report those six as failures and could not distinguish them from the 15.

**Extend owner decision 11:** is "published" satisfied by the current `ai-raccoon` id only, or by either
id for the versions that historically shipped under `arasz.ai-raccoon`? The reconciliation script and
its fixture gate must encode the answer, and the gate's fixture includes one former-id release and one
genuinely-missing release so the two classes cannot be conflated.

## R17 — F8's retry-diagnostic half names no mechanism

v2 gave F64 a runner (so the ledger is live again) and appended "(F8 gate)" for "a retry-recovered
failure emits a diagnostic". But F8's verified mechanism is that `RetryFact` recovery leaves **no
trace** (`RetrySurfaceGateTests.cs:20-38`; ~1838 retry-attribute sites), and the diagnostic emission is
a separate change in the retry machinery with no file named anywhere in the plan. A ledger runner alone
still cannot see "passed on the third try".

**Fix:** name the hook in the plan before implementing. The two workable shapes:
- **(a)** a custom retry attribute wrapping `xRetry.v3` that emits a `[LoggerMessage]`-style line per
  recovery (testable via the existing `FakeLogger`/`ActivityListener` seams), or
- **(b)** a runner-level sink (TRX or console) configured in the test project, with the gate reading the
  captured output.

The gate is written against whichever lands, and must be watched red against **today's** machinery
(zero occurrences of `retry|transient|attempt` in a recovered test's output).

## Decision list deltas

- **11 (F63)** — extended: which package id satisfies "published" (R15).
- **new 12 (F8/F64)** — revive the flake ledger *and* emit a retry diagnostic, or remove both halves
  deliberately (R17).
- **new 13 (F42)** — `quiet.log` single-writer ownership: serve owns it, or an OS-level lock (the plan
  review flagged this as shaping P1's file defect; it was already asked in the record, now listed).
- **new 14 (F26)** — is the chunk the intended delete unit, or must the write be deletable as a whole?
  (v2's gate said "or advertised N", which the review showed does not make the rest deletable.)
