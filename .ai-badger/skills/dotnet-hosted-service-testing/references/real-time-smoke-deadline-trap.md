# Real-time smoke tests — the deadline-starts-at-construction trap

From the project serve-mode review (2026-08-06): an idle-watchdog feature's real-time smoke test had a latent flake mode worth checking for in every hosted-service review.

## The trap

An idle watchdog arms its deadline at CONSTRUCTION (ctor sets the baseline timestamp, design intent: "a fresh server lives a full timeout even with zero requests"). A real-time test then:

1. starts the host,
2. waits for a post-startup output line (e.g. the serve URL, printed only after `StartAsync`),
3. asserts the host shuts down within a bound.

The deadline is ticking during step 2's wait — the STARTUP phase (temp-bank/SQLite creation, bundled-model SHA-256 verification of a multi-MB file, Kestrel bind, hosted-service start) runs INSIDE the timeout window. If startup exceeds the
short timeout (2s in the reviewed test), the host dies BEFORE the output line is printed → the line-wait times out → flake, even though the feature is correct.

## Rules of thumb

- Measure (or reason from) the startup work, then size the timeout for ≥5–10× the measured pre-output phase. A "2s timeout / 10s bound" is NOT generous when the clock starts at construction — the bound only covers post-output time.
- Post-output bound math: `timeout + tick + shutdown slack` is the honest ceiling for elapsed-from-output.
- The watchdog baseline-at-construction is CORRECT production behavior — don't "fix" the production code; fix the test's timeout.
- The deterministic wiring proof belongs in a fake-clock host test (real host + real HTTP traffic + fake TimeProvider), leaving the real-time test with "it eventually shuts down" semantics only.

## Verified mechanics that make the seam work

- Host seam: optional `TimeProvider?` param on the host builder; register `AddSingleton(timeProvider ?? TimeProvider.System)` AFTER the framework/Dependencies registration — Microsoft DI resolves the LAST descriptor for a service type, so
  the fake overrides `TimeProvider.System`. (Verified: the fake clock drove the watchdog through the real middleware + real MCP tool call.)
- Real-host tests with `Trait(Unit/Fast)` and no `[Collection]` run in the DEFAULT PARALLEL collection. They add wall time + CPU spikes that can starve timing-sensitive step-poll tests in other collections (e.g. BDD `StepUntilAsync` with a
  5s real-time bound: digest pipeline + real embedding under parallel load can exceed it). When triaging an unrelated suite flake: name the real-host tests "plausible load contributor, not cause" unless code paths overlap; mitigations are
  serializing the real-host tests or lengthening the poll bounds.

## Concrete case

the project `ServeRunnerTests.IdleTimeout_ShutsTheHostDown_AfterTheSpanWithoutActivity`: `--idle-timeout 2s`, real time; pre-URL phase measured ≈0.2–0.3s (temp bank + SHA-256 of the 23MB bundled ONNX model + Kestrel) ≈ 10× margin → low
risk, but the review still recommended `2s/10s → 5s/15s` as one-line CI insurance (finding F1, SHOULD-FIX low).
