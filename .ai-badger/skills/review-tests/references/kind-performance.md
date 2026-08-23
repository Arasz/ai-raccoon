# kind-performance

Applies only to tests living in a dedicated, out-of-band performance/load lane — never the default
suite. The runner is a load/stress tool (NBomber, k6) or a micro-benchmark harness (BenchmarkDotNet),
run on controlled hardware. Per ruling C17, `T1-ISO-01`'s no-wall-clock rule and this kind are not
in tension: a percentile/error-rate threshold is a legitimate assertion **inside this lane only**,
excluded from the default gate. A p95 assertion found in a unit or integration lane is not this
kind's rule firing — it is `T1-ISO-01` firing, full severity, no exception.

**`T2-PERF-01` — Load tests validate a response-time goal under specified normal concurrent load; stress tests validate stability and graceful recovery under extreme load. Keep the two goals distinct.**
- *design:* name which of the two a given test is before writing it; do not let one test try to answer both questions.
- *review:* a test asserting both a latency SLO and a crash-recovery claim is answering neither cleanly.
- *check:* does a single test assert both a latency/SLO bound and a crash-recovery claim together? Evidence: read its assertions — a test mixing a percentile check with a 'recovers gracefully' claim is the violation; split it into a load test and a stress test.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-CST-01`
- *cites:* MS Learn load-tests.
- *meta:* pass=7 order=28

**`T2-PERF-02` — Always load/stress test a Release build, never Debug or Development-mode configuration.**
- *design:* run the perf lane against the same build configuration that ships.
- *review:* Debug is unoptimized and Development-mode logging skews results — the runner cannot observe production performance from either.
- *check:* auto — build configuration used by the perf lane's pipeline stage.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-05`
- *cites:* MS Learn load-tests.
- *meta:* pass=0 order=1

**`T2-PERF-03` — Define explicit pass/fail thresholds on the load test itself — not eyeballing a report — and only inside the performance lane (ruling C17).**
- *design:* set the threshold in the same tool run that produces the metric; never a separate manual read of a dashboard.
- *review:* a threshold defined but not wired into the run is documentation, not a gate; a threshold wired into the default suite is `T1-ISO-01` violated, not this rule satisfied.
- *check:* auto — threshold assertions present and scoped to the perf lane only.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-01`
- *cites:* NBomber docs; ruling C17.
- *meta:* pass=2 order=11

**`T2-PERF-04` — Assert on percentiles and error rates, not averages.**
- *design:* pick p95/p99 and an error-rate bound as the assertion; an average is not the number that describes user-visible tail latency.
- *review:* an average hides exactly the tail behaviour a load test exists to catch.
- *check:* does the assertion gate on a mean/average duration, or a percentile/error-rate threshold? Evidence: read the assertion call — `.Mean()`/`Average` compared against a bound is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-ORC-03`
- *cites:* NBomber threshold examples.
- *meta:* pass=4 order=17

**`T2-PERF-05` — Wire thresholds into the normal test-framework assertions so they gate CI like any other test — inside this lane only (ruling C17).**
- *design:* express the threshold as an assertion the perf lane's own runner fails on, not a report a human reads later.
- *review:* this is the sharpest cross-lane conflict in the ruleset — the rule forbids a budget in the default suite, not the existence of budgets anywhere.
- *check:* auto — threshold assertions exist and the lane is excluded from the default gate.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-01`
- *cites:* NBomber docs; ruling C17.
- *meta:* pass=2 order=11

**`T2-PERF-06` — Know the tool's default abort behaviour: a threshold breach does not necessarily stop the run unless configured to.**
- *design:* configure the tool's abort-on-breach setting explicitly; do not assume a red threshold halts anything.
- *review:* a threshold that is asserted but does not stop the run, or block a promotion, is documentation wearing a gate's clothes.
- *check:* argued — trace whether a breach fails the pipeline stage or only prints.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-PRF-04`
- *cites:* NBomber docs.
- *meta:* pass=8 order=32

**`T2-PERF-07` — Tier performance testing by pipeline stage: a fast smoke check gates PR/merge; heavier soak/load tests run nightly or per release, out-of-band.**
- *design:* keep the PR-time check to minutes; schedule the heavier run separately and name what it covers.
- *review:* the tiering is the mechanism that keeps the perf lane's exclusion from the default gate derived, not merely convenient.
- *check:* is the PR-gating perf check limited to a fast smoke run, with the heavier soak/load run wired into a separate nightly/per-release schedule? Evidence: `grep -n 'schedule:' .github/workflows/*perf*.yml` — a PR-time job running the full load profile with no separate scheduled heavy variant is the violation.
- *rationale:* weak — 2025-2026 DevOps-blog synthesis, directionally consistent with the general fast/nightly pattern but no single first-party citation.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-CST-03`
- *meta:* pass=0 order=3

**`T2-PERF-08` — Derive a baseline from multiple runs, not one, and gate on a percentage deviation from it rather than a single hard number.**
- *design:* run the benchmark several times before setting a baseline; express the gate as a deviation tolerance, not an absolute figure copied from one run.
- *review:* a baseline from a single run cannot distinguish a real regression from environmental noise.
- *check:* was the baseline set from a single run, or from several runs with a stated deviation tolerance? Evidence: read the baseline-setting commit/script — a single captured number gated by hard equality or a fixed delta is the violation.
- *rationale:* weak — blog synthesis; the shape (multi-run baseline, percentage gate) is sound even where the specific numbers are illustrative.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-ORC-02`
- *meta:* pass=4 order=16

**`T2-PERF-09` — Investigate every sudden performance regression rather than dismissing it as noise, and keep the perf suite from going stale.**
- *design:* n/a — a triage rule.
- *review:* this is `T1-ISO-09`'s triage discipline applied to a performance signal: confirm it against the unmodified baseline before deciding what it means.
- *check:* argued — the diagnosis record names the baseline run it was compared against.
- **severity:** major · **evidence:** weak · **flag:** argued · **parent:** `T1-ISO-09`
- *meta:* pass=0 order=2
