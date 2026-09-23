# P4 implementation record — attach-or-start revert, proof-gated handover, serve/restart, CLI surface

Branch: `task/air-identity-proof-p4`, worktree `.ai-badger/worktrees/air-identity-proof-p4`, based on
`0bd902fc` (P1/P2/P3 + ADR-0106 merged). Plan: `docs/work/plans/PLAN-FINAL.md` §2 P4. Decision:
`docs/adr/0106-attach-or-start-with-backend-identity-proof.md`.

## What landed

The launch default is attach-or-start again, behind the identity proof.

- The acquire probes the configured port. A listener that proves it holds this root's identity key
  is attached to, and nothing is spawned. Nothing listening starts one on the configured port.
  Anything else (unproven listener, failed proof, unanswered probe) gets a private ephemeral
  fallback, after the bounded nonce challenge, so zero secret bytes reach an unproven listener. The
  only requests an unproven listener sees are the existing `POST /mcp "x"` probe and the challenge.
- The fallback is proof-gated too. The child must prove before its URL is handed to the token
  reader, so a racer on the child's port gets nothing.
- The dispose-time stop proves before `/shutdown`. A listener that cannot prove at stop time is
  sent nothing and reported as not stopped (F3).
- `serve` attaches and exits 0 only to a proven listener; an unproven holder is refused with exit 3
  and the manual-stop remedy. Cross-root attach is therefore refused: another root's server cannot
  prove against this root's key.
- Bare `serve --restart` cycles a proven server. An unproven holder is refused before the token file
  is read.
- `--attach` is gone from the root and serve trees. Passing it is an unrecognized argument (exit 9).
- Fallback idle bound: the one-shot settings fallback is spawned with `--idle-timeout 5m`; the
  proxy's fallback child keeps K1a (stopped with the proxy). The configured-port instance and a
  proven attached server are shared and never stopped by a client.

Files: `BackendSessions.cs` (policy + stop proof + ids 690/691), `BackendLaunchArguments.cs`
(`FallbackIdleTimeout`, `PrivateServeArguments(config, idle)`), `ProxyRunner.cs` (constructs the
prover from the launch's own options), `CliSettingsBackend.cs` (same policy, 5-minute fallback,
disclosure re-worded), `ServerRestart.cs` / `IServerRestart.cs` / `RestartOutcome.cs`
(`AttachRequired` → `Unproven`, proof before token read), `NodeRunner.cs` (prove before attach,
proof-aware refusal lines, UX-F10 line re-shaped), `NodeRegistration.cs`,
`CliCommandTree.cs` / `CliArgs.cs` / `RootCliOptions.cs` / `CliOptionsExtensions.cs` /
`ServeCommands.cs` / `ServerConfig.cs` (surface removal), `AppRegistrations.cs` (`IIdentityProver`
registration), `docs/reference/logging-event-ids.md` (count 189 → 191, rows 650-657 and 688-691).

## Corrections and simplifications against the brief

1. **The shared policy lives in `BackendSessions.AcquireSharedAsync`.** A new standalone class would
   have owned a new EventId block and broken nothing, but A12 says the new ids go "inside the owning
   blocks", and `LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners` leaves exactly
   one home for both: `BackendSessions` can grow `[688-691]`, while `CliSettingsBackend` (`687`) and
   `ServerRestart` (`650-657`) cannot absorb 690/691 without overlapping `[688-689]`. The settings
   path calls `BackendSessions.AcquireSharedAsync` and `BackendSessions.Log` directly; same assembly,
   one owner, no interleave. `IIdentityProver` is registered factory-only in
   `RegisterCoreMemoryServices`, because `IdentityProver` (sealed) has two public constructors and
   `AddRequiredSingleton`'s extra concrete registration would be ambiguous to activate.
