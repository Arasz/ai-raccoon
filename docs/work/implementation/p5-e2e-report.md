# P5 E2E record — identity-proof end-to-end gates

Branch: `task/air-identity-proof-p5-e2e`, worktree `.ai-badger/worktrees/air-identity-proof-p5-e2e`, based on
`858ccbbe` (P1–P4 merged). Plan: `docs/work/plans/PLAN-FINAL.md` §2 P5. Decision:
`docs/adr/0106-attach-or-start-with-backend-identity-proof.md`.

Scope of this lane: the end-to-end gates, the re-shape of stale E2E assertions, and the never-attach
disposition. Docs, README and ADR edits belong to the P5 docs lane and are not in this record.

## What landed

Every gate runs the built binary. The real proxy and the real `serve --restart` verify, and the real
`serve` proves, so the crypto round trip under test is the product's own, not a fixture echo.

| gate | class | cells |
|---|---|---|
| `ThreatMatrix_FiveAttackers_ThreePaths_ZeroSecretBytesEverywhere` | `BackendLaunchIdentityProofE2ETests` | 18: {honest, squatter, relay-same-root-other-port, replay, planted-key, cross-root-copy} × {acquire, restart, dispose} |
| `DisposeStop_UnderAStatefulClient_EveryRacerOnADeadChildsPortGetsOnlyTheChallenge` | `BackendLaunchIdentityProofE2ETests` | the dispose path while the proxy holds stateful MCP sessions |
| `FullFlow_ProvenAttach_HandoverWorksAcrossProxySettingsAndRestart` | `BackendLaunchIdentityProofE2ETests` | proxy start-on-port → settings write/read → bare restart → second proxy reads the first one's write |
| `ProjectScopeRun_LeavesNoSecretAtTheDataRootTopLevel` | `SecretPlacementE2ETests` | quiet serve + proxy + settings in project scope; state-dir backup carries both secrets and restores them |
| `EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBankAndLeavesTheDirectoryEmpty` | `NoBankE2ETests` | 50 verbs derived from the command tree × {existing empty root, missing root} |
| `Serve_WithALegacyTokenFile_MigratesIt_AndTheProxyStillAttaches` | `TokenPathMigrationE2ETests` | legacy top-level token adopted, deleted, key minted, proxy attaches |

New fixtures:

- `TestHelpers/Impostor.cs` is a raw-TCP hostile listener. It claims the ai-raccoon name on
  `/observability`, answers the probe with jsonrpc, and answers `/identity/prove` through the attack's
  own strategy. It records every request byte (headers and body, by Content-Length or chunked framing)
  and exposes `SecretsSeen(secrets)`. `Squatter` stays capture-only, as D8 asks.
- `E2E/RealServe.cs` runs one `serve` of the built binary on a given root and port. Start returns once
  `/observability` names this very process. Disposal stops it through its own token-guarded
  `/shutdown`, and kills it only if that fails.
- `E2E/ProxyProcess.cs` drives the built proxy over raw JSON-RPC on stdio, stateless (`2026-07-28`) or
  stateful (`2025-11-25`), and ends the session the way a well-behaved client does: it closes stdin
  and waits. See finding F5 for why the SDK stdio client could not be used.

### What each cell asserts

- **Honest (positive control):**
  - Acquire: the proxy attaches to the real server on the configured port, and nothing is spawned.
  - Restart: the old server exits 0 and the restarting process owns the port.
  - Dispose: the proven child receives `shutdown requested over /shutdown` and exits.
- **Every hostile cell, in this order:**
  1. Zero secret bytes at the hostile listener. The secrets checked are the token, the PEM body lines,
     `PRIVATE KEY`, and the private scalar in base64, base64url and hex.
  2. No request carries the token header.
  3. Every request is the `POST /mcp "x"` probe, the challenge, or (restart only) the identify
     `GET /observability`.
  4. The challenge was reached: at least one challenge arrived, except for the planted key, which must
     see zero because the verifier refuses the key file before any challenge is sent.
  5. The proof failure named in the log is the one that stopped the attack: `Malformed` for the
     squatter, `BadSignature` for relay and replay, `NoKey` for a planted key at acquire and restart
     (`BadSignature` at dispose, where the key is already pinned), and `RootMismatch` for a copied root.
