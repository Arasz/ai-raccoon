# Stack — .NET / xUnit v3

Layer 3. Every rule below specialises an L1 (`T1-*`) or L2 (`T2-*`) rule named in
`parent:`. The pre-consolidation id that named it in the research lanes survives
only in `absorbs:` — that is the dedup map, and it is what lets a reviewer
holding a research-lane id resolve it to this file (governance.md, id scheme). Assumes
.NET 8+ / xUnit v3 unless a rule states otherwise.

**Cite, do not restate.** When the `dotnet-test` plugin is installed, reach for
it instead of re-deriving its output by hand:
- static smell census → `dotnet-test:test-anti-patterns`, **when the `dotnet-test` plugin is installed**
- per-assertion classification (not the letter grade — ruling C19) → `dotnet-test:assertion-quality`, `dotnet-test:grade-tests`, **when the `dotnet-test` plugin is installed**
- untestable-statics report → `dotnet-test:detect-static-dependencies`, **when the `dotnet-test` plugin is installed**
- risk ranking → `dotnet-test:crap-score`, `dotnet-test:coverage-analysis`, **when the `dotnet-test` plugin is installed**
- apply/run/revert discipline on a survivor → `dotnet-test:test-gap-analysis` step 4b, **when the `dotnet-test` plugin is installed**

## Runner and tooling facts
- **Commands:** `dotnet build`, `dotnet test --filter "RequiresInfra!=true"` (or the project's
  equivalent trait filter) — a scoped filter in every lane, never the full suite (`T3-NET-08`).
- **Packages:** xUnit v3 (`xunit.v3`, `xunit.runner.visualstudio` ≥ 3.0.2); `FakeTimeProvider` from
  `Microsoft.Extensions.TimeProvider.Testing`; `Testcontainers.*` for real dependencies; Verify for
  snapshots; NSubstitute (or the house double library) for one-off stubs; `Reqnroll` +
  `Reqnroll.xunit.v3` for BDD (never `Reqnroll.xUnit`, which is v2-only).
- **Blind spots:** the EF Core InMemory provider is not SQL — it ignores transactions by default, so
  any behaviour depending on rollback semantics is untested by construction (`T3-NET-19`). A Cosmos
  or Azurite emulator does not model RU consumption, the production consistency level, or
  cross-partition query cost, so a passing emulator test is silent about all three. Stryker's
  `static readonly` field initialisers run once at type load, before the per-mutant switch activates,
  and report false survivors (`T3-NET-30`); `--namespace` expands to one directory deep and can mutate
  zero types while exiting green (`T3-NET-31`).

## Which runner observes which behaviour

| Behaviour under test | Runner / harness | Blind if the wrong one is used |
|---|---|---|
| Pure logic, mapping, validation | xUnit v3 unit, no I/O | — |
| Repository query text, partition-key argument, tenancy predicate | Testcontainers or the real backend/emulator | EF InMemory ignores transactions; an emulator doesn't model RU or the production consistency level |
| Full HTTP pipeline: routing, model binding, auth, validation, serialization | `WebApplicationFactory<Program>` | A hand-built host or an over-replaced `ConfigureWebHost` skips real middleware wiring |
| Multi-service startup, resource readiness ordering | `DistributedApplicationTestingBuilder` + `WaitForResourceAsync` | A sleep-based wait cannot observe `KnownResourceStates.Running` and races the container |
| Mutation survivor verification | Stryker, scoped `--file`, plus a manual mutate-run-restore | `static readonly` survivors and `--namespace`'s one-directory-deep expansion both under-report |
| Architecture boundary rules | ArchUnitNET with every forbidden-type assembly explicitly loaded | An unloaded assembly makes the rule pass over an empty set |
| Load/stress | NBomber or `dotnet/crank`, Release configuration only | Debug-configuration timings are not representative of production |
| BDD acceptance | Reqnroll + `Reqnroll.xunit.v3` | `@ignore` is special-cased at generation time; every other tag becomes a `Category` trait, not a skip |

---

## xUnit v3 harness