2. **`IIdentityProver` is constructed in `ProxyRunner.RunAsync` from the launch config**, not
   resolved from DI. The proxy graph does not register `InfrastructureOptions`, and a DI singleton
   would bind the proof to whichever root registered first. `TestData.CreateProxyRunner()` therefore
   stays a proxy-only graph.
3. **The F5 gate is a unit gate with an explicit `AttachCalls == 0` assertion.** The real-hanging
   listener variant adds minutes and cannot see the failure mode; the mode that matters is "an
   unanswered probe is not `NotListening`", and the mutation run (M9) proved the assertion catches it.
4. **`ProxyTokenRefusedE2ETests` was reshaped rather than re-enabled.** The P3 report expected it to
   "become true again" under attach-or-start. It cannot: it was cross-root by construction, and under
   the proof a cross-root proxy falls back to a working private backend instead of surfacing the
   gate's verdict. It now shares the backend's root and rotates the token file after the server
   minted it, so the proof passes and the token gate is the thing that refuses. `ProxyWireE2ETests`
   needed the same root sharing plus the proof route mapped; without it the bare proxy fell back and
   never dialled the fixture whose headers the class records.
5. **`CliContractTests` was red since P3, not since P4.** Its fixture root had no bank (F39), so every
   scenario exited 22. It now seeds a bank; its "no server reachable" row became
   `SettingsCommand_WithAHangingHolder_FallsBackPrivately_AndWarns`, because exit 18 is no longer
   reachable on that path once a private fallback exists.
6. **Two gates were strengthened after mutation showed them passing for the wrong reason.**
   `HangingProbe_ChallengesThenFallsBack` and the unproven-listener tests used the same port for the
   configured endpoint and the fake fallback URL, so `Calls[0]` could not tell which listener was
   challenged; the fake launcher now returns distinct private URLs. The hanging-probe gate also
   asserts `AttachCalls == 0`, which is what made M9 red.

## Rejected

- A standalone acquire-policy class with its own EventId block (see correction 1).
- Resolving `IIdentityProver` as a DI implementation singleton (correction 2).
- Treating `Unanswered` as `NotListening` and spawning on the configured port. That is the F5 defect:
  a hanging holder would burn the launcher budget on a child that cannot bind.
- Keeping a silent `--attach` no-op. The absence gates treat that as a failure; M5 proved they do.

## Gates, RED evidence, and mutation proof

Initial RED, before the fix (old private-spawn/shared-acquire code, new tests present):

- Acquire channel: `dotnet test --filter "FullyQualifiedName~BackendSessionsTests|FullyQualifiedName~CliSettingsBackendTests"`
  → `Test run summary: Failed! total: 24 failed: 5 succeeded: 19`. Named failures included
  `AcquireAsync_WithAProvenListener_AttachesWithoutSpawning` (`InvalidOperationException : a proven
  listener must be attached to, never start anything`), `AcquireAsync_WithASquatter_FallsBackPrivately_WithABoundedIdleTimeout`
  (`launcher.AcquireCalls should be 0 but was 1`), and `OpenAsync_WhenTheListenerProves_AttachesAndDoesNotSpawn`
  (`launcher.Calls should be 0 but was 1`).
- Serve/restart channel: `--filter "FullyQualifiedName~ServeRestartTests|FullyQualifiedName~NodeRunnerTests|FullyQualifiedName~ObservabilityRunnerTests"`
  → `total: 35 failed: 10`, including `Restart_Bare_AgainstAProvenServer_CyclesIt`,
  `Restart_Bare_AgainstAnUnprovenHolder_RefusesExit3_SendsNoToken`,
  `Serve_OnAProvenListener_ReportsAttachAndExitsZero`,
  `Serve_OnAnotherRootsServer_RefusesExit3_WithTheRemedy`, and
  `AttachedSecondServe_StillReportsTheOwnerPid`.