- **Relay** also asserts that the relayed answer was a genuine 200 signature, so only the port binding
  stopped it.
- **Replay** first proves the captured answer verifies for its own nonce.
- **Cross-root copy** is a real `serve` on another root whose state directory holds this root's key
  and token. It must stay alive, and at acquire the session must run on this root's own fallback child.

## Product defects found and fixed (each with its own failing test first)

1. **`edd81a2e` — our own code created the state directory world-readable, then refused it.**
   `SqliteConnectionFactory` (bank-first creation, for example `encryption show`) and
   `QuietFileLoggerProvider` (quiet mode opens its log before the mint) created `<dataRoot>/.ai-raccoon`
   with the process umask (0755). The D1 check then refused the directory `serve` had just made.
   Measured before the fix:
   - `ai-raccoon --data-root <fresh> --install-scope project --quiet serve` → exit 7, "the state
     directory … is not owner-only".
   - `encryption show` followed by `serve` → the same refusal.

   This broke every quiet (Hermes-style) project-scope first run. The fix: `BankPaths.CreateDirectory`
   creates a missing state directory as 0700 and leaves an existing one untouched. Red tests:
   `SqliteConnectionFactoryTests.OpenBankAsync_InAFreshProjectRoot_LeavesAStateDirectoryServeCanMintInto`
   and `QuietLoggingTests.Quiet_InAFreshProjectRoot_LeavesAStateDirectoryServeCanMintInto`, both failing
   on the mode assertion.
2. **`0a70f747` — `model code set default` downloaded into a typo root before the F39 guard.**
   The verb downloads the default code model before it touches the settings store. Measured at a
   mistyped `--data-root`: 4 files, including `model.onnx`, landed under `<typo>/models/…`, and only
   then did the verb exit 22. The dispatcher now runs `BankPresenceGuard.EnsureExists` first. Red test:
   `NoMintGuardCompositionTests.ModelCodeSetDefault_AgainstAnEmptyExplicitRoot_Exits22_BeforeDownloadingAnything`
   (exit 15, "must not start a download").

## Findings reported, not changed

- **F1 (HIGH, upgrade) — every existing install's state directory is refused after upgrade.** D1
  refuses an existing state directory that is group- or world-accessible. Every directory an earlier
  binary created has the umask's 0755:
  - the live `~/.ai-raccoon` is `drwxr-xr-x` (metadata read only, nothing written);
  - a user-scope 0755 root → `serve` exits 7 with the `chmod 700` remedy;
  - a legacy project root as an older binary leaves it (`.ai-raccoon` 0755 plus a 0600 top-level
    `mcp-token`) → `serve` exits 7 before the D3 migration can run.

  So the migration is unreachable on a realistic legacy root, and the owner's own server will not
  start after `dotnet tool update` until someone runs `chmod 700`. The product matches ADR-0106 as
  written; the ADR's upgrade note does not mention this. A possible direction, which is a ruling and
  not this lane's to make: tighten a directory the current uid owns and that is only
  group/world-*readable*, and keep refusing anything writable by others or owned by someone else.
  `TokenPathMigrationE2ETests` seeds a 0700 state directory for this reason.
- **F2 — restart sends the identify `GET /observability` before the proof.** No secret is involved,
  but it falls outside the ADR's literal invariant ("requests are limited to the existing `POST /mcp`
  probe and the bounded nonce challenge"). The matrix allows exactly this one extra request on the
  restart path and names it. Either the wording or the order needs to change.
- **F3 — a fallback child that fails its proof is never stopped.** `FallbackAsync` drops the URL, so
  `_privateBackends` never records it, and the child lingers until its idle timeout. Seen when the
  full-flow probe (DER signatures) left an orphan `serve --port 0` behind, which I killed by PID. This
  is a resource leak, not a secret leak.