#### `T3-NET-01` — Serialize a class with `[CollectionDefinition(Name, DisableParallelization = true)]`; never the per-test `DisableParallelism`.
- *design:* reach for the collection-level attribute when a shared resource forces serialization.
- *review:* grep both spellings; `DisableParallelism` compiles and does nothing at the collection level.
- *check:* `grep -rn "DisableParallelization\|DisableParallelism"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-05` · *absorbs:* `N-XU-01` · *cites:* `dotnet-flaky-test-diagnosis/SKILL.md` Class B

#### `T3-NET-02` — Pass `TestContext.Current.CancellationToken` into every awaited call that accepts one.
- *design:* thread the ambient token through instead of leaving the overload's default.
- *review:* grep `await` lines calling a `CancellationToken`-overloaded method with no token argument.
- *check:* `grep -rn "await .*(" tests/ | grep -v CancellationToken`.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T2-UNIT-06` · *absorbs:* `N-XU-02` · *cites:* xUnit v3 docs

#### `T3-NET-03` — `IAsyncLifetime.InitializeAsync` returns `ValueTask` in xUnit v3; `DisposeAsync` overrides `WebApplicationFactory`.
- *design:* match the v3 signature; a v2-shaped one compiles as a new method and never runs.
- *review:* a fixture that "never starts its container" is frequently this, not an infra failure.
- *check:* `grep -rn "Task InitializeAsync"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-06` · *absorbs:* `N-XU-03` · *cites:* dotnet-claude-kit `testing`

#### `T3-NET-04` — `[Theory]` with `[InlineData]`/`[MemberData]` instead of duplicated `[Fact]`s; `MemberData` sources are deterministic and order-independent.
- *design:* parametrize instead of copy-pasting near-identical facts.
- *review:* a `MemberData` source that shuffles or depends on wall-clock order is a hidden flake source.
- *check:* does the `MemberData` source read `DateTime.Now`, an unseeded `Random`, or enumerate a `Dictionary`/`HashSet` (order not guaranteed)? Evidence: read the source method's body.
- **severity:** minor · **evidence:** strong · **flag:** argued
- *parent:* `T1-STR-01` · *absorbs:* `N-XU-04`

#### `T3-NET-05` — Categorise with `[Trait]`, and enforce the trait's spelling and placement with a reflection meta-guard.
- *design:* one meta-test asserts the set of infra-traited types equals the set that touches infra.
- *review:* a misspelled trait is silently included by a filter and silently excluded by its complement.
- *check:* does a meta-test assert that equality?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-CST-03` · *absorbs:* `N-XU-05` · *cites:* `evidence.md` (test-suite-shape)

#### `T3-NET-06` — One assertion library, house-wide; check the census, not the style guide.
- *design:* pick one (Shouldly, or plain `Assert`) and never introduce a second.
- *review:* `grep -roh 'Should[A-Za-z]*(\|Assert\.\|\.Should()' tests/ | sort | uniq -c` — a real split is a finding, a style guide is not evidence either way.
- *check:* the census command above.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-SCO-02` · *absorbs:* `N-XU-06` · *cites:* `evidence.md` (test-suite-shape)

#### `T3-NET-07` — Rebuild after editing tests before judging a failure; `--no-build` against a stale assembly reports phantom failures.
- *design:* n/a (a review-time discipline).
- *review:* before triaging any red run, confirm the assembly under test reflects the current source.
- *check:* did `dotnet build` run since the last test edit?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ISO-09` · *absorbs:* `N-XU-07` · *cites:* `dotnet-hosted-service-testing/SKILL.md` item 9

