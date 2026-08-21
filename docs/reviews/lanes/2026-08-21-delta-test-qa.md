# Lane report — Test-suite QA (2026-08-21 delta campaign)

Lane: test-suite QA · Base: `155f281e` · Read-only · 11 findings (5 MEASURED, 1 READ, 1 INFERRED,
4 MEASURED-strength confirmations as NIT; 3 MEDIUM, 3 LOW, 5 NIT). One briefed lead disproven (F9).

### F1 — All retrieval-quality and crash-recovery gates are Nightly-only; no PR gate runs them [READ]
**Severity:** MEDIUM
**Evidence:** `TestCategories.cs:9-11`; build.yml filters only Fast/bdd/Slow; nightly.yml is unfiltered but schedule-only and its own header says "Absence of a nightly run is NOT evidence that anything passed". 32 files carry Speed=Nightly incl. HeldOutRetrievalGateTests, TableRetrievalGateTests, ModelMigrationCrashRecoveryE2ETests. A ranking or migration-relay regression merges green through all three PR jobs.

### F2 — StepUntilAsync bounds loop iterations, not any single awaited call; one blocked call hangs the testhost indefinitely [INFERRED]
**Severity:** MEDIUM
**Evidence:** `WatchIntegrationTests.cs:771-800` — budget checks (`:781-782`) run only between iterations; condition/tick awaits carry only the never-firing test token; xUnit has no per-test timeout. Consistent with the orchestrator's idle-testhost kill and the 2026-08-20 doc. The class is fully in-process — the briefed "spawned server holds a port" mechanism does not apply here.

### F3 — The one red is an arrange-phase seed timeout, not a cascade-behaviour failure [MEASURED]
**Severity:** LOW
**Evidence:** `WatchIntegrationTests.cs:343-348` — the expired budget is the FIRST StepUntilAsync ("seed files did not become searchable") before the delete happens. Loop spun healthily (600 steps/18.2s); under full-suite load the real-ONNX seed embed didn't land in 60s fake time. Correctly messaged; unledgered per known-flakes policy (first red tolerated).

### F4 — The crash-mid-drain window is genuinely exercised by a real process kill [MEASURED — verified strength]
`ModelMigrationCrashRecoveryE2ETests.cs:224-252` — kill inside the polling predicate at `0 < embedded < 128`; lease row as start signal; exact-count + real-searchability asserts. Not a mocked-exception fake.

### F5 — Skip honesty: ground truth's blanket claim has exactly one counterexample [MEASURED]
**Severity:** LOW — `ModelMigrationCrashRecoveryE2ETests.cs:294-297` permanent `[Fact(Skip)]` with empty body, honestly documented (repro recipe at :268-293), but ADR-0076's watched-red negative exists only as prose.

### F6 — HeldOut reversal discrimination is executable and self-proving [MEASURED]
`HeldOutRetrievalGateTests.cs:105-122` — reversed mean < floor − tolerance asserted; passed in the verified full run. Residual: floors' raise history guarded only procedurally.

### F7 — No new vacuous range/finite-only gates; H2 class stays closed [MEASURED]
Sweep of 192 new + 153 modified test files clean. Two assertion-free report `[Fact]`s exist (`TableRetrievalGateTests.ReportPerQueryScores`, `TableChunkingArmComparison`) — always-pass report-only facts inflating pass counts.

### F8 — Fake fidelity matches the prior-campaign standard where it matters [MEASURED]
SHA256-derived vectors, real HttpListener FakeHfServer, byte-exact download asserts. Two constant-zero-vector fakes are scoped to call-counting/table-shape subjects — acceptable, declared.

### F9 — Maintenance ordering guarantee is tested, not just jobs in isolation [MEASURED — lead disproven]
`EmbedSweepAfterJobsTests.cs:61-72` pins same-pass embed-after-jobs (the exact 2026-08-14 live-bank backlog regression); on-demand poll tests pin both pickup directions.

### F10 — Golden-vector gate is honest, but its strongest check silently disables off-capture-arch [READ]
**Severity:** LOW — `MiniLmGoldenVectorTests.cs:108-156`; arch-scoped tripwire with measured cross-arch fallback; easy to misread as full coverage on CI.

### F11 — Timeout margins thinned by this delta's growth [INFERRED]
**Severity:** LOW — nightly 45min cap vs 24m45s local + reruns; build-slow 30min after a prior kill, while the delta added ~33.8k test lines. One slow runner from an abort-before-upload.

## Still open
- Mechanism of the full-suite seed-embed slowdown (retry-backoff fake-time vs CPU starvation).
- Whether `WaitUntilAsync(...ContinueWith(t => !t.Result))` faults unobserved if IsMigrationOpenAsync throws.
- Whether any Nightly run has actually executed recently (needs gh run list).

## Owner questions
- Should the Nightly family get a workflow_dispatch PR-gate leg?
- Should StepUntilAsync wrap each iteration's awaits in a wall-clock-linked token?
- Ledger the watch-seed flake now, or wait for the second red?
- Convert the two assertion-free report Facts to explicit reports so pass counts stay meaningful?