- **F4 — a stateful-revision client costs two fallback children.** On a squatted port, a
  `2025-11-25` client makes the proxy acquire twice: its startup session, then a reopen at the
  client's revision. That means two children and two model loads. Both are proven and stopped at exit
  (see the stateful gate).
- **F5 — the SDK stdio client never lets the proxy's shutdown run.** `StdioClientSessionTransport`
  (MCP SDK 2.2.0) waits the full `ShutdownTimeout` for the process to exit *before* closing stdin, then
  kills the process tree. Under that client, the proxy's prove-then-stop never executes and its
  children die in the tree kill. That is not a secret leak, but the dispose-path defense only runs for
  clients that close stdin. `ProxyProcess` exists for this reason.
- **F6 — the proxy's `BackendSessions` warnings (690/691) reach stderr under `--quiet`.** The quiet
  ruling says nothing reaches stdout/stderr. This may be deliberate for the N1 disclosure; worth a
  ruling.
- **F7 — the no-bank remedy omits the scope.** `BankMissingException` says `ai-raccoon serve
  --data-root <root>` even for a project-scope launch. Followed literally, it creates a *user-scope*
  bank at `<root>/memory.db`.
- **F8 — `model download <repo>` at a typo root writes `<root>/models/…`.** It is not an auto-launch
  verb, so it is outside D4, but it is the same class of problem as defect 2.
- **F9 — three orphaned `serve --port 0` processes from the P4 worktree.** Root prefix
  `backend-sessions-attach-or-start` (`BackendSessionsTokenExposureTests`), ppid 1, up about 2h45m,
  probably from P4's mutation runs. They are not this lane's PIDs, so they were left alone.

## Red probes — mutation → red evidence → reverted → green

Every product mutation was applied, the class was run, the file was restored with `git checkout`, and
the class was re-run green. The output files are in the lane's scratchpad; the summaries below are
quoted from them.

| # | mutation | cells red | red evidence |
|---|---|---|---|
| M1 | `IdentityProver.ProveAsync` returns proven without proving | all 15 hostile cells + stateful gate (17/19; honest acquire and honest restart green) | acquire/restart: `listener.SecretsSeen(_secrets) should be empty but had 1` (the token); cross-root acquire: `ChildPorts() should not be empty` (proxy attached to the copied-root server); cross-root restart: restart asked the copied-root server to stop and served in its place (run hit its cap); dispose cells red at setup (no fallback child: the squatter was "proven") |
| M2 | dispose-time proof removed (`BackendSessions.StopPrivateBackendsAsync`) | the 5 hostile dispose cells + stateful gate (6/19) | `SecretsSeen … had 1` at every racer; cross-root: `attack.Server.Process.HasExited should be False` (the copied-root server was shut down with the shared token) |
| M3 | port dropped from the transcript (`port * 0`, both sides) | relay × 3 | `SecretsSeen … had 1` |
| M4 | constant nonce (`NewNonce` → 32 zero bytes) | replay × 3 | `SecretsSeen … had 1` |
| M5 | verifier stops refusing a non-owner-only key file (`EnsureFileIsPrivate` removed in `ReadStateKey`) | planted-key acquire + restart | `SecretsSeen … had 1`. Planted-key × dispose stays green: there the defense is the key pinned at acquire, and M1/M2 turn it red |
| M6 | endpoint signs any requested root (`rootFp = challenge.RootFp`) | cross-root × 3 | acquire: `ChildPorts() should not be empty`; restart: the copied-root server was stopped; dispose: `HasExited should be False` |
| M7 | verifier accepts a malformed 200 (`Malformed` → proven) | squatter × 3 + stateful gate | `SecretsSeen … had 1`. After the first M7 run, the dispose fixtures' configured-port listener was changed to answer 503, so no answer-check mutation can make it the backend and each dispose cell reaches its own attack |
| M8 | endpoint signs DER instead of IEEE-P1363 | `FullFlow_…` | the proxy started the instance, could not verify it, fell back, could not verify the fallback either, and exited 6 |
| M9 | `McpTokenFile` path at the data-root top level | `ProjectScopeRun_…` | top level was `[".ai-raccoon", "mcp-token", "mcp-token.lock"]` |
| M10 | owner-only state-dir creation reverted (`BankPaths.CreateDirectory` → default mode) | `ProjectScopeRun_…` | quiet `serve` exited: "the state directory … is not owner-only" |
| M11 | F39 guard removed from both acquire paths | `EveryAutoLaunchVerb_…` | 49 verbs violated: at the missing root each exited 0 and created it (a bank was minted); at the 0755 typo root each exited 18 instead of 22 |
| M12 | legacy adoption disabled (`_legacyPath = null`) | `Serve_WithALegacyTokenFile_…` | `File.Exists(legacyPath) should be False` ("must not be left at the top level") |
| M13 | migration writes a fresh token instead of adopting | `Serve_WithALegacyTokenFile_…` | state-dir token ≠ legacy value ("adopted, not replaced") |