#### `T3-NET-08` — In a lane, run a scoped filter — never the full suite.
- *design:* every lane brief states the filter; a full local sweep buys no coverage the pipeline lacks.
- *review:* flag a lane script invoking `dotnet test` with no `--filter`.
- *check:* `grep -n 'dotnet test' <lane script>` with no `--filter` on the same line is the violation.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-02` · *absorbs:* `N-XU-08` · *cites:* `evidence.md` (never-run-full-suite-in-lanes); invariant `pipeline-runs-the-rest`

## Async correctness

#### `T3-NET-09` — No `async void` tests, and no `async void` in code under test.
- *design:* production async that must run in a fire-and-forget context uses a logged wrapper, never `async void`.
- *review:* `grep -rn "async void"` — a test hit is invisible-on-failure; a production hit crashes the process.
- *check:* the grep above.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-01` · *absorbs:* `N-ASY-01` · *cites:* dotnet-claude-kit `verify` Phase 3

#### `T3-NET-10` — Never `.Result` / `.GetAwaiter().GetResult()` / `.Wait()` in a test.
- *design:* `await` all the way down.
- *review:* sync-over-async turns a real failure into a deadlock or an obscuring `AggregateException`.
- *check:* `grep -rn "\.Result\b\|GetAwaiter().GetResult()\|\.Wait()"`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-05` · *absorbs:* `N-ASY-02` · *cites:* dotnet-claude-kit `verify`

#### `T3-NET-11` — Every async assertion is awaited: `await Assert.ThrowsAsync<T>(...)`, `await Should.ThrowAsync<T>(...)`.
- *design:* return/await the assertion call, always.
- *review:* an un-awaited async assertion silently passes even when the check would have failed.
- *check:* `grep -rn "Assert.ThrowsAsync\|ShouldThrowAsync\|Should.ThrowAsync"` lacking `await`/`return`.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-01` · *absorbs:* `N-ASY-03` · *cites:* `dotnet-test:test-anti-patterns` (Critical), `dotnet-test:grade-tests`

#### `T3-NET-12` — Do not sprinkle `ConfigureAwait(false)` in test bodies; audit it in the library under test instead.
- *design:* leave test bodies alone; add it in shipped library code where a sync context could deadlock.
- *review:* an xUnit v3 test body has no ambient sync context — flagging `ConfigureAwait` there is a false positive.
- *check:* `grep -rn "ConfigureAwait" tests/` — a hit inside a test project body is not itself a finding (no ambient sync context there); the same grep against library/production code under test is the actual audit target.
- **severity:** minor · **evidence:** weak · **flag:** argued
- *parent:* `T1-CST-02` · *absorbs:* `N-ASY-04`