- CLI surface: `--filter "CliArgsTests.AttachOption_IsAbsentFromBothRoots|CliArgsTests.ServeAttach_IsAnUnrecognizedArgument_Exit9|CliArgsTests.Parse_RootQuietBeforeABrokenVerb"`
  → `total: 4 failed: 3 succeeded: 1` (the arity PC already passed on `--quiet`; it is a positive
  control, not a RED gate).

Mutation proof (each mutation applied, gate run, file restored with `git checkout`):

| gate | mutation | result |
|---|---|---|
| `Acquire_WithAProvenBackend_Attaches_AndSpawnsNothing` | Answered also takes the launcher branch | RED |
| `Acquire_WithNoListener_StartsOnTheConfiguredPort` | NotListening calls `StartPrivateAsync` | RED |
| `Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes` | challenge skipped before fallback | RED |
| `HangingProbe_ChallengesThenFallsBack` (F5) | `Unanswered` treated as `NotListening` | RED |
| `DisposeStop_WithARacerOnTheDeadChildsPort_SendsNoSecretBytes` (F3) | stop proof removed | RED |
| `ProveIsRequired_BeforeEveryTokenBearingRequest` | stop proof removed | RED |
| `DisposeStop_WithAProvenBackend_SendsTheShutdownAfterTheProof` | stop proof removed | RED (via the hostile half) |
| `Restart_Bare_AgainstAProvenServer_CyclesIt` | prover challenged with `port + 1` | RED |
| `Restart_Bare_AgainstAnUnprovenHolder_RefusesExit3_SendsNoToken` | restart proof removed | RED |
| `Serve_OnAProvenListener_ReportsAttachAndExitsZero` | attach arm always refuses | RED |
| `Serve_OnAnotherRootsServer_RefusesExit3_WithTheRemedy` | attach arm never refuses | RED |
| `AttachOption_IsAbsentFromBothRoots` / `ServeAttach_IsAnUnrecognizedArgument_Exit9` | hidden `--attach` no-op re-added | RED |
| `AcquireShared_WithAnUnprovenListener_FallsBackPrivatelyWithAWarning` / settings squatter gate | 690 log call removed | RED |
| `Parse_RootQuietBeforeABrokenVerb_KeepsTheVerbPath` | `ContainsVerb` skips the token after every flag | RED |
| `Shutdown_AfterStartingTheConfiguredPortInstance_NeverStopsIt` (K1a) | every acquire recorded private | RED |

## Verification

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark" --no-build`:
  `total: 3937, failed: 0, succeeded: 3936` (1 skipped).
- Touched Slow classes: `BackendSessionsTokenExposureTests`, `ProxyPrivateBackendLifetimeTests`,
  `ServeRestartTests`, `NodeRunnerTests`, `ObservabilityRunnerTests`, `CliBankWriteTests`,
  `CliSettingsSharedBackendTests` — all green (41/41 and 39/39 in the two grouped runs).
- Touched Nightly classes: `CliContractTests` (3/3), `ProxyLaunchE2ETests`, `ServeRestartE2ETests`,
  `ProxySpawnedBackendE2ETests`, `ProxyWireE2ETests`, `ProxyTokenRefusedE2ETests` (12/12).
- One transient failure was observed on the first run of the grouped Nightly set:
  `ProxyLaunchE2ETests.BareLaunch_DoesNotOpenTheBank` failed once, then passed in isolation and in
  two full re-runs of the set. HYPOTHESIS: cross-test timing in that fixture (it overwrites the bank
  the same-root backend serves while background maintenance starts); not reproduced, and the class
  is `[RetryFact]`. Worth a Nightly watch.

## Commits

1. `1ebe4208` attach-or-start behind the identity proof with a proven private fallback.
2. `b19827e9` prove before attach and cycle; remove `--attach` everywhere.
3. `71e4a5f4` pin proof-before-token ordering for acquire and dispose stop.
4. `a0dac5a7` proof-capable restart-timeout fixture; touched Nightly E2E classes green.
5. Strengthened hanging-probe/unproven-listener gates (this record's commit).