The first matrix runs also showed two fixture-honesty problems, which were fixed before any probe was
trusted:

- The reason-line and exit-code assertions went red before the secret check, so the red named a
  symptom rather than the leak. The secret check now runs first (`86a236c4`).
- "The copied-root server is still alive" cannot see an attach at acquire. That cell now requires the
  session to run on a fallback child.

## Never-attach disposition (A7)

| gate | disposition | reason |
|---|---|---|
| `BackendSessionsTokenExposureTests.OpenAsync_WithASquatterHoldingTheConfiguredPort_DoesNotSendItTheToken` | updated (P4) → `Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes` | the zero-token spirit survives; the shape is now fallback plus proof |
| `…OpenAsync_WithAttachAgainstARealServer_OpensASessionThroughTheToken` | updated (P4) → `Acquire_WithAProvenBackend_Attaches_AndSpawnsNothing` | attach is the default again; the flag is gone |
| `CliSettingsTokenExposureTests.AcquireAsync_AgainstAListenerHoldingTheConfiguredPort_SendsItTheToken_AsRuledAcceptable` | deleted (P4, `1ebe4208`) | its premise, K1a's accepted handover, is exactly what ADR-0106 reverses (D8) |
| `CliSettingsTokenExposureTests.AcquireAsync_AgainstARealServer_ReadsThroughTheToken_WithNoAttachFlagNeeded` | deleted with its class (P4); successor `FullFlow_ProvenAttach_HandoverWorksAcrossProxySettingsAndRestart` (this lane) | the settings read/write attaching to a proven instance is now gated end to end |
| `ServeRestartTests.RestartWithoutAttach_AgainstAnAiRaccoonListener_RefusesAndSendsNoToken` | updated (P4) → `Restart_Bare_AgainstAnUnprovenHolder_RefusesExit3_SendsNoToken` | the refusal reason is now a failed proof; the zero-token assertions are kept |
| `ServeRestartTests.WithoutRestart_WithAttach_AnExistingServerIsStillAttachedTo` | updated (P4) → `WithoutRestart_OnAProvenListener_AttachesAndExitsZero` | flag dropped; the impostor arm is added |
| `ServeRestartTests.AServerThatRefusesOurToken_ExitsRestartTokenRefused_AndNeverAttaches` | kept | "never attaches" here means a refused token ends the restart instead of falling back to attach; this is still true and still wanted under ADR-0106 |
| `BackendLauncherTests.StartPrivate_*` (4) | kept | they gate the fallback machinery and the stdout-only URL trust |
| `ProxyPrivateBackendLifetimeTests` pair | updated (P4) → `Shutdown_StopsTheEphemeralFallback_ButNeverTheConfiguredPortInstance`, `Shutdown_AfterStartingTheConfiguredPortInstance_NeverStopsIt` | two-class lifetime rule (K1a) |
| `CliSettingsSharedBackendTests` pair | kept | attach-or-start positive controls |
| `CliArgsTests.Parse_RootAttachBeforeABrokenVerb_KeepsTheVerbPath` | updated (P4) → `Parse_RootQuietBeforeABrokenVerb_KeepsTheVerbPath` | the only gate for the arity fix; re-fixtured on `--quiet` |
| `ProxyLaunchE2ETests`, `ProxyWireE2ETests`, `ProxyTokenRefusedE2ETests`, `ProxySpawnedBackendE2ETests`, `ServeRestartE2ETests` | updated (P4) | share the root and hold the identity key, so they attach by proof |
| E2E token-path assertions (`McpServerLaunchArgsE2ETests`, `ProxySpawnedBackendE2ETests`, `ProxyTokenRefusedE2ETests`) | updated (this lane, `00b64297`) | now read `McpTokenFile.Path` instead of `<dataRoot>/mcp-token`; right today only because the user-scope state directory is the data root |
| `McpServerLaunchArgsE2ETests.BareLaunch_ServesTheFullToolSurfaceOverARealPipe` | updated (this lane, `00b64297`) | red since P3: it launched at an empty non-default root, which F39 refuses with 22; it now seeds the bank |

