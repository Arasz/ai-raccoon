---
name: design-gate-audit
description: Audit design-doc acceptance gates for honesty/feasibility.
---

# design-gate-audit

Auditing a design doc's acceptance criteria BEFORE implementation, so gates cannot certify broken or vacuous behavior. Pair with the loaded hosted-service skills for BackgroundService-specific mechanics (`dotnet-hosted-service-testing` for FakeTimeProvider semantics, `dotnet-hosted-service-review` for loop robustness).

## Procedure

1. **Read the design doc's gate table and work-package plan.** Every row: "would this gate fail if the feature were broken?" (HONEST) and "does the named test file / framework / helper exist?" (FEASIBLE).
2. **Verify feasibility by reading the actual test project** — never trust the doc. Check: the named test file exists, the named pattern exists in it (host-shape pins, FakeTimeProvider settles, E2E factory), the named production seam exists (host factory, DI singleton, renderer).
3. **Verify factual claims by grepping** — e.g. "no test churn: existing constructions keep compiling" → grep the literal (`new ServerConfig(` may return 0 while target-typed `new(...)` constructions exist — count THOSE), confirm the production type's positional arity, and confirm a defaulted 4th positional param keeps 3-arg call sites compiling. Report the count, not the doc's paraphrase.
4. **Attack each gate for wrong-reason passes and vacuous negatives** (checklist below).
5. **Emit the report** with the skeleton in §Report skeleton, then the TDD order per work package with corrections, then missed cases.

## Gate-honesty checklist

- **Vacuous negative**: a "X not called before timeout" case passes via the lost first fake-clock `Advance` (StartAsync does not run ExecuteAsync on the caller thread). Negative-only assertions must follow a settled positive or use counters (StopApplication-call counter in a fake `IHostApplicationLifetime`; invocation counter on the fake). See `dotnet-hosted-service-testing`.
- **Timing-window vacuity vs sibling services**: a gate like "idle test runs with extraction enabled proves extraction doesn't reset the watchdog" is vacuous if the sibling's configurable interval floor (e.g. `ParseIntervalMinutes`: minutes > 0 → ≥ 1 min) cannot fire inside the gate's real-time window. Fix: shared-FakeTimeProvider two-service test — both services on ONE clock, fake store with call counter + fake lifetime; advance past X's interval → X's counter > 0 AND Y's StopApplication count == 0; advance past Y's deadline → count == 1.
- **Poll interval vs timeout expectation**: a shutdown-latency gate ("within ~5s" with a 2s timeout) is unsatisfiable if the loop polls every 1 minute (shutdown fires up to one poll late). Either the design derives `poll = min(baseline, timeout/2)` (pin the derivation) or the gate is rewritten.
- **Real-time reset halves are unprovable**: "make one call, then shutdown within 5s" passes even when the reset wiring is broken (shutdown happens regardless) and flakes under CI load. Split: deterministic wiring proof = real host + real traffic + fake TimeProvider via a host seam (TimeProvider is usually a DI singleton — require an optional `TimeProvider?` param on the internal host factory); real-time smoke gets a generous bound asserting only "shutdown happened"; "never shuts down" cases assert with the fake clock, never by real-time waiting.
- **Routing/dispatch gates**: a negative pin in a subcomponent ("verb never reaches the dispatcher") passes BOTH when routing works AND when the routing branch is missing (then the subcomponent IS what runs) — the primary bug ships green. Require a dispatch seam (injectable runner/delegate) with a positive pin: serve args → runner invoked, dispatcher not; verb args → the reverse. `WebApplicationFactory` is the wrong tool for blocking entry-point paths (TestServer replaces Kestrel; bound-URL/blocking semantics unrepresentative).
- **Unpinned "still logs / unchanged" claims**: "bare launch still logs X on stderr" is a gate only if something asserts it — check for a LoggerMessage pin; E2E factories usually run at Warning minimum, so Information logs are invisible to them. Either add the pin or reword the claim to what the suite covers.
- **Blocking-run testability**: a `RunAsync` that blocks until shutdown + `StringWriter` means stdout is only visible AFTER return. Specify the stop seam (ct-cancellation triggers `StopApplication` via HostingAbstractionsHostExtensions — cancel a CTS, assert exit 0) and split cases: port-0 URL asserted post-hoc; mid-run client access uses a fixed free port (URL known a priori) or a line-signaling TextWriter.
- **Port-race**: "find a free port then release" has a race window. Busy-port tests must HOLD a `TcpListener(IPAddress.Loopback, 0)` open for the whole run (read `LocalEndpoint`), disposing in finally. Also: catch bind failures specifically (`IOException`/`SocketException`/`AddressInUseException`), never bare `Exception` — or a ct-cancel during startup is misreported as the port error.
- **Process-global env poisoning**: fixtures booting the real composition root (key resolution, bank decryption) must clear process-global env vars (e.g. `AIRACCOON_DB_PASSPHRASE`) under the repo's env gate (`TestData.EnvVarGate` precedent), or a dev machine's value flips exit codes.
- **"Unit/Fast" trait vs real bootstrap**: an in-process host test that hits best-effort startup downloads (bundled model ensure, 30s bound) can burn the whole budget on a fresh machine — pre-ensure the asset in the fixture (E2E precedent) or mark the suite Slow.

## Report skeleton

1. Verdict summary (conditional approval + the MUST-FIX list, each tagged "design edit, not code" when true).
2. Findings table: ID · severity MUST-FIX/SHOULD-FIX/NIT · gate attacked · why (evidence, file:line) · concrete fix.
3. Per-criterion verdict table A1..An: HONEST? (fails if broken?) and FEASIBLE? (file/framework exists?) with notes.
4. TDD order per work package with corrections (RED test first per gate; mark which tests are RED now vs after the WP).
5. Missed test cases the table doesn't cover.

## Pitfalls

- Rate gate-honesty bugs as MUST-FIX even when the fix is a design-doc edit — the doc is the build contract; an unsatisfiable or vacuous gate certifies broken behavior.
- `JsonDocument` deep-equal IS property-order-insensitive for objects (fine for single-object golden docs) — don't invent an order problem.
- Verify the "no churn" claim substance: `record` positional params with a defaulted 4th arg keep 3-arg call sites compiling, but reword "direct constructions" to "positional constructions" if the grep shows only target-typed `new(...)`.
