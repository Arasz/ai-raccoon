# Worked example — HTTP serve-mode design audit (2026-08-06)

Design: the repo's work docs (worktree `switch-from-stdio-to-http`).
Report: the same work-docs directory.
Shape: 13 acceptance gates (A1–A13) over 4 work packages (WP1 CLI surface → WP4 docs); a BackgroundService idle watchdog + middleware + blocking ServeRunner.

## What the audit caught (each maps to a checklist item)

| Checklist item | Concrete instance |
|---|---|
| Vacuous negative | A5 "advance < timeout → StopApplication not called" — first fake-clock Advance after StartAsync is lost, so the negative passes whether or not the timer registered. |
| Timing-window vacuity vs sibling | A8 "idle E2E runs with extraction enabled" — `ExtractionConfigKeys.ParseIntervalMinutes` floor is 1 min; extraction cannot fire inside a 2–15s real-time window. |
| Poll interval vs timeout | A6 expected shutdown "within ~5s" for a 2s timeout while the watchdog polled every 1 min (up to ~62s late). |
| Real-time reset unprovable | A6 "make one tool call then shutdown within 5s" passes even with broken `NotifyActivity` wiring. |
| Routing/dispatch gates | A13 "no serve path leaks into the config dispatcher" — the negative pin passes both with and without the Program.cs `["serve"]` branch; needed an `EntryPointRouter` seam + positive pin. |
| Unpinned log claim | A12 "bare launch still logs HttpTransportListening" — no test pins that log (E2E factories run at Warning minimum; only `ServerSetup.Log` is LoggerMessage-pinned). |
| Blocking-run testability | A2/A3 with StringWriter — stdout visible only after return; split port-0 (post-hoc assert) vs fixed free port (mid-run client); ct-cancel triggers StopApplication (HostingAbstractionsHostExtensions). |
| Port-race | A4 — `FreePort()` releases before Kestrel binds; hold `TcpListener(loopback, 0)` open and read `LocalEndpoint`. |
| Env poisoning | ServeRunnerTests resolve the encryption key; `<APP>_DB_PASSPHRASE` on a dev machine flips exit 0 → 2 — use `TestData.EnvVarGate`. |
| Trait vs bootstrap | ServeRunner calls `EnsureEmbeddingAvailabilityAsync` (30s bound, may download ONNX model) — pre-ensure via `TestData.CreateBundledModel()` or mark Slow. |
| Claim verification | "no test churn" — grep `new ServerConfig(` = 0 literals in tests; exactly 1 target-typed 3-arg `new(...)` (ServerSetupHostTests.Config) + 1 src call site; defaulted 4th positional keeps both compiling → claim HOLDS. |

## Verified-existing test infrastructure (feasibility anchors)

- Host-shape pins: `ServerSetupHostTests` `GetServices<IHostedService>().ShouldContain/ShouldNotContain(service is ExtractionHostedService)`.
- FakeTimeProvider pattern: `ExtractionHostedServiceTests` — settle `Task.Delay(50)` after StartAsync, `Task.Delay(100)` after each Advance, invocation counters (`ExtractionCalls`).
- E2E: `McpServerFactory : WebApplicationFactory<Program>` + `HttpClientTransport` + `McpClient.CreateAsync`; `E2ETestCollection` serial; traits `Category=Unit/Fast`, `E2E/Slow`.
- DI smoke precedent: `ExtractionDependenciesSmokeTests` (`ShouldHaveSingleItem()` on `OfType<ExtractionHostedService>()`).
- `TimeProvider.System` is a DI singleton (`Dependencies.RegisterMemoryServices`) — a host-level fake-clock test needs a `TimeProvider?` param seam on the internal host factory.

## Report skeleton used (the one in SKILL.md)

Verdict summary → findings table (12 rows: 4 MUST-FIX, 5 SHOULD-FIX, 3 NIT) → A1–A13 honest+feasible verdicts → WP1–WP4 TDD order with corrections (e.g. WP1 needed 6 RED tests, not 2) → 12 missed cases. All MUST-FIX fixes were design-doc edits, not code.