## Commands run (each touched class, once green after its last edit)

- `dotnet build` → 0 warnings, 0 errors.
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*SqliteConnectionFactoryTests' --filter-class '*QuietLoggingTests' --filter-class '*BankPathsTests'` → 23/23.
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*BackendLaunchIdentityProofE2ETests'` → 20/20 after the full-flow gate (19/19 before it; 28 s).
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*SecretPlacementE2ETests'` → 1/1.
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*NoMintGuardCompositionTests' --filter-class '*DefaultCodeModelCommandTests' --filter-class '*BankPresenceGuardTests'` → 15/15.
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*NoBankE2ETests' --filter-class '*ProxyLaunchE2ETests'` → 4/4 (NoBank 71 s).
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*TokenPathMigrationE2ETests'` → 1/1.
- `dotnet test --project tests/AiRaccoon.Tests --filter-class '*McpServerLaunchArgsE2ETests' --filter-class '*ProxySpawnedBackendE2ETests' --filter-class '*ProxyTokenRefusedE2ETests'` → 7/7 after the reshape.
- E2E category sweep, once, at `00b64297`: `dotnet test --project tests/AiRaccoon.Tests --filter "Category=E2E"` →
  total 75, succeeded 74, skipped 1 (the pre-existing
  `ModelMigrationCrashRecoveryE2ETests.AnInterruptedMigration_LeavesTheBankHalfMigratedForever_WhenTheRelayIsRemoved`),
  failed 0, 5 m 23 s. No process from this worktree was left running.

No `[LoggerMessage]` ids added. VERSION, `~/.ai-raccoon` and port 7721 untouched.

## P5 fixes

Branch `task/air-identity-proof-p5-fixes`. The owner approved F1, F2, F3, F5 and F7. Each fix below
had a failing behaviour test first. The red was captured at the unfixed code, then the fix was
applied and the same classes run green. Every command was
`dotnet test --project tests/AiRaccoon.Tests --filter-class '*X'`.

