# Fail closed when backend identity cannot be confirmed

Date: 2026-10-05

Status: implementation proposal; no production change or executed RED/GREEN evidence in this planning task.

## Contract and scope

A proxy may start a backend only after the configured loopback endpoint is positively observed as `ProbeVerdict.NotListening`. A responding server whose identity cannot be confirmed must cause a refusal, with zero backend starts. An inconclusive probe must also prevent a start unless cryptographic proof succeeds and allows attachment. Once one shared launch has been attempted, failure to prove its returned listener must refuse the acquire; it must never launch a second backend on another port.

This applies to the endpoint selected by `ServerConfig.Port`, including proxy reopen and the CLI settings commands that share acquisition. It does not search for ai-raccoon processes or listeners on other ports. A server on another configured port does not prevent an intentional start here. Simultaneous proxies that each observe a free port may both attempt a shared start before one wins the bind. Preventing all such attempted processes would require separate cross-process coordination and is outside this change. The required guarantee is no launch after an observed responding or inconclusive listener, and no private fallback or second launch after failed proof.

Server identification already exists. Preserve the ECDSA nonce challenge, root/port binding, read-only trust-anchor handling, budgets, and the rule that an unproven peer receives no token, key material, or tool payload. A JSON-RPC body, product name, PID, or version is insufficient identification.

## Source evidence

Line numbers refer to the planning baseline and may move during implementation.

| Evidence | Consequence |
|---|---|
| `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:136-168` | `AcquireSharedAsync` invokes private fallback after failed proof of both an existing listener and a URL returned by the shared launcher. |
| `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:175-207` | Reopen may reuse a private child; `FallbackAsync` calls `StartPrivateAsync`, whose launch arguments select an ephemeral port. |
| `src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs:144-159` | `AcquireAsync` uses boolean `RespondsAsync`; false authorizes `Process.Start`, although false includes an inconclusive probe. |
| `src/AiRaccoon/Hosting/Common/ServerProbe.cs:42-111` and `ProbeVerdict.cs` | `RespondsAsync` is true only for `Answered`. Non-JSON-RPC replies, timeout, reset, and other transport failures yield `Unanswered`; only connection refusal yields `NotListening`. `Unanswered` is the existing representation of unknown, not a new `Unknown` enum member. |
| `src/AiRaccoon/Hosting/Proxy/IdentityProver.cs:140-176` | Missing/unreadable key fails read-only; the challenge and verification may fail even though a real server is listening. Caller cancellation propagates. |
| `src/AiRaccoon/Settings/CliSettingsBackend.cs:62-93` | Settings acquisition calls the same shared policy and currently interprets fallback failure separately before reading the token. |
| `docs/adr/0106-attach-or-start-with-backend-identity-proof.md`, Decision and Residuals 3/10 | Private fallback is deliberate current policy; changing it requires updating the decision and the missing-key/DoS descriptions. |
| `BackendSessionsTests.AcquireShared_WhenTheStartedListenerCannotProve_FallsBackPrivately`, `AcquireShared_WithAnUnprovenListener_FallsBackPrivatelyWithAWarning`, and `HangingProbe_ChallengesThenFallsBack` | Existing tests require the unwanted behavior rather than reproducing the requested refusal. Replace their contracts and retain their security assertions. |
| `BackendSessionsTokenExposureTests.Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes`; `BackendLaunchIdentityProofE2ETests.AcquireCellAsync` | Integration and executable-level threat tests also assume successful fallback and must move to refusal plus zero launches. |

## Acquisition policy

| Initial probe | Identity proof | Allowed action | Launch count for this acquire |
|---|---|---|---|
| `Answered` | proven | Attach to configured endpoint | 0 |
| `Answered` | any failure, including `NoKey`, bad signature, malformed response, status failure, timeout | Refuse as unproven; preserve reason | 0 |
| `Unanswered` (unknown) | proven | Attach to configured endpoint; proof establishes identity despite the MCP probe | 0 |
| `Unanswered` | any failure | Refuse as unproven/inconclusive; preserve probe and proof results | 0 |
| `NotListening` | not yet attempted | Ask shared launcher to acquire configured endpoint; launcher re-probes before starting | At most 1 shared attempt |
| Shared launcher's re-probe is `Answered` | proof afterward proven / failed | Attach / refuse respectively; launcher itself starts nothing | 0 |
| Shared launcher's re-probe is `Unanswered` | no URL returned | Refuse unavailable/inconclusive; no process start | 0 |
| Shared launcher's re-probe is `NotListening` | returned listener proven | Start on configured port and attach | 1 |
| Shared launcher's re-probe is `NotListening` | returned listener fails proof | Refuse as unproven; no fallback and no second launch | 1 |
| Caller cancellation at any stage | not applicable | Propagate cancellation; no subsequent proof, retry, or launch | No new launch after cancellation |

