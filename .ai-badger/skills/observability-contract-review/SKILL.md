---
name: observability-contract-review
description: "Use when reviewing claims that 'all calls are instrumented': span+metrics helper diffs, tool-layer try/catch instrumentation, N/N tool-surface parity tests. Checks path coverage, not call-site presence — filtered-catch escape holes, exactly-once recording, Activity status/tag ordering, instrumentation-test honesty, CI Speed-trait blind spots, metrics-unchanged verification."
author: hermes-curator
license: MIT
platforms: [macos, linux, windows]
metadata:
  hermes:
    tags: [code-review, observability, tracing, metrics, mcp, e2e]
    related_skills: [code-review-checklist, comprehensive-code-review, integration-review-gate, review-changes]
version: 1.0.0
---

# Observability / Instrumentation Contract Review

Reviewing code that claims "every call is instrumented" (spans carrying status + tags, per-call counters/histograms) is about **path coverage**, not call-site presence. The gap is almost always an exception path that escapes the recording catch. Use this checklist when a diff touches an observability helper (e.g. a `ToolExecutionActivity`-style wrapper), tool-layer try/catch instrumentation, or a tool-surface parity test.

## When to use

- PR adds/changes a span+metrics helper that tools wrap their bodies with.
- PR claims "all N tools route through the helper" or "N/N tools answer over the wire".
- PR adds an E2E `tools/list` parity test.
- Any review where a metric/span contract (status, tags, counter) is an acceptance criterion.

## Checklist (verify against code, not claims)

1. **Call-site coverage is not path coverage.** Grep for helper construction (`new ToolExecutionActivity(...)`) — N sites ≠ N tools fully instrumented. Then walk every early-exit path:
   - Guards/validation/access-control (`RequireProjectId`, `EnsureAsync`) — **what exception type do they throw?**
   - **Filtered catches (`catch (Exception ex) when (ex is not X)`) are the #1 hole**: when a guard throws the filtered type *before* the inner typed catch, the exception escapes unrecorded — span ends `Unset`, no `result`/`error_type` tags, no metric. Reachable via missing-required-param and access-denied errors.
   - **Fix that preserves the domain exception as `error_type`**: make `Record*` idempotent (private `bool _recorded`; second call no-ops), then widen the outer catches to plain `catch (Exception ex) { RecordError(ex); throw; }`. Inner typed catches keep recording the domain exception (first record wins); previously-escaped paths now record. Never fix by just removing the filter — that double-counts the inner-catch rethrows.

2. **Status/tag ordering vs Activity stop.** `SetStatus`/`SetTag` on a stopped `Activity` is invalid/ignored. `Record*` must be called while the span is running — last statement before `return`/`throw` — and `Dispose()` (`using var`) fires at scope exit, after.

3. **Exactly-once recording.** Success: `RecordInvocation` is the final statement before `return`. Error: exactly one `RecordError` per invocation — watch for an inner catch recording AND an outer catch recording the same throw (the filter pattern exists precisely to prevent this; changing the filter without an idempotence guard re-opens it).

4. **Instrumentation unit-test honesty:**
   - Assertions read from the listener-captured `Activity` reference (mutated in place) or from `ActivityStopped` are both honest; beware asserting state the record call hasn't run yet.
   - `ActivityListener` is process-global: tests listening on a shared source name must be serialized (collection with `DisableParallelization = true`) or a concurrent class's activity clobbers `ShouldHaveSingleItem()` — this is a documented flake pattern in several repos.
   - Pin the negative too: if the ADR promises "`error_type` absent on success", assert absence, not just presence.

5. **E2E tool-surface parity tests:**
   - "Exactly N tools" must be exact set equality (sorted names `ShouldBe`), never a Contains-list.
   - Verify "N/N over the wire" by enumerating tool names across **all** test files — a literal-`CallAsync("name")` grep misses direct `CallToolAsync("name", ...)` calls and variable-built names. A tool called via `CallToolAsync` with a literal string is easy to undercount.
   - **CI blind spot**: check the new test's `Speed` trait against the CI filter (e.g. `build.yml` runs `--filter "Speed=Fast"`). A `Slow` parity gate is nightly-only → recommend a Fast unit-level inventory test (reflection over `[McpServerTool]` attribute names) so surface drift fails PR CI.
   - Substring JSON assertions (`"deleted":1`) are fragile — also matches `"deleted":12`; parse the JSON property and assert the typed value.
   - Transient-state assertions (watch state `scanning|healthy`) are legitimate when the tool docs promise an async background transition; `.ToLowerInvariant()` guards enum-string casing policy. The meaningful part is excluding terminal/error states (`retrying`, `stopped`).
   - Index-based array access (`watches[0]`) → prefer `First(w => w.path == expected)`.
   - Settings seeded mid-flight after server start: verify the service reads settings per-call (no construction-time caching) and that the seeding connection uses the SAME encryption/key provider as the server.

6. **Resource/cleanup discipline in E2E tests:**
   - Temp dirs deleted in `finally` can race a background scanner (IOException flake) → wrap delete in `try/catch (IOException)` (best-effort, like the factory's own Dispose precedent).
   - Dedicated project id + per-test temp DataRoot = no cross-test residue; factory dispose should delete DataRoot.

7. **Metrics-unchanged verification.** If a criterion says "metrics unchanged", diff the metrics class: instrument names, tag sets, histogram bucket advice, DI registration — all must be untouched.

8. **Review-record claims are in-scope.** A record claiming "the whole surface emits the contract" makes pre-existing gaps in that surface in-scope: flag them MAJOR even though the diff didn't introduce them, and require a fix or a tracked follow-up issue — the claim as-written is the acceptance criterion.

## Severity calibration

- **BLOCKER**: broken core change, dishonest test (cannot fail / no real assertion), secrets.
- **MAJOR**: contract gap on reachable error paths (even pre-existing, if the PR claims full coverage); a CI blind spot that defeats the PR's own new gate.
- **MINOR/NIT**: fragile assertions, index assumptions, hardcoded keys where helpers exist, dirty worktree files, doc typos adjacent to edited lines, unpinned negative contracts.

## Output shape

Numbered findings (severity + `file:line` + concrete fix), an explicit verdict (approve / approve-with-changes / reject), and explicit pass statements on the judged dimensions (implementation correctness, test honesty, architecture/layering, cleanup discipline, security). Say "clean on the substance" explicitly when true — reviewers should not need to infer an absence of findings.

## Gotchas

- "All calls are instrumented" is a path-coverage claim, not a call-site count — filtered-catch escape holes bypass instrumentation.
- Metrics-unchanged verification must compare before/after runs, not the presence of metric code.
## References

- `references/filtered-catch-hole.md` — the concrete before/after of the unrecorded-path defect (guard throws filtered type before inner try) with the idempotent-record fix.
- `references/quiet-mode-functional-payload.md` — the quiet-mode functional payload: what a successful tool call must carry on the span/metrics contract.
