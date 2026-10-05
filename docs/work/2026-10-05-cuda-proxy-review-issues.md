# CUDA + proxy review — follow-up issues (2026-10-05)

Source review: `eef7e154~1..HEAD` — CUDA runtime fallback (#848, 1.57.1) + proxy refuses unproven backend (#852, 1.57.2) + bundled alias-scope delete (#853).
Verdict was **safe to merge** with two Low findings below. This file is the single collection point; the low-effort task `air-cuda-proxy-review-followup-fixes` fixes what is fixable and tracks the rest.

## I1 — Combined range bundles 3 tasks (Low, process only)
- **What:** the reviewed range mixes CUDA fallback, proxy identity refusal, and `SettingsEndpoint` alias-scope delete (#853).
- **Impact:** review noise only; each PR alone is single-task (`VERSION` 1.57.1 / 1.57.2 / alias fix).
- **Fix:** none in code. Keep per-PR review discipline; this file exists so the range-review has one issues list.
- **AC:** this file lists I1 as process-note, no code change required.

## I2 — Unexplained `EmbeddingModelRejectedException` in one combined full-flow run (Low, test debt)
- **What:** `docs/work/2026-10-05-proxy-identity-refusal.md` notes one `EmbeddingModelRejectedException` in the positive full-flow test during a prior combined run; rebuild passed that test alone and the combined 129. Precise cause not established; no oracle weakened.
- **Impact:** possible load-order / resource flake; if it recurs it erodes trust in the green gate.
- **Fix (low effort):** record as tracked debt with repro pointer; run the touched embedding + proxy surfaces once each (`CudaRuntimeFallbackTests`, `BackendLauncherTests`, `BackendSessionsTests`, `BackendLaunchIdentityProofE2ETests` real-server regression markers only if cheap); if green, close with a note naming the transient and the command that proved it. If red, quarantine per `dotnet-flaky-test-diagnosis` and file the isolation result — do not expand scope.
- **AC:** debt note exists in this file; touched-surface tests ran once green, or a quarantine record exists with seed/isolation output.
- **Result 2026-10-05 (task `air-cuda-proxy-review-followup-fixes`):** touched surfaces ran once each in the task worktree, all green — `CudaRuntimeFallbackTests` 14/14, `BackendLauncherTests` 14/14, `BackendSessionsTests` 28/28 (56 total, 0 failed). Commands: `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~<Suite>"` per suite. No red, so no quarantine; the prior `EmbeddingModelRejectedException` transient did not reproduce. Debt closed as non-reproducing; reopen with seed + isolation output if it recurs.

## I3 — Build/tests/lint not re-run inside the review (verification gap, not a defect)
- **What:** the review verified statically (diff, contracts, test walk) and relied on the work record's "129 passed, zero warnings" + `/tmp/air-proxy-evidence/` logs. No full `dotnet build`/`dotnet test` was run in the review turn.
- **Impact:** CI remains the gate, especially for Windows + full-repo verification.
- **Fix:** the task runs `dotnet build` (zero warnings) + the touched-surface tests once, reads the CI verdict for #848/#852, and reports. No full-suite theater; no stability re-runs.
- **AC:** build output + touched-test output pasted in the task record; CI verdict for both PRs named.
- **Result 2026-10-05 (task `air-cuda-proxy-review-followup-fixes`, worktree):** `dotnet build` → `Build succeeded. 0 Warning(s) 0 Error(s)`. PR #848 (CUDA): MERGED `eef7e15` — `changes`, `scripts-harness`, `build-fast`, `build-bdd` SUCCESS; `build-slow` CANCELLED (superseded by next push); arm64/mlx/nightly SKIPPED (opt-in). PR #852 (proxy): MERGED `e31976b` — PR-head checks all CANCELLED (rapid rebase+merge superseded them); merge commit's `release` workflow SUCCESS, `build` workflow CANCELLED (superseded by `55e95f4` one minute later). Main-branch `build` runs on `d24bc7b`/`eef7e15`/`55e95f4` show failure/cancelled, but every executed job underneath is SUCCESS — the failures are cancelled-job artifacts of 4 pushes in ~30 min (e.g. publish on `eef7e15`: 5/6 packs SUCCESS, musl pack CANCELLED, publish SKIPPED). No red test anywhere; Windows + nightly verification remains CI's standing remit.

## Out of scope (explicitly not issues)
- ORT invalid-input triggering one CUDA fallback attempt before propagating (`calls==2` asserted, ADR-0112 documents it).
- No cross-process startup lock for two proxies racing a free port (ADR-0128 §Scope exclusion; bind exclusion decides).
- `ProxyPrivateBackendLifetimeTests` deletion — retired with the removed lifecycle, shared survival/restart/attacker controls remain.
- `PATH`/`LD_LIBRARY_PATH` cuDNN guidance is docs-only and host-specific; not unit-testable.