Re-probe `Unanswered` need not retry identity through the launcher. Conservative refusal is sufficient; an ordinary reopen can perform the full acquisition again. Polling after a shared start may observe `Unanswered` and continue within the existing startup budget, but cannot use it to authorize another start. Preserve the last-chance probe after a child exits: only `Answered` hands a candidate URL to the prover; ambiguity yields unavailable, never a replacement spawn.

## Minimal implementation shape

Keep `BackendSessions.AcquireSharedAsync` as the policy owner and `BackendLauncher` as the process mechanism. Replace both fallback branches with an unsuccessful `AcquireOutcome` that retains the `IdentityProofFailure`. Remove `Fallback` from that outcome after converting callers. A URL must be non-null only after proof succeeds.

Use `ProbeAsync` rather than boolean `RespondsAsync` for the launcher's pre-start decision, and check the caller's cancellation token immediately before `Start`. Preserve the boolean API for unrelated consumers if needed; no probe schema or identification endpoint redesign is required.

Both proxy and settings callers should translate proof failure to existing `ErrorCode.Server.Unproven` (50), using `IdentityProof.RefusalText` for the reason. Ordinary startup failure keeps its existing reach/start error. The diagnostic must name the configured endpoint, say that no extra backend was started, and suggest checking the data root/key or explicitly stopping the conflicting server before retrying. Never advise automatically replacing a key while the server is live. Preserve existing bank-presence and no-token refusal behavior.

Every reopen reruns configured-endpoint acquisition. Remove private-backend tracking, sticky private reuse, private shutdown paths, and private-only launch arguments/API when a repository-wide caller search confirms they have no other production consumer. Keep shared lifetime owned by `IdleWatchdog`; a failed proof never authorizes an HTTP shutdown or killing an unrelated process. Shared-child cleanup on existing startup timeout/cancellation is unchanged and must not be silently redesigned here. Keep executable resolution fallback from ADR-0116: that rescues a deleted executable path and is unrelated to spawning a private backend.

Reserve retired error/log numeric IDs rather than recycling them. Update their documentation if they are no longer emitted. Keep restart channel-binding security tests because `serve --restart` still uses proof before shutdown; dispose tests specific to a removed private-child lifecycle can be retired with that reason.

## TDD design

The oracle is the requested contract above: identity failure is never permission to start another backend. Each test asserts the refusal/success together with starts and secret-bearing traffic, rather than checking only text.

The first RED must reproduce the reported scenario with a real server. Seed an isolated bank, start a genuine ai-raccoon on a reserved configured port, and wait for readiness. Confirm a real proof succeeds as the positive control. After the server has cached its signer, atomically replace the verifier's local identity-key with a different valid private P-256 key, preserving required permissions. The server remains live but the production `IdentityProver` cannot confirm its identity (key-ID mismatch or signature rejection). Construct a fresh `IdentityProver` after replacement: `IdentityKeyFile.ReadStateFile` (`IdentityKeyFile.cs:44`) caches `_signer`, so reusing the positive-control prover would retain the old trust anchor. Assert a concrete non-null failure rather than requiring an incorrect guessed failure enum. This simulates stale trust while exercising real server identification, not a fake server standing in for it.

Call the production acquisition composition with real `ServerProbe` and `IdentityProver`, and a recording launcher decorator that delegates to the real launcher. The baseline must fail `starts == 0` because it takes the private fallback. Capture child handles/URLs in the fixture to clean up the baseline-spawned child without sending credentials to an unproven peer. Restore the original key in `finally`, then stop the original server safely. A fresh prover and independent HTTP client after restoration prove the original server survived; do not reuse the faulted verifier. The fixture must close every process even when assertions fail.

Add a marker-backed real-process integration test through the existing `BackendSessions` constructor's explicit `processPath` seam. Supply a test-owned wrapper executable that appends a durable marker for every `serve` invocation before forwarding to the real binary; keep stdout clean. Use the live real server and fresh faulted production prover above. Exclude the explicitly started original server from the marker count. Assert refusal, no backend URL/session, no `serve` marker, and continued original-server liveness. In the baseline, the marker must record the extra `serve --port 0`; this is RED evidence independent of logs.

For actual proxy executable E2E, use the production binary and existing process helpers to verify refusal exit, no MCP initialization or secret-bearing traffic, and original-server survival. Do not assume wrapping its initial invocation intercepts children: production `Environment.ProcessPath` still selects the real binary. The marker test at the existing constructor seam proves real spawning behavior; executable E2E proves composition wiring. Do not add an environment override or production test hook just to instrument child launches.