| fix | commit | red (unfixed code) | green |
|---|---|---|---|
| F1 — tighten an owned 0755 state directory, refuse writable or foreign-owned ones | `ce62f460` | `McpTokenFileStateDirTests`, `IdentityKeyFileTests`, `TokenPathMigrationE2ETests`: 4 failed / 17 passed. `token (null) should not be null or white space`; `key should not be null`; both E2E runs: `serve … never answered (exited: True)`, with stderr `NodeRunner[607] cannot read or create the MCP token` | those three classes plus `LoggerMessageEventIdTests`, `TokenFileLegacyMigrationTests` and `McpTokenFileTests`: 50/50 |
| F7 — the exit-22 remedy is runnable and keeps the scope | `46415c28` | `BankPresenceGuardTests`: 2 failed (User, Project). `Errors should be empty but had 2 items: "Unrecognized command or argument '--data-root'."`: the old remedy put `--data-root` after the verb, which the parser rejects | `BankPresenceGuardTests`, `NoMintGuardCompositionTests`, `HowToExitTableTests`: 12/12 |
| F2 — `serve --restart` proves before it identifies | `6d03caad` | `ServeRestartTests` + `BackendLaunchIdentityProofE2ETests`: 5 failed / 26 passed. Unit gate: `fake.ObservabilityRequests should be 0 but was 1`. Matrix restart cells for squatter, relay, replay and planted key: `an unproven listener received GET /observability (0 body bytes)` | those classes plus `ServeRestartE2ETests`: 33/33 |
| F3 — stop a fallback child that fails its own proof | `305a4c75` | `ProxyPrivateBackendLifetimeTests.AFallbackThatFailsItsOwnProof_IsStoppedAtOnce`: `the fallback child (pid 25992) failed its proof but is still running` | that class plus `BackendSessionsTests`, `BackendLauncherTests` and `CliSettingsBackendTests`: 42/42 |
| F5 — SDK stdio client residual | `7f14c265` | docs only (ADR-0106 residual 13) | — |

After the last fix, the consuming E2E classes ran once more at the tip: `BackendLaunchIdentityProofE2ETests`,
`TokenPathMigrationE2ETests`, `SecretPlacementE2ETests` and `ProxySpawnedBackendE2ETests` → 27/27,
1 m 38 s. No process from this worktree was left running.

What changed, per fix:

- **F1.** `OwnerOnlyFile.EnsureDirectory` handles an existing state directory in three ways:
  - Owner-only: nothing changes.
  - Group/world-*writable*: refused, as before.
  - Readable or executable by others but not writable: it runs `chmod` back to owner-only and returns
    true. A `chmod` that fails because another user owns the directory becomes a refusal naming the
    owner as the remedy. That is how the foreign-owner case is enforced; the test uses root-owned
    `/usr/share` and skips when run as root.

  `serve` logs the tightening once, as the new EventId **692** (`OwnerOnlyFile.Log.StateDirectoryTightened`).
  The registry now counts 192. Key and token *files* that others can read are still refused.
  The verifier's read-only `Read()` checks are unchanged: a shared directory is not provable until a
  `serve` on that root has tightened it. The ADR-0106 D1 bullet and the upgrade note state the rule.
  - Mutation check: with the writable refusal removed and the foreign-owner `chmod` failure swallowed,
    `McpTokenFileStateDirTests` went 3 red. Restoring the code turned them green again.
  - Side effect on M10 from the table above: reverting owner-only creation no longer turns
    `ProjectScopeRun_…` red, because `serve` now tightens that directory. The creation mode is still
    gated directly by `SqliteConnectionFactoryTests` and `QuietLoggingTests`.
- **F7.** The remedy now reads `ai-raccoon --data-root <root> [--install-scope project] serve`, and the
  test parses it with `CliArgs.TryParse`. Beyond the missing scope in the report above, the old
  spelling `ai-raccoon serve --data-root <root>` did not parse at all: `--data-root` must precede the
  verb, and the live binary exited 15 with `Unrecognized command or argument '--data-root'`. The how-to
  and reference docs quote the new line.
- **F2.** `ServerRestart.CycleAsync` now proves first, then identifies, then reads the token. An
  unproven listener gets `RestartOutcome.Unproven`, and the refusal and log 657 no longer say it
  "identifies as ai-raccoon". `Foreign` remains only for a proven listener whose `/observability` names
  something else; `AListenerThatWillNotIdentify_…` was re-fixtured onto a proven fake. The threat
  matrix's restart exception for `GET /observability` is gone, and `FakeRaccoon` now counts
  `/observability` hits after its own warm-up.
- **F3.** `BackendResult` gained `Child`, the private child's `Process`, which only `StartPrivateAsync`
  sets. When the child's proof fails, `FallbackAsync` kills its process tree and waits up to 5 s for
  it to exit. The settings verbs' fallback shares this path.
