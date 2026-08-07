---
name: dotnet-flaky-test-diagnosis
description: "Use when a .NET test fails in the full suite but passes alone (or flakes intermittently): classify via the ladder — intra-test race (lock-guard fake collections), inter-test contention (xunit v3 DisableParallelization collections), or environmental flakes (child PATH/env, cold-worktree asset provisioning) — before blaming the branch. Includes the clean-main baseline check and gate discipline."
description: Use when a test fails in the full suite but passes alone.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, testing, flakes, diagnosis]
    related_skills: [dotnet-hosted-service-testing, systematic-debugging]
---

# .NET flaky-test diagnosis

A test that fails in the full suite but passes in isolation is a PARALLELISM flake, not
a regression — unless it fails isolated too, in which case it is a real bug and this
skill does not apply.

## Classification ladder (run in this order)

1. **Reproduce in the full suite** — note the exact test names (`grep "FAIL\]" log`).
2. **Isolated re-run** (`dotnet test --filter "FullyQualifiedName~X"`): passes 1/1 → flake
   candidate; fails → real bug, stop.
3. **Clean-main baseline**: run the same test on pristine origin/main (no branch changes).
   Fails there too → pre-existing flake, NOT caused by your branch — do not chase it as
   your regression. Cleanest method in a multi-session repo: a THROWAWAY WORKTREE at the
   base commit — `git worktree add /tmp/base-check-<sha> <base-sha>` (plus any
   gitignored runtime assets the suite needs, e.g. an ONNX model), run the failing test
   there, then `git worktree remove --force`. This never touches the shared main checkout
   (which other sessions may be using) and survives stash-less. Evidence: the same test
   name failing at base = pre-existing; passing at base but failing on your branch = yours.
4. **Classify the root cause** (the two classes below). The discriminator: does the test
   itself fan out concurrent work (intra-test), or does it use real I/O with wall-clock
   deadlines (inter-test)?

## Class A — INTRA-test race: concurrent work writes shared test state

The code under test runs jobs concurrently (Task.Run, parallel batches, async fan-out)
and the TEST FAKE records results into a plain `List<>` / `Dictionary` / counter.
Concurrent `Add()` on an unsynchronized `List` loses entries under load.

- Symptom: count assertions fail (e.g. `Ingested.Count` 1 vs expected 3); passes ~2/3
  isolated; fails consistently under full-suite load.
- FIX: lock-guard the fake's collections — never serialize the production scheduler
  (the concurrency is by design; the fake must tolerate it):

```csharp
private readonly object _sync = new();
public List<(string ProjectId, string Path, string Content)> Ingested { get; } = [];
// in the fake method:
lock (_sync) { Ingested.Add((projectId, path, content)); }
```

## Class B — INTER-test contention: real I/O with wall-clock deadlines

The test drives real infrastructure (FileSystemWatcher, real SQLite, subprocesses) with
poll loops bounded by wall clock (`DateTime.UtcNow.AddSeconds(n)`). Under full-suite
parallel load other collections starve the CPU and the deadline expires.

- Symptom: different tests of the SAME class fail on different runs (~1 in N); each
  passes isolated; no code path is actually wrong.
- FIX: serialize the class with an xunit v3 collection (verified against
  https://xunit.net/docs/running-tests-in-parallel — the attribute is
  `DisableParallelization`, NOT `DisableParallelism`; the latter is the v3-only
  per-test/[TestClass] variant):

```csharp
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatchIntegrationCollection
{
    public const string Name = "watch-integration";
}

[Collection(WatchIntegrationCollection.Name)]
public sealed class WatchIntegrationTests { ... }
```

- Document WHY in the collection definition (wall-clock deadlines / shared env / real
  I/O) — the reason is the contract that keeps the serialization justified.
- Match the project's existing pattern if one exists (e.g. an E2E collection with the
  same shape) — consistency across collections beats bespoke names.

## Class C — environmental flakes (process/env/network)

Child-process tests, ambient env vars (a real bws/aws CLI on PATH leaking in), port
collisions. Fix = hermetic child PATH / env isolation, or record-and-tolerate with the
owner's approval.

**Cold-worktree asset provisioning (seen 3x 2026-08-07).** A freshly created worktree's FIRST full-suite run can fail ~48 tests — ALL in the retrieval/reference-assets area, with `System.IO.FileSystem.CopyFile` inside
`ReferenceAssets.EnsureAssetAsync` (pinned native modules are gitignored and copied into
`bin` on first run; a transient copy failure on the cold tree fails the whole cluster). The SECOND run on the same tree passes (assets persist). Discriminator vs a real regression: failure count clusters in asset-using tests, the exception
is a file copy not an assertion, and any re-run is green. Do NOT rebuild a baseline worktree for this — the re-run IS the evidence; record "cold first run failed, warm run green" and move on. (Also matches the repo history note: "44 initial
failures were the gitignored ONNX model missing from the fresh worktree at build time — environmental.")

## Gate discipline

- Per-PR gates: a full-suite run showing ONLY the recorded known flakes counts GREEN
  after an isolated re-run (owner record-and-tolerate policy); any OTHER failure is RED.
- Fix flakes as their OWN PR (one-PR-per-task): a flake fix is a separate unit of work
  with its own evidence, not a rider on the feature/refactor PR.
- When your branch LACKS the flake fix, expect OTHER tests of the same class to flake in
  turn (same root cause, different name) — don't re-diagnose each one; pull the fix in.
- After the fix, the discriminating evidence is: fail in full suite pre-fix, pass in
  isolation post-fix, AND pass in a fresh full-suite run.
- **Verify against the branch the changes are actually on.** A worktree checkout can sit
  on a different branch than the one you're verifying (e.g. worktree on PR G while PR F's
  files live on another branch). An ad-hoc verification script that greps for files will
  then FAIL with "file not found" even though everything is correct — the grep ran in the
  wrong working tree. Check `git branch --show-current` (and that the files exist in THAT
  tree) before reading a script's failures as real; re-run it on the right checkout. The
  full-suite gate run is branch-bound evidence — record which commit it ran on.
- **Concurrent-session half-fix breaks origin/main — your gate inherits the RED.**
  In a multi-session repo (verified 2026-08-06 twice): another session renames something
  (consts, methods, tool names) and updates only PART of the test contract (e.g. the
  filter but not the assertion), pushing a state where origin/main itself fails the full
  suite. Your branch, merged with that main, inherits the failure — it is NOT your
  regression. Diagnose by comparing the committed source against the test expectation
  (`git show origin/main:src/... | grep <old-name>` vs the test's filter/assert), then:
  1. File the fix as a tiny separate PR (one line, same class as the half-fix) so main
     is green again — do not bundle it into your feature PR (scope).
  2. Merge that fix INTO your branch and re-run the gate.
  3. If the other session's WIP is still uncommitted in the shared checkout, verify your
     branch against the COMMITTED state — the working tree may carry their in-flight
     edits that mask or change the failure.

## Case study

`references/watch-flake-fixes-2026-08-06.md` — two watch tests, both classes
A and B in one session, the full diagnosis transcript, and before/after evidence.

## Gotchas

- Run the clean-main baseline check BEFORE blaming the branch — a failure on clean main is not your change.
- Classify via the ladder first: intra-test race, inter-test contention, then environmental — misclassification sends you down the wrong fix.