| Behavior | Level and observation | Mutation that must redden it |
|---|---|---|
| Real server running, proof fails | Integration reproduction above: failed proof, acquire refusal, zero delegated starts, original still live | Restore the failed-proof `StartPrivateAsync` branch |
| Responding foreign HTTP and JSON-RPC impersonator | Unit matrix plus real HTTP listener: all proof failures, zero shared/private calls, zero token-bearing traffic | Treat failed proof as absence |
| Probe timeout/reset/non-JSON-RPC reply | Unit `Unanswered` cases and launcher marker test: no start; bounded refusal | Replace `ProbeAsync == NotListening` launch guard with `!RespondsAsync` |
| Free endpoint, then listener appears before launcher starts | Scripted probe sequence `NotListening` then `Answered`/`Unanswered`; marker stays empty | Drop the launcher's second verdict check |
| Free endpoint, one shared start, proof fails | Unit sequence: one shared call, zero private calls, null acquired URL, preserved reason | Return the unproven URL or restore post-start fallback |
| Successful existing proof and free-port startup | Existing integration controls: zero/one starts respectively, functional MCP/settings operation | Refuse all acquires or skip proof before returning URL |
| Reopen after live endpoint proof fault | Public `OpenAsync`/forwarder reopen: refusal, no extra session/token request, zero new starts | Reintroduce private reuse/acquire bypass |
| CLI settings against unproven listener | Public core plus executable consumer: `Server.Unproven`, no store/HTTP settings request, zero starts | Ignore proof failure in CLI caller |
| Cancellation before or during probe/proof | Unit cancellation: cancellation propagated, no following launch/token request | Swallow caller cancellation or remove pre-start cancellation check |
| Shared-start timeout/exit and last-chance ambiguity | Launcher marker: at most one start, no retry spawn, existing bounded outcome | Start another process on poll/last-chance failure |
| Genuine proof + missing token | Retain existing no-token refusal tests: no token minted by verifier, no further spawn | Read/mint token before proof or auto-start on no token |

Unit tests use scripted probes/provers and recording launchers only to remove network/process cost. Real-network tests establish that these doubles represent the production verdicts. Use `FakeTimeProvider` for launcher polling where existing seams support it. Real process tests use readiness handshakes and cancellation deadlines, never elapsed-time performance assertions. Reserve loopback ports until the named bind transfer, isolate all roots and marker files, use existing environment scopes for encryption settings, and serialize process/global environment tests in their existing collection. Production CSPRNG remains intact; fault keys/nonces belong only to disposable fixtures. No machine-wide process counts or unbounded sleeps.

Proposed independent regression names are `BackendSessionsTokenExposureTests.Acquire_RealRunningServerWithChangedTrustAnchor_RefusesWithoutStartingBackend`, `BackendSessionsTokenExposureTests.Open_RealRunningServerWithChangedTrustAnchor_WritesNoServeMarker`, and `BackendLaunchIdentityProofE2ETests.Proxy_RealRunningServerWithChangedTrustAnchor_RefusesAndLeavesOriginalAlive`. These are proposed methods, not existing executed tests. The first RED gate is `dotnet test --filter "FullyQualifiedName~BackendSessionsTokenExposureTests.Acquire_RealRunningServerWithChangedTrustAnchor_RefusesWithoutStartingBackend"`, with a nonzero count and failed extra-start assertion.

For each behavior, write and run one failing test before its production change, then run GREEN and record counts. For already-green controls, apply/revert the named mutation and record observed RED/GREEN. This document predicts failures from source inspection; none has been executed in this planning task.

## Serialized implementation DAG

The graph is `S1 -> S2 -> S3`; S3 is the single integration sink. No parallel file ownership or separate test-only completion step is needed. The accompanying JSON supplies the graph schema; all acceptance criteria remain pending until implementation.

### S1 — Refuse unproven acquisition and reproduce the live-server defect

Owner: .NET engineer with test-engineer review. Effort: high. Dependencies: none.

Owned files: `BackendSessions.cs`, `BackendLauncher.cs`, `IBackendLauncher.cs`, `Hosting/Common/BackendLaunchArguments.cs`, `CliSettingsBackend.cs`, unit `BackendSessionsTests.cs` and `CliSettingsBackendTests.cs`, integration `BackendLauncherTests.cs` and `BackendSessionsTokenExposureTests.cs`, directly affected test doubles.

