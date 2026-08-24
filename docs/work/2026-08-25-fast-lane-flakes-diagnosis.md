# Fast-lane CI diagnosis — 2026-08-25 (run 32785819720, main push)

**Task:** diagnose-fast-tests. Evidence sources: run 32785819720 (main, post-#585 merge) vs
32786146703 (PR, same head content) — both `dotnet test --filter "Speed=Fast&Performance!=Benchmark"
--no-build` on ubuntu-latest.

## What failed (2 tests, both flake classes, not regressions)

### 1. `VersionContractTests.PackedMcpServerJson_CarriesTheVersionFileVersion` — exit 134 (SIGABRT)

- The test runs a **full `dotnet pack` of the tool inside the fast lane** with a 3-min timeout
  (`tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs:87-91`).
- Crash: `System.AccessViolationException` in `Microsoft.Build.Shared.CoreClrAssemblyLoader`
  reading PE headers — the **documented host-crash class** (build.yml:179-213 records a SIGBUS-135
  test-host crash with no diagnostic evidence, and "a test-host segfault reads as 'Passed! N'
  followed by a bare exit code 1").
- **build-fast does NOT set the crash-dump env** (`DOTNET_DbgEnableMiniDump`) that build-slow has
  (build.yml:179-183) — the crash was untriagable; no dump was captured.
- A crashed `dotnet pack` can leave MSBuild node-reuse processes spinning, stealing the 2-vCPU
  runner for the remainder of the run — the likely amplifier of the slowdown below.

### 2. `NodeRunnerTests.ConcurrentStartsOnSamePort_ExactlyOneOwns_TheOtherAttachesOrReturnsPortInUse` — 0 successes

- The first assertion (`exits.ShouldAllBe(Success || PortInUse)`) **passed**, so **both** in-process
  servers returned `PortInUse` — a third bind took the port before either racer.
- Mechanism: `LoopbackPort.Reserve()` binds an ephemeral port; `ReleaseForBind()` returns it to the
  pool; the two racers bind **after** release (`NodeRunnerTests.cs:169-170`). Between release and
  bind, a parallel serve test's own `Reserve()` can be handed exactly that port. On a loaded runner
  (or one degraded by the pack crash's zombie nodes) the window widens and the steal becomes likely.
  Locally and on quiet CI runs the race is won in µs; the assertion is correct — the environment is
  the variable.

## Why the fast lane took 15 minutes

- The lane's `timeout-minutes: 15` (build.yml:92) is exactly at the observed ceiling. Normal test
  phase: **168s** (run 32786146703, same content, same runner type). The failing run's test phase
  ran **14m31s+** and the lane was **cancelled at 20:00** (32785819720) — bdd and slow lanes
  finished in ~2 min beside it.
- Two amplifiers, in order of suspicion:
  1. The crashed `dotnet pack` leaving MSBuild node-reuse processes alive on the 2-vCPU runner
     (node reuse is on by default; the workflow never sets `MSBUILDDISABLENODEREUSE`).
  2. Co-tenant load on the shared ubuntu-latest host (5-6x variance between identical runs is
     documented behaviour for 2-vCPU GitHub runners).
- The suite itself is 3358 tests incl. process/heavyweight integration tests (the pack, ~4
  ServeHarness files) that cost seconds each — fine on the dev machine (2m20s), brittle at the
  15-min budget under load.

## Recommended fixes (in order)

1. **Move `PackedMcpServerJson_CarriesTheVersionFileVersion` to `Speed=Slow`.** It is a
   pack-and-inspect, not a fast unit test; build-slow still gates every push (and carries the
   crash-dump env). Removes ~1-3 min of fast-lane wall time and the crash-prone heavyweight from
   the tightest lane. Any other `dotnet pack`-spawning test moves with it.
2. **Add the crash-dump env to build-fast** (mirror build.yml:179-183) so a host crash in the fast
   lane is triagable next time — this run's 134 left no evidence.
3. **Harden `ConcurrentStartsOnSamePort`**: retry the race on fresh ports (the contract is
   "exactly one owns"; a third-party steal is not a product defect) — or serialize the serve tests
   with a collection fixture. Retry-in-test is the smaller change.
4. **Set `MSBUILDDISABLENODEREUSE=1` on the CI test steps** so a crashed build/pack cannot leave
   zombie node processes degrading the rest of the run.
5. (Optional) Raise `build-fast` timeout to 20 min for headroom; the fixes above are expected to
   make it moot.

No product defect found: both failures reproduce the documented host-crash / port-race flake
classes; neither is caused by the merged code (the same head passed as PR run 32786146703).
