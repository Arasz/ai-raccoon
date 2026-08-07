---
name: dotnet-hosted-service-review
description: "Use when reviewing a PR that adds or modifies a .NET BackgroundService/IHostedService — background extraction loops, watchers, sync, sweep, or any poll loop. Checklist: ExecuteAsync try/catch coverage (StopHost kills the process), cancellation filtering, PeriodicTimer semantics, store-level idempotency vs TOCTOU, settings-channel parsing, LoggerMessage invariants. Produces numbered findings + severity + file:line."
description: Use when reviewing .NET hosted/background services.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, hosted-services, review, backgroundservice]
    related_skills: [dotnet-hosted-service-testing, code-review-checklist]
---

# dotnet-hosted-service-review

Reviewing a PR that adds or modifies a .NET `BackgroundService` / `IHostedService` (poll loops: background extraction, watchers, sync, sweep). Focus: robustness, cancellation, idempotency, registration, and logging invariants. Produces
numbered findings with severity + file:line evidence and a claims-vs-code verdict.

## Checklist

1. **ExecuteAsync try/catch coverage** — every await except the cancellation wait must be inside a try/catch (or have its own). The classic miss: the interval re-read / config reload AFTER the run body
   (`timer.Period = await ReadIntervalAsync(...)`) and the startup config read sit outside the try. An unhandled non-OCE exception there faults ExecuteAsync → default `BackgroundServiceExceptionBehavior.StopHost` kills the WHOLE process
   (MCP server included), contradicting any "best-effort" claim in the class doc. Evidence: read the loop body and check what is outside the try block, not just that a try exists.

2. **Compare with the sibling hosted service in the same repo** — same-repo precedent is the strongest review signal. In the project, the sibling hosted service try/catches both the reconcile AND the delay; a new service that guards less is
   a finding. Cite the divergence explicitly.

3. **Cancellation handling in per-item catches** — `catch (Exception ex)` per project/item swallows `OperationCanceledException`: on shutdown each in-flight item logs a spurious Warning and the top-level
   `catch (OCE) when (IsCancellationRequested) break` never fires from the run body. Inner catches should filter OCE (`when (!cancellationToken.IsCancellationRequested)`) or rethrow.

4. **Timer semantics** — `PeriodicTimer` first tick = one full interval: "enable" produces no work until the first tick (UX surprise; check for a run-now verb or a doc note). Re-reading the interval after each run is correct (config change
   without restart) but must be inside the try (item 1). `timer.Period` has a setter — mutation is fine.

5. **Idempotency claims vs store semantics** — "idempotent" claims must be checked at the store layer: SELECT-then-INSERT dedup (path-keyed) with NO unique constraint on the table = TOCTOU across processes. `PRAGMA busy_timeout` serializes
   commits, not read windows. Multi-process topology: each stdio MCP client spawns its own server process → N background loops per bank when the feature is enabled. If the same exposure pre-exists on the synchronous tool path, rate it
   SHOULD-FIX (low), not MUST-FIX, and offer a partial UNIQUE index as the fix.

6. **Settings-channel semantics** — strict parsers (`value == "true"`), null/missing key → safe default (verify `GetSettingAsync` returns null for unset keys), unknown values → fail-safe mode. Grep `SetSettingAsync` callers to confirm the
   CLI is the only writer of the new keys. Case-sensitive parsing with fail-safe fallback is a NIT, not a bug.

7. **Logging invariant** — nested static partial `Log`, `[LoggerMessage]`, explicit EventIds. Check collisions against the repo's per-class ranges (e.g. 1-5, 100, 200-205, 300-330, 400, 500-505). EventIds are scoped per logger category, so
   cross-class reuse is benign — note only same-category collisions.

8. **Safety claims → claims-vs-code table** — turn every PR-body claim into a verdict row with file:line evidence (off-by-default / never-mutates / propose-never-shares / explicit-gate-warns / idempotent / no-delete-path). Grep for the
   forbidden operation (e.g. `Delete*` calls) to prove "no delete path" rather than trusting the diff. State verdict per claim: HOLDS / HOLDS-with-caveat / FAILS.

9. **Test fidelity for loops** — fakes that don't model store idempotency (fake ShareAsync appends unconditionally, shared index never updates after a share) make loop tests pass even if the loop re-does work every tick; the real cross-run
   idempotency (index re-read + store dedup) then has no test. Flag as NIT with the fix: mutate the fake's index on share.

## Worked example

A worked review record (PR case study: finding shapes with evidence lines and the safety-claim table) is a useful companion when reviewing — keep the repo's own past review docs handy and cite the divergence explicitly.

## Gotchas

- Don't rate a pre-existing TOCTOU as MUST-FIX when the identical pattern already exists on the synchronous tool path — SHOULD-FIX (low) + owner question is the honest severity.
- `BackgroundServiceExceptionBehavior.StopHost` is the .NET default — verify the host config before claiming a faulted ExecuteAsync is contained.
- One host per process (e.g. ServerSetup picks stdio-app-host OR web-host) means `AddHostedService` runs once per process — the multi-loop question is about processes sharing one bank, not hosts within a process.
- Access-mode / auth guards usually gate only the MCP tool layer — a background service calling the store directly bypasses them. Raise as an owner question (intent), not an automatic finding.