- [ ] AC1: The real-server proof-fault test first fails on extra starts, then passes with zero starts and original-server survival. Gate: filtered integration test with nonzero test count and recorded RED/GREEN output.
- [ ] AC2: Every policy-table row is covered by a behavioral test; launcher starts only after `NotListening`, and post-start failure has one shared attempt and no second launch. Gate: focused session/launcher unit and integration suites plus guard mutation RED/GREEN.
- [ ] AC3: Proxy and CLI proof refusals use `Server.Unproven`; no unproven URL, settings store, token-bearing request, or minted trust/token file escapes. Existing no-bank, no-token, proven attach/start, cancellation, and executable-resolution controls pass. Gate: focused consumer/security tests with start and request counts, then `dotnet build`.

Write the first regression before altering production. Convert current fallback-expecting tests in this package rather than leaving contradictory expectations alongside the new rule. Remove unreachable private mechanics as callers disappear; do not leave a second acquisition policy.

### S2 — Prove executable behavior and reopen/security compatibility

Owner: test engineer with .NET engineer support. Effort: high. Depends on S1.

Owned files: `tests/AiRaccoon.Tests/E2E/BackendLaunchIdentityProofE2ETests.cs`, `tests/AiRaccoon.Tests/E2E/ProxyProcess.cs`, `tests/AiRaccoon.Tests/Integration/Setup/Serve/IdentityProofChannelTests.cs` if assertions need adjustment, a new test-owned wrapper helper under `tests/AiRaccoon.Tests/TestHelpers/`, and remaining identity/reopen test adjustments under the unit/integration paths owned by S1.

- [ ] AC4: Real-process acquisition through the existing executable seam refuses with zero `serve` markers; the baseline/mutation records an ephemeral launch and reddens the assertion. Actual proxy executable E2E refuses and preserves original-server liveness. CLI and reopen show zero launcher calls. Gate: scoped integration marker test plus executable/consumer tests and liveness evidence.
- [ ] AC5: Acquire threat cells refuse every unproven attacker while preserving zero-secret assertions; proven attach/start flow and restart proof-channel cases still pass. Retire only private-dispose cases whose lifecycle was removed, documenting each reason. Gate: affected threat matrix and channel-binding suites, counts included.
- [ ] AC6: An independent `review-tests` pass finds no unresolved blocker/major issue in modified tests; resource controls and applied red-proof edits are reconciled. Gate: written test-review evidence and observed RED/GREEN for changed checks.

### S3 — Publish the new contract and close cross-consumer evidence

Owner: documentation engineer with architecture/code-review review. Effort: medium. Depends on S2.

Owned files: a new ADR superseding the fallback part of ADR-0106; ADR-0106 status/cross-reference and residual updates; `docs/how-to/configure-ai-raccoon-server.md`; `docs/reference/cli-reference.md`; `docs/reference/logging-event-ids.md`; source comments in the files changed by S1 if needed; remaining current fallback guidance found by scoped search. Historical changelog/research records remain historical.

- [ ] AC7: Current documentation matches the policy table, `Server.Unproven`, configured-endpoint scope, reopen behavior, shared lifetime, and concurrent-starter limitation. The ADR explicitly supersedes private fallback while retaining cryptographic identity. Gate: scoped fallback-guidance search and source-backed independent review.
- [ ] AC8: Cross-consumer evidence shows proxy startup, reopen, and CLI acquisition all reach the same policy, with zero additional backend starts on occupied/inconclusive/unproven endpoints and one-start positive controls. Gate: review S1/S2 recorded evidence against all request points and final diff; no unchecked implementation criterion may be called complete.

Use scoped commands such as `dotnet test --filter "FullyQualifiedName~BackendSessionsTests|FullyQualifiedName~BackendLauncherTests|FullyQualifiedName~BackendSessionsTokenExposureTests|FullyQualifiedName~CliSettingsBackendTests"`, then the affected E2E and identity-channel classes once their changes are complete. Require nonzero executed counts; a zero-test successful exit is not evidence. Run the modified suites and direct consumers, leaving the full repository sweep to CI. Release/version work follows repository policy when this implementation is released; this proposal itself does not release anything.

## Simpler-shape check

The small solution is to remove automatic private fallback and require the existing three-state probe before any start. It adds no port registry, global singleton, process inventory, transport redesign, new cryptography, or generic orchestration layer. The larger private-backend lifecycle disappears because the behavior that required it disappears. Further identity reliability work can follow a concrete reproduced proof failure; it must not restore duplicate launch as recovery.

## Planning review

An independent source review checked the reproduction and launch policy. Its required corrections are incorporated: construct fresh verifiers after fixture key replacement/restoration, and observe executable markers at the injected process-path seam rather than claiming an invocation wrapper intercepts self-launches. Concrete process-helper ownership and identity-channel gate targets are included. The three-step graph validates with no findings; all eight implementation criteria remain pending.