#### `T3-NET-13` — Cancellation is behaviour: at least one test cancels mid-flight and asserts both the exception and the absence of a partial write.
- *design:* the cancel-path test is not optional wherever a `CancellationToken` is honoured.
- *review:* a cancel test that only checks the exception type misses whether the aggregate was left half-written.
- *check:* does the cancellation test assert both the exception type and a subsequent state read (no partial write), not the exception alone? Evidence: read its assertions.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-SCO-04` · *absorbs:* `N-ASY-05` (archetype B10) · *cites:* `evidence.md` (incomplete-state-transitions)

## Time

#### `T3-NET-14` — `TimeProvider` is injected; tests use `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.
- *design:* seam the clock at the constructor; production reads no ambient time.
- *review:* `grep` production for `DateTime.UtcNow`; a correctly-seamed service has no reason to hold one.
- *check:* the grep above, plus a hand-rolled `IClock` that `TimeProvider` would replace.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-ISO-03` · *absorbs:* `N-TIME-01` · *cites:* MS Learn `TimeProvider` overview

#### `T3-NET-15` — An `Advance` immediately after `StartAsync` is lost; sync on the service's own emitted event, not on a delay.
- *design:* poll a `FakeLogger` collector for the service's own startup event id rather than advancing right after start.
- *review:* `grep` for `Advance(` on the line after `StartAsync`, and for `Task.Delay(50)`/`(100)` used as a settle.
- *check:* the grep above; the fix is `WaitForLogAsync(eventId, count)`.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-02`, `T1-ORC-03` · *absorbs:* `N-TIME-02` · *cites:* `dotnet-hosted-service-testing/SKILL.md` items 1-4, 11

#### `T3-NET-16` — Derive a watchdog's poll tick from its timeout (`tick = min(60s, timeout/4)`), and pin it with a short-timeout test.
- *design:* size the tick as a fraction of the timeout, not a fixed constant.
- *review:* a fixed 60s tick makes a short-timeout gate unsatisfiable and its early-shutdown assertion vacuous.
- *check:* is the tick a fixed constant, or derived as a fraction of the configured timeout (e.g. `timeout/4`)? Evidence: read the constructor; pair with a short-timeout test that must observe an early check.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ORC-06` · *absorbs:* `N-TIME-03` · *cites:* `dotnet-hosted-service-testing/SKILL.md` item 10

#### `T3-NET-17` — Pin a `PeriodicTimer` cadence with split advances; one large advance cannot observe the tick size.
- *design:* keep the clock below the deadline until cadence is established, then cross it.
- *review:* a single large advance fires immediately regardless of tick size and asserts nothing about cadence.
- *check:* does the cadence test perform several small `Advance()` calls before crossing the deadline, or one large jump? Evidence: read the `Advance()` call sequence.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-ORC-06` · *absorbs:* `N-TIME-04` · *cites:* `dotnet-hosted-service-testing/SKILL.md` item 7

#### `T3-NET-18` — `StopApplication` does not stop a `WebApplication` host — assert the runner-shaped chain, not `ApplicationStopped` directly.
- *design:* assert `ApplicationStopping` / `WaitForShutdownAsync`; `ApplicationStopped` needs the runner's own `StopAsync()`/dispose.
- *review:* a "the watchdog shut the host down" test asserting `ApplicationStopped` directly claims more than the contract promises.
- *check:* does the shutdown test assert `ApplicationStopped` directly, or the `ApplicationStopping`/`WaitForShutdownAsync` chain plus the runner's own `StopAsync()`? Evidence: read the assertion against the runner's documented lifecycle.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-CST-02` · *absorbs:* `N-TIME-05` · *cites:* `dotnet-hosted-service-testing/SKILL.md` item 8

## Data access

#### `T3-NET-19` — Do not use the EF Core InMemory provider. Use SQLite in-memory, Testcontainers, or the repository pattern.
- *design:* pick a real-transaction-capable substitute from the start.
- *review:* `grep -rn "UseInMemoryDatabase"` — Microsoft's own guidance calls it strongly discouraged; it ignores transactions by default.
- *check:* the grep above.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-05` · *absorbs:* `N-DATA-01` · *cites:* MS Learn — testing without the database

#### `T3-NET-20` — Testcontainers images are pinned to a tag matching the production engine's major version; one container per fixture, not per test.
- *design:* share the container across the fixture's class; pin the tag.
- *review:* a container spun up per test method is both slow and a namespacing hazard.
- *check:* `grep -rn 'WithImage\|new .*Container(' tests/` — a floating tag with no version pin, or a container constructed inside a `[Fact]` body rather than `IAsyncLifetime.InitializeAsync`, is the violation.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-06` · *absorbs:* `N-DATA-02` · *cites:* dotnet-claude-kit `testing`

#### `T3-NET-21` — Every repository interface has a contract suite bound to both the fake and the real backend, and a meta-test fails when one is missing.
- *design:* write the contract suite once, parametrized over both implementations.
- *review:* this is what catches a `since`-filter or ordering divergence between an in-memory fake and the real store.
- *check:* does a meta-test enumerate repository interfaces and assert both bindings exist?
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *parent:* `T1-DBL-01` · *absorbs:* `N-DATA-03` · *cites:* `evidence.md` (test-suite-shape)

#### `T3-NET-22` — Query text, partition-key arguments and tenancy predicates are proven against the real store, and the lane that proves them is named.
- *design:* name, in the test, which lane exercises the real backend for this query.
- *review:* grep the method name across unit fakes and integration suites — if only the fake references it, the query is untested.
- *check:* the grep above.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T2-INTG-03` · *absorbs:* `N-DATA-04` · *cites:* `evidence.md` (test-suite-shape); `dotnet-hosted-service-testing/SKILL.md`

## HTTP surface

#### `T3-NET-23` — Prefer `WebApplicationFactory<Program>` over a hand-built host; replace only boundary services.
- *design:* swap the handler, never more; one factory test then covers routing, binding, validation, filters, serialization and persistence together.
- *review:* count `RemoveAll`/`Replace` calls in `ConfigureWebHost` — each is a claim about production no longer tested here.
- *check:* the count above.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T0-08`, `T2-INTG-03` · *absorbs:* `N-WAF-01` · *cites:* dotnet-claude-kit `testing`

#### `T3-NET-24` — Authentication and identity resolution must be exercised through the real pipeline by at least one test.
- *design:* add one test that sends the raw identity header through the real pipeline, and a negative one that sends none and asserts 401.
- *review:* if every API test sets the user id by hand on the context, the resolver is bypassed everywhere — a middleware regression there is a total auth bypass, not a payload-injection concern.
- *check:* grep tests for direct principal/user-id assignment; if that is all of them, this is a finding.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-SCO-06`, `T1-CST-04` · *absorbs:* `N-WAF-02` · *cites:* `evidence.md` (test-suite-shape)

#### `T3-NET-25` — Outbound HTTP is faked at the `HttpMessageHandler`/WireMock boundary — never a real network call, never a mocked `HttpClient` type.
- *design:* inject `IHttpClientFactory`; fake the handler, not the client interface.
- *review:* a mocked `HttpClient` type is mocking a type the test doesn't own (Google TotT).
- *check:* `grep -rn 'Substitute.For<HttpClient>\|Mock<HttpClient>'` — any hit mocking the `HttpClient` type itself, rather than `HttpMessageHandler`/`DelegatingHandler`, is the violation.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-DBL-03`, `T1-ISO-07` · *absorbs:* `N-WAF-03` · *cites:* dotnet-claude-kit `testing`

#### `T3-NET-26` — Assert the response contract: status code, `Location`/headers, problem-details shape — not only the body's happy field.
- *design:* assert the envelope alongside the payload.
- *review:* a test that only reads the happy field misses a status-code or header regression entirely.
- *check:* does the test assert the status code or headers/problem-details shape anywhere, or only the happy-path body field? Evidence: read every assertion in the test class.
- **severity:** minor · **evidence:** strong · **flag:** argued
- *parent:* `T1-SCO-07` · *absorbs:* `N-WAF-04` · *cites:* dotnet-claude-kit `testing`

## Aspire and local orchestration

#### `T3-NET-27` — Drive an Aspire stack with `DistributedApplicationTestingBuilder` and wait via `ResourceNotificationService.WaitForResourceAsync` — never a sleep.
- *design:* build with `DistributedApplicationTestingBuilder.CreateAsync()`, wait with `ResourceNotificationService.WaitForResourceAsync(name, KnownResourceStates.Running)` — the readiness API exists precisely so tests stop guessing.
- *review:* a sleep-based wait races the container and produces the exact intermittent failure the API was built to remove.
- *check:* `grep -rn "Thread.Sleep\|Task.Delay" ` in orchestration test setup.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T2-INTG-04` · *absorbs:* `N-ASP-01` · *cites:* dotnet/aspire #4445

#### `T3-NET-28` — One AppHost at a time, and check for a second instance first when an emulator misbehaves.
- *design:* n/a (an environment discipline).
- *review:* two AppHosts corrupt each other through a shared named volume, and the symptom points nowhere near the cause.
- *check:* `ps aux | grep AppHost` and `docker ps --filter name=cosmos` — two of either is the answer.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-06` · *absorbs:* `N-ASP-02` · *cites:* `evidence.md` (one-apphost-at-a-time)

#### `T3-NET-29` — Per-worktree data volumes are ephemeral, or explicitly cleaned; a wedged orchestration instance in a persistent volume poisons every later run.
- *design:* wire volume cleanup into the worktree teardown, or use an ephemeral volume by default.
- *review:* a Pending-forever instance in a reused volume 409s every later run in that tree.
- *check:* `grep -rn 'WithDataVolume\|WithVolumeMount'` for a fixed literal name with no corresponding cleanup step in teardown or CI is the violation.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-06` · *absorbs:* `N-ASP-03` · *cites:* `evidence.md` (azurite-volume-corpse)

## Quality tooling

#### `T3-NET-30` — Verify a Stryker survivor by hand before writing a test for it.
- *design:* n/a (a review-time discipline).
- *review:* apply the mutant by hand, run the covering test, watch it redden, restore; `static readonly` initialisers run once at type load and can never be exercised by the per-mutant switch.
- *check:* the manual mutate-run-restore above.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-PRF-02` · *absorbs:* `N-MUT-01` · *cites:* `evidence.md` (stryker-blind-spots); Stryker docs on static mutants

#### `T3-NET-31` — Scope Stryker by enumerated `--file`, not `--namespace`, and fail the harness when the mutant count is zero.
- *design:* pass explicit file globs; assert a non-zero mutant count before trusting the score.
- *review:* `--namespace X` expands to `**/X/*.cs` — one directory deep — and a namespace whose types live in subdirectories yields zero mutants with a green-looking exit.
- *check:* the non-zero-mutant assertion above.
- **severity:** blocker · **evidence:** strong · **flag:** auto
- *parent:* `T1-PRF-03` · *absorbs:* `N-MUT-02` · *cites:* `evidence.md` (stryker-blind-spots)

#### `T3-NET-32` — Mutation runs are scoped and on-demand, not a CI gate; the score is a measurement, not a threshold.
- *design:* run Stryker scoped, by hand, when a suite's quality is in question — never wire a numeric threshold into CI.
- *review:* the blind spots above mean an automatic threshold measures partly-fictional survivors.
- *check:* `grep -rn 'threshold\|--break' <stryker config / CI workflow>` — a numeric mutation-score threshold wired to fail a build is the violation; a scoped, hand-run invocation is not.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-PRF-04` · *absorbs:* `N-MUT-03` · *cites:* `evidence.md` (stryker-blind-spots)

#### `T3-NET-33` — An ArchUnitNET rule must explicitly load every assembly containing the types it forbids, and be proven against a planted violation.
- *design:* list the assemblies explicitly; do not rely on ambient discovery.
- *review:* a rule over an unloaded assembly's types passes vacuously — plant a violation and watch it fail, or presume it inert.
- *check:* the planted-violation red-proof.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *parent:* `T2-ARCH-01`, `T2-ARCH-03` · *absorbs:* `N-ARC-01` · *cites:* `evidence.md` (archunitnet-rules-pass-vacuously)

#### `T3-NET-34` — Prefer the reflection-based assembly-reference test where it expresses the same invariant; delete the ArchUnit sibling that has never fired.
- *design:* pick the coarser mechanism when it is airtight.
- *review:* a rule that has never fired is either dead weight or untested — check which before keeping it.
- *check:* has the ArchUnit rule ever gone red under a planted violation (`T2-ARCH-01`'s proof)? Evidence: if not, and a coarser reflection-based assembly-reference check already forbids the same dependency, the ArchUnit rule is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T2-ARCH-04` · *absorbs:* `N-ARC-02` · *cites:* `evidence.md` (archunitnet-rules-pass-vacuously)

#### `T3-NET-35` — Rank by CRAP, not by coverage percentage, when deciding what to test next.
- *design:* pull the CRAP-ranked list; `CRAP(m) = comp(m)² × (1 − cov(m))³ + comp(m)`, and >30 is the refactor-or-test-urgently band.
- *review:* a method at 100% coverage still scores its bare complexity; coverage alone hides "uncovered and dangerous" behind "uncovered and trivial".
- *check:* `dotnet-test:crap-score` / `dotnet-test:coverage-analysis`, **when the `dotnet-test` plugin is installed**.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-04` · *absorbs:* `N-COV-01`

#### `T3-NET-36` — Audit for untestable statics as a first-class finding: `DateTime.Now/UtcNow`, `File.*`, `Directory.*`, `Environment.*`, `new HttpClient()`, `Console.*`, `Process.*`, `Guid.NewGuid()`.
- *design:* seam each with its supported abstraction (`TimeProvider`, `IFileSystem`, `IConfiguration`/options, `IHttpClientFactory`, an injected id generator) before arguing about coverage.
- *review:* a ranked static-call-site report tells you where testability is blocked before the coverage conversation even starts.
- *check:* `dotnet-test:detect-static-dependencies`, **when the `dotnet-test` plugin is installed**.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-03`, `T1-ISO-07` · *absorbs:* `N-TST-01`

## Determinism specifics

#### `T3-NET-37` — Parse and format with `CultureInfo.InvariantCulture` in production, and prove it with at least one test running under a comma-decimal culture.
- *design:* pass `IFormatProvider` explicitly everywhere; run one test under a non-default culture.
- *review:* `decimal.Parse("1,5")` is 15 in one culture and 1.5 in another — cheap to prevent, easy to miss on a single-locale CI box.
- *check:* `grep -rn "Parse(|ToString("` with no `IFormatProvider`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-ISO-04` · *absorbs:* `N-CUL-01` (archetype B5)

#### `T3-NET-38` — Use `DateTimeOffset` at boundaries and assert the offset/kind, not only the instant.
- *design:* prefer `DateTimeOffset`; where `DateTime` is unavoidable, pin `Kind`.
- *review:* a `DateTime.Unspecified` round-trips through serialization as whatever the reader assumes, and passes locally while failing in a UTC container.
- *check:* `grep -rn 'new DateTime(' ` for a call with no `DateTimeKind` argument feeding a serialization or storage boundary; confirm by round-tripping the value through JSON and reading whether `Kind` survives.
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed
- *parent:* `T1-ISO-04` · *absorbs:* `N-CUL-02`

#### `T3-NET-39` — Assert `[LoggerMessage]` `EventId`s via `FakeLogger`, never log message text.
- *design:* assert on the event id; treat the message as prose, not contract.
- *review:* a message-text assertion is a change detector; the event id emission also doubles as `T3-NET-15`'s deterministic sync point.
- *check:* `grep -rn "\.Contains(\"" tests/` against log-message assertions.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-SCO-01` · *absorbs:* `N-LOG-01` · *cites:* `dotnet-hosted-service-testing/SKILL.md` item 11; invariant `high-performance-logging`

#### `T3-NET-40` — Every hosted service / middleware / options binding gets a DI smoke test.
- *design:* the registration triple — service, options, and the hosted service itself — each gets `provider.GetServices<T>().ShouldHaveSingleItem()`.
- *review:* dropping a registration should fail something; if nothing does, the wiring is untested.
- *check:* does such a smoke test exist per registration?
- **severity:** major · **evidence:** strong · **flag:** argued
- *parent:* `T1-CST-06` · *absorbs:* `N-CFG-01` · *cites:* `dotnet-hosted-service-testing/SKILL.md`

#### `T3-NET-41` — A guard clause's exception type is the contract; assert the type and the parameter name, not the message prose.
- *design:* use the project's guard-clause helper (invariant `guard-clauses`); assert `ArgumentNullException`'s `ParamName`, not its text.
- *review:* message-prose assertions are change detectors on the exact same axis as `T3-NET-39`.
- *check:* `grep -rn '\.Message' tests/` on a guard-clause exception test — a message-text assertion with no `ParamName`/type assertion alongside it is the violation.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-ORC-05`, `T1-SCO-01` · *absorbs:* `N-GRD-01` · *cites:* invariant `guard-clauses`

#### `T3-NET-42` — Verify snapshots: scrub GUIDs and timestamps, commit `.verified` files, review their diffs, and never accept in bulk.
- *design:* scrub non-deterministic fields before the comparison; commit the `.verified` file.
- *review:* a bulk-accepted snapshot diff is an unreviewed change disguised as a passing test.
- *check:* does the commit that changed `.verified` files carry a per-file review trail (a PR review comment, or a commit message naming what changed), rather than a bulk accept? Evidence: the commit/PR history for those files.
- **severity:** blocker · **evidence:** strong · **flag:** argued
- *parent:* `T2-SNAP-03`, `T2-SNAP-04` · *absorbs:* `N-VER-01` · *cites:* dotnet-claude-kit `testing` (Verify)

#### `T3-NET-43` — NSubstitute (or the house mocking library) for one-off stubs; hand-written fakes in a shared testing project for ports with behaviour.
- *design:* keep a dedicated `*.Testing` project of contract-bound fakes; reach for a one-off substitute only for a boundary with no real behaviour to fake.
- *review:* zero mocking in the domain layer is the target shape.
- *check:* `grep -rln 'NSubstitute\|Moq\|FakeItEasy' <domain test project>` — any hit is the violation; zero mocking in the domain layer is the target shape.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-DBL-02`, `T1-DBL-03` · *absorbs:* `N-DBL-01` · *cites:* `evidence.md` (test-suite-shape)

## Performance tooling

#### `T3-NET-44` — For load/stress testing, always use Release configuration, never Debug/Development.
- *design:* build and run the load lane in Release; a Debug-configuration number is not a production number.
- *review:* flag any load/stress harness invoked against a Debug build.
- *check:* `grep -n "Configuration" <load-lane script>`.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T2-PERF-02` · *absorbs:* `D3` · *cites:* MS Learn — load tests

#### `T3-NET-45` — Express NBomber load-test scenarios as plain async C# functions, assert thresholds via percentiles/error rates through the normal test framework, never averages.
- *design:* write the scenario as ordinary async code; gate on a percentile or an error-rate threshold.
- *review:* an average hides the tail; a percentile threshold is what a user actually experiences.
- *check:* does the scenario's assertion gate on a mean/average duration, or on a percentile/error-rate threshold? Evidence: read the assertion — `.Mean()`/`Average` compared against a bound is the violation.
- **severity:** minor · **evidence:** strong · **flag:** argued
- *parent:* `T2-PERF-04`, `T2-PERF-05` · *absorbs:* `D4` · *cites:* NBomber docs

#### `T3-NET-46` — Use `dotnet/crank` for ASP.NET Core/.NET runtime-level trend tracking and regression bisection across versions.
- *design:* reach for `crank` when the question is a cross-version runtime trend, not an in-process load scenario.
- *review:* an ad hoc perf harness reinventing what `crank` already does is a maintenance cost with no bisection story.
- *check:* does a hand-rolled timing loop compare against a prior run or version, duplicating `dotnet/crank`'s existing comparison mode? Evidence: does the harness track cross-version trend, or only a single in-process number?
- **severity:** minor · **evidence:** strong · **flag:** argued
- *parent:* `T2-PERF-07` · *absorbs:* `D6` · *cites:* dotnet/crank docs

## BDD

#### `T3-NET-47` — Reqnroll is the only live .NET BDD runner; never recommend SpecFlow.
- *design:* integrate `Reqnroll` + `Reqnroll.xunit.v3`; SpecFlow reached end-of-life 2024-12-31 and is stuck on stale xUnit v2 betas.
- *review:* a SpecFlow reference in a new integration is itself the finding.
- *check:* `grep -rn "SpecFlow" *.csproj`.
- **severity:** major · **evidence:** strong · **flag:** auto
- *parent:* `T1-CST-05` · *cites:* `dotnet-bdd-testing/SKILL.md` — runner landscape

#### `T3-NET-48` — `@ignore` is the only Gherkin tag that maps to a skip; every other tag becomes a `Category` trait.
- *design:* to defer a scenario, tag it `@ignore` (with a reason and re-enable condition, per `T1-STR-03`) — a custom tag alone does not skip it.
- *review:* a scenario tagged `@deferred` with no `@ignore` still runs; confirm the intent before reading a run as "deferred and safe".
- *check:* `dotnet test --list-tests` against the source `.feature` tags.
- **severity:** minor · **evidence:** strong · **flag:** auto
- *parent:* `T1-STR-03` · *cites:* `dotnet-bdd-testing/SKILL.md` — tags, skips and Rule: blocks
