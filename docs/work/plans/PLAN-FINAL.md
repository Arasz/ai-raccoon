# FROZEN PLAN — air-backend-launch-identity-proof

Status: frozen after a 3-plan MoE panel (d-1546 architect, d-1547 dotnet, d-1548 test) and a 3-reviewer
MoE panel (d-1553 code-reviewer → REVISE, d-1554 architect, d-1556 dotnet feasibility → all buildable).
This document is the authoritative overlay; the three plan files and three reviews are appendices
(`plan-*.md`, `review-*.md`) and are NOT to be followed where they differ from here.

Task: revert the backend-launch default to **attach-or-start** (launch an instance only if none is
running) and replace ADR-0105's never-attach defense with **cryptographic proof of backend identity**
(per-root ECDSA key; a listener proves possession before any secret reaches it). Folded in: p34 —
**F49** (token + key live in the bank state directory, never the data-root top level) and **F39**
(a mistyped `--data-root` must not mint a bank on auto-launch).

---

## 1. Frozen decisions

### D1 — Trust anchor: per-root ECDSA P-256 key file
- `identity-key` in the bank **state directory** (User scope: `<dataRoot>`; Project scope:
  `<dataRoot>/.ai-raccoon`). 0600, same `UnixCreateMode` precedent as the token
  (`McpTokenFile.cs:170`). Windows ACL inheritance is a documented residual (POSIX 0600/0700 only).
- **net10.0 has no Ed25519 in the BCL** (verified against the ref pack: only the PQ composite
  `MLDsa*WithEd25519` names exist). `ECDsa.Create(ECCurve.NamedCurves.nistP256)` +
  `SignData`/`VerifyData` is in-box, zero new packages. **ECDSA is greenfield** — no existing
  ECDsa usage anywhere in the repo.
- Store as PKCS#8 PEM (`ImportFromPem`/`ExportPkcs8PrivateKey`); `keyId` =
  base64url(SHA-256(SPKI DER)).
- **Only `serve` mints** (the `McpTokenFile.EnsureAsync` precedent at `NodeRunner.cs:42,53`).
  The **verifier is read-only and never mints** — a client with no key file is `NotProven` and
  creates zero files (critical for F39: no verifier-side mint in an empty root).
- Signer loaded once (cached `ECDsa` instance), never re-imported per request.
- **Mint/heal is OS-locked** (`FileShare.None` lock file) — the in-process `SemaphoreSlim`
  (`McpTokenFile.cs:34`) is not cross-process safe; never delete a file younger than the heal window.
- **Trust-anchor validation, fail closed**: state dir created 0700; an existing state dir or
  `identity-key`/`mcp-token` that is not owned by the current uid, or is group/world writable or
  readable, refuses (no attach). Gate: planted permissive dir → refuse.
- **Rotation** = manual stop → delete/replace `identity-key` → start again (a dedicated rotate verb
  is future work). The architect plan's "delete key + `serve --restart`" is impossible (the restart
  cannot prove once the key is gone) and is rejected.

### D2 — Wire protocol v1 (frozen; goes verbatim into ADR-0106)
```
POST /identity/prove            Content-Type: application/json
request   { "v":1, "nonce":"<32-byte CSPRNG, base64url>", "rootFp":"<base64url sha256>",
            "keyId":"<base64url sha256(SPKI)>" }
response  200 { "v":1, "keyId":"…", "signature":"<base64url IEEE-P1363 fixed 64-byte r||s>" }
          400 { "error":"malformed" | "root-mismatch" | "no-key" }        any other status = NotProven
```
- The listener resolves its OWN state dir and its OWN bound port; if its `rootFp` ≠ the challenge's
  `rootFp` it refuses (**400 root-mismatch, no signature, nothing echoed**).
- The signed transcript is
  `"ai-raccoon/identity/v1\n" + nonce + "\n" + keyId + "\n" + rootFp + "\n" + port`,
  SHA-256, **IEEE-P1363 fixed-field concatenation** pinned at both sign and verify
  (`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`).
- The verifier reconstructs the transcript with **the port it actually dialled**; a relayed
  signature from a backend on another port fails verification. Replay dies on the fresh nonce
  (new nonce per request **attempt**, including retries).
- **Nothing sensitive is echoed** — `rootFp` is enforced by listen-side refusal, never returned
  (answers d-1554 F12 disclosure). **`clientPort` is dropped** — it cannot be honest with a pooled
  `HttpClient`; not claimed anywhere.
- Endpoint placement: register with `MapPost` in `ConfigureMcpEndpoints` (`McpServerSetup.cs:74-107`,
  same style as `MapObservability`/`MapShutdown`) and add the path to `McpTokenGate.OpenPaths`
  (`McpTokenGate.cs:31`). The gate stays default-closed; a forgotten allowlist entry costs a 401.
- Bounded: small in-flight cap on the endpoint (cheap DoS bound); fixed-size responses.
- **Why the dotnet plan's nonce-only transcript is rejected (F1):** signing only the nonce proves
  *possession by proxy* — a squatter can relay the challenge to any live same-root backend
  (the victim's `serve`, or an ephemeral fallback child, discoverable via `/observability`) and
  hand the token over on the returned signature. `serverPort` + `rootFp` binding closes it.

### D3 — Token: kept, relocated, migrated
- The proof authenticates the SERVER to the client; the token authenticates the CLIENT to the
  server (`McpTokenGate` default-closed; `/mcp`, `/settings`, `/shutdown` all ride it). The proof
  does NOT make the token redundant; deriving one from the other collapses the two directions.
  (Unknown #6 — settled.)
- Token moves from `<dataRoot>/mcp-token` (`McpTokenFile.cs:41`) to the bank state directory.
  `McpTokenFile(string dataRoot)` becomes scope-aware; the 4 production call sites
  (`NodeRunner.cs:42`, `BackendSessions.cs:45`, `CliSettingsBackend.cs:72`, `McpServerSetup.cs:83`)
  route through `SqliteConnectionFactory.BankPathFor(options)` + `Path.GetDirectoryName`
  (public, `:176`; idiom at `MaintenanceCommands.cs:19`, `QuietLogging.cs:30` — `BankDirectoryFor`
  is private and NOT the seam).
- **Migration**: read-fallback to the legacy top-level path ONLY when that file is owned by the
  current uid and not group/world readable/writable; adopt + write into the state dir; delete the
  legacy file only after a successful state-dir write; state-dir wins when both exist; **never mint
  at the legacy path**. Planted legacy token → fail closed. User-scope path is unchanged; only
  Project-scope expectations move.

### D4 — F39 no-mint scope (owner-flagged; adopted default)
- Guard the **client auto-launch paths only**: `BackendSessions.AcquireBackend`
  (`BackendSessions.cs:174-185`) and `CliSettingsBackend.AcquireAsync` (`CliSettingsBackend.cs:43-57`)
  — bank file missing (`File.Exists(BankPathFor(options))`) → `BankMissingException` → mapped to
  `ExitCode.NoBank` (22, `ExitCode.cs:66`) at `ProxyRunner.cs:29-33` and `ConfigCommands.cs:147-160`.
- **Exempt by PATH IDENTITY, not flag explicitness**: the resolved data root equal to the default
  root (`DefaultOptions.DataRoot`) keeps today's bootstrap; `serve`/`serve --restart`,
  `encryption`, `doctor` are exempt unconditionally.
- `ExitCode.NoBank`'s doc phrase ("no bank file exists at the resolved path") survives; the
  how-to row notes the new auto-launch use (`HowToExitTableTests.cs:30`).
- Unrecognized CLI args exit **9** (`FailedToParseCliArgs`); `InvalidArgument`=15 is semantic
  validation, not parsing.

### D5 — Attach-or-start + fallback policy
- `--attach` is **removed entirely** (both spellings: `CliCommandTree.cs:59` root and `:65` serve;
  plus `RootCliOptions.cs:28`, `CliArgs.cs:196`, `CliOptionsExtensions.cs:33`, `ServeCommands.cs:29/:64/:85`).
  Passing it → unrecognized argument → exit 9.
- Default: probe the configured port; a **proven** ai-raccoon → attach to it (no second backend);
  nothing listening → launch one on the configured port; **unproven listener / proof failure /
  probe `Unanswered`** → clients fall back to a private ephemeral backend with the N1 disclosure
  naming the remedy (stop the listener). `serve` on an unproven holder REFUSES exit 3 after the
  proof attempt (message names the manual stop, never the token). Fallback idle bound: 5 minutes
  for one-shot settings fallback; proxy-spawned children keep K1a stop-with-proxy.
- **Prove before EVERY token-bearing request**: acquire, `ServerRestart.RequestShutdownAsync`,
  and the proxy dispose path `BackendSessions.StopPrivateBackendsAsync` (`:112,:124-141`). If the
  listener cannot prove at stop time, send nothing and report not-stopped (F3).
- **Invariant wording (honest form)**: to any listener that has not proven identity — **zero secret
  bytes; requests are limited to the existing `POST /mcp "x"` probe and the bounded nonce
  challenge**. "Zero requests" was false (the probe already POSTs to whatever holds the port,
  `ServerProbe.cs:52-56`, with no token — which is why "zero secret bytes" is already true today).
- The proof **shrinks** the acquire-time TOCTOU window; it does not close it (the token rides later
  connections). Channel binding / unix sockets stay future work. No plan may claim closure.

### D6 — ADR strategy
- **Supersede, don't amend**: new **ADR-0106** (next free number; verified against `docs/adr/`)
  carrying the decision, wire spec, threat model, upgrade note, overruled-precedent rationale and
  residuals. ADR-0105 gets `Status: Superseded — by ADR-0106` plus the minimal "mutual proof"
  overruled annotation (precedent: 0013→0016, 0002→0021; the index forbids post-acceptance edits).
  Both README index rows updated. `docs/SECURITY.md` does not exist — the surface is the ROOT
  `SECURITY.md`.

### D7 — F49 surface
- Key + token + bank + log live together under the state directory; a state-dir backup carries the
  credential (an explicit goal); no unignored 0600 secret at a repo's data-root top level.
- Gitignore claim: the documented layout must not leave the secret one `git add -A` from a commit.

### D8 — Gate honesty corrections (from the reviews)
- Replace "zero requests to a squatter" with the D5 wording everywhere; the old phrase is false.
- **Delete** `CliSettingsTokenExposureTests.cs:44` (its premise — "the F70 handover is ruled
  acceptable" — is exactly what the ruling reverses). Keep `BackendSessionsTokenExposureTests` as
  the re-shaped F70 gate.
- Re-fixture `CliArgsTests.Parse_RootAttachBeforeABrokenVerb_KeepsTheVerbPath` (`:794`) onto
  `--quiet` — it is the ONLY gate for the `ContainsVerb` arity fix; it must not be retired with
  `--attach`.
- Proof-capable fixtures: `FakeRaccoon` gains a `/proof` handler + injected `ECDsa` signer
  (modest); `Squatter` stays capture-only (raw TCP, no signing). Backends in `ProxyLaunchE2ETests`
  (ungated, `:56-59`) and `QuietLoggingTests` (`:190,:214`) must share the root + hold the identity
  key, or they fail for the wrong reason.
- One EventId block for new lines: **690-691** (inside the owning blocks; `LoggerMessageEventIdTests`
  `:16,:29,:66,:80`). Today's count is **189** (`logging-event-ids.md:12`); update the prose count
  AND rows 650-657/687 message text (`--attach` references). Prefer stderr for refusals where the
  NodeRunner pattern already does.
- Cross-root attach: `serve` on another root's server refuses exit 3 — re-shape
  `NodeRunnerTests.cs:119`, `ObservabilityRunnerTests.cs:188`, and the UX-F10 warning text.

---

## 2. Packages (order: P1 → (P2 ∥ P3) → P4 → P5)

Amendments applied are listed per package. `A#` = code-reviewer, `F#` = architect review.

### P1 — Bank state dir + token relocation + identity primitive (F49)
Files: new `src/AiRaccoon.Infrastructure/Sqlite/BankPaths.cs` (public scope-aware resolution; existing
private helpers delegate, no behavior change); `SqliteConnectionFactory.cs`; `McpTokenFile.cs`
(scope-aware ctor + legacy migration + 0600/ownership checks + OS lock); new
`Hosting/Common/IdentityKeyFile.cs` (PKCS#8 PEM, 0600, keyId, OS-locked mint/heal, ownership checks);
new `Hosting/Common/IdentityProof.cs` (transcript build, P1363 sign/verify, `NotProven` reasons);
call sites `NodeRunner.cs:42`, `BackendSessions.cs:45`, `CliSettingsBackend.cs:72`,
`McpServerSetup.cs:83`; test-helper path updates (`RaccoonBackendCleanup.cs:17`, `McpTokenFileTests`,
E2E literal paths).
Amendments: A2 (dir/ownership fail-closed — shared with P3), A3 (migration semantics), A13 (merge
main first, re-verify anchors), F2, F6, F7, F8 (rootFp lives in the primitive), D1/D2.
Gates (watched red at today's code where marked RED):
- RED `Token_ResolvesInsideTheBankStateDirectory_NotTheDataRootTopLevel` (Project scope)
- RED `IdentityKey_IsMintedOnlyByServe_AndOnlyInTheStateDir` + positive control (serve mints)
- RED `Verifier_WithNoKeyFile_IsNotProven_AndCreatesZeroFiles` (F39-adjacent, F12)
- RED `CrossProcess_MintAndHeal_ConvergeOnOneKey` (two processes; F7)
- RED `Mint_IsSignedWithAnOwnerOnlyKey_AndFailsClosed_OnAPermissivePlantedDir` (F2)
- RED `LegacyToken_IsMigrated_Validated_DeletedAfterSuccess_AndNeverReMinted` (A3, F6)
- Controls: round-trip sign/verify under the same key; User-scope path unchanged.
Note: `NoHandRolledCryptoTests` — if any HKDF derivation appears it must register under
`ContentAddressingSites`; prefer no derivation at all.

### P2 — Proof channel (server endpoint + client verifier + fixtures)
Files: new `Setup/Identity/IdentityProofEndpoint.cs`; new `Hosting/Proxy/IdentityProver.cs` (or
`Hosting/Common`); `Setup/McpServerSetup.cs` (map + `serve`-side key ensure); `McpTokenGate.cs`
(`OpenPaths` += the proof path); `tests/TestHelpers/FakeRaccoon.cs` (`/proof` + injected signer);
`tests/TestHelpers/Squatter.cs` (extend capture only if needed).
Amendments: A1 (accept ONLY D2's bound transcript; reject the unbound one), F1, F5 (Unanswered →
challenge then fallback within budget — shared with P4), F12 (no echoed rootFp; signer cached;
verifier never mints; burst bound), F13 (the frozen wire spec).
Gates: RED `Prove_WithFreshNonce_ReturnsAP1363SignatureVerifyingUnderTheStateDirKey`;
RED `Prove_RefusesRootMismatch_WithoutEchoingTheRootFingerprint`;
RED `Relay_SameRootBackendOnAnotherPort_IsNotProven` (F1's missing gate);
RED `Replay_OfACapturedResponse_IsNotProven`; RED `Endpoint_ReachableWithoutTheToken_WhileMcpAndShutdownStayGated`;
RED `Verifier_RejectsMalformed_Oversized_AndSlowResponses_WithinBudget`; positive controls:
`FakeRaccoon` round-trip + the P5 real-binary cross-check (the real control).

### P3 — F39 no-mint guard (client auto-launch paths)
Files: new `BankPresenceGuard`/`BankMissingException` (wherever the composition seams live);
`BackendSessions.cs`; `CliSettingsBackend.cs`; `ProxyRunner.cs:29-33`; `ConfigCommands.cs:147-160`;
`ExitCode.cs` doc; Quick-Start/README docs; fixture seeding (`CliSettingsSharedBackendTests.cs:29-37`,
`CliSettingsBackendTests`, `BackendSessionsTests` must seed banks or they fail for the wrong reason).
Amendments: A2, F39 scope (D4), how-to row phrase survives.
Gates: RED `SettingsVerb_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing`;
RED `ProxyAcquire_AgainstAnEmptyExplicitRoot_Exits22_AndCreatesNothing`;
RED `NoBank_MapsTo22_InBothCompositionRoots`; controls: default-root bootstrap unchanged,
`serve`/`encryption` creation preserved, plus `CliCommandsDoNotOpenTheBankTests` stays green.
Note: `BackendSessions.cs`/`CliSettingsBackend.cs` are touched by P1's call-site change first —
P3 runs after P1 merges (no concurrent writers).

### P4 — Attach-or-start revert + proof-gated handover + serve/restart + CLI surface
Files: `BackendLauncher.cs`/`IBackendLauncher.cs` (spawn → fallback-only; `StartPrivateAsync`
machinery kept), `BackendSessions.cs` (attach-or-start + proof-gated acquire + proof-gated dispose
stop), `CliSettingsBackend.cs`, `ServerConfig.cs`/`BackendLaunchArguments.cs`, `NodeRunner.cs`
(serve attach-report + refusal message), `ServerRestart.cs`/`IServerRestart.cs`/`RestartOutcome.cs`
(proven-cycle only), `CliCommandTree.cs`/`CliArgs.cs`/`RootCliOptions.cs`/`CliOptionsExtensions.cs`/
`ServeCommands.cs` (`--attach` removal).
Amendments: A4 (fallback policy, 5-min one-shot idle, K1a stop, serve refusal exit 3), A8 (arity
gate re-fixtured), A10 (cross-root semantics + warning text), A11 (proof-capable fixtures), A12
(EventIds 690-691), F3 (prove before every token-bearing request, incl. dispose stop), F4 (shrink
wording), F5 (Unanswered), F9 (fallback idle + sprawl bound), F10 (rotation doc).
Gates: RED `Acquire_WithAProvenBackend_Attaches_AndSpawnsNothing`;
RED `Acquire_WithNoListener_StartsOnTheConfiguredPort`; RED `Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes`;
RED `DisposeStop_WithARacerOnTheDeadChildsPort_SendsNoSecretBytes` (F3);
RED `ProveIsRequired_BeforeEveryTokenBearingRequest` (ordering);
RED `Restart_Bare_AgainstAProvenServer_CyclesIt`; RED `Restart_Bare_AgainstAnUnprovenHolder_RefusesExit3_SendsNoToken`;
RED `Serve_OnAProvenListener_ReportsAttachAndExitsZero` (exit 3 → 0 change);
RED `AttachOption_IsAbsentFromBothRoots` + RED `ServeAttach_IsAnUnrecognizedArgument_Exit9`;
PC `Parse_RootQuietBeforeABrokenVerb_KeepsTheVerbPath` (re-fixtured arity gate);
PC K1a `ProxyPrivateBackendLifetimeTests` stays green; hanging-probe gate (F5) within budget.

### P5 — Integration + record + docs (LAST)
Files: new E2E (`BackendLaunchIdentityProofE2ETests`, `SecretPlacementE2ETests`, `NoBankE2ETests`,
`TokenPathMigrationE2ETests`); re-shaped existing E2E path assertions; `docs/adr/0106-*.md` (new) +
0105 status edit + README index; root `SECURITY.md`; README "What's new" + Quick-Start;
`agent-memory-server.md`; how-to; `docs/reference/logging-event-ids.md` (count + rows).
Amendments: A7 (invariant wording everywhere + every never-attach gate dispositioned; omissions
4/5), A9 (docs/release surfaces; **VERSION untouched**), A11, F11 (ADR index), F13.
Gates: RED `ThreatMatrix_FiveAttackers_ThreePaths_ZeroSecretBytesEverywhere` (attacker cells:
squatter, relay-same-root-other-port, replay, planted-key, cross-root-copy; honest cell green);
RED `FullFlow_ProvenAttach_HandoverWorksAcrossProxySettingsAndRestart`;
RED `ProjectScopeRun_LeavesNoSecretAtTheDataRootTopLevel` (+ state-dir backup carries both files);
RED `EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBankAndLeavesTheDirectoryEmpty`;
RED `Serve_WithALegacyTokenFile_MigratesIt_AndTheProxyStillAttaches`.
Full gate inventory: plan-test's d.1 table as amended by A7/omissions 4-5 (each gate individually
watched red before its fix; positive control beside it; the P5 real-binary cross-check is the
control for the crypto round trip, not the FakeRaccoon echo).

---

## 3. Risks / residuals ADR-0106 must state
1. Same-uid attackers and root are out of scope (they read the key directly).
2. Post-proof TOCTOU: the proof binds one connection; the token rides later ones; channel binding /
   unix sockets remain future work.
3. DoS: port-squat forces fallback for every client; hanging listeners too; fallback sprawl costs a
   model load each; the endpoint is rate-bounded but unauthenticated.
4. Bearer token after handover remains full access (env propagation, `/proc`, crash dumps, transcripts).
5. Copied/restored state dir: cross-root confusion is refused via rootFp (restores are supported by design).
6. Legacy-token migration window; planted-file defense; no supported mixed-version operation
   (an old binary re-mints a top-level token after migration → manual stop required).
7. Permissions: POSIX 0600/0700 only; Windows ACL inheritance; plaintext key even with an encrypted
   bank; state-dir backups carry the trust anchor.
8. F39 default-root carve-out is a deliberate product decision (path identity).
9. Metadata: `/observability` reveals pid/version; rootFp is never echoed.
10. Missing/corrupt key file = "cannot attach", only "spawn private".
11. Invariant's precise form (D5 wording) — not "zero requests".
12. The overruled precedent: ADR-0106 must answer ADR-0105's "unauthenticated cryptographic oracle"
    objection (bounded transcript, fresh nonce, domain separation, rate bound) rather than drop it.

## 4. Clause → package → AC

| clause | package | acceptance criterion |
|---|---|---|
| revert to attach-or-start (proxy/settings) | P4 | proven backend attached, nothing spawned; none running → launched on the configured port |
| revert (serve) | P4 | proven port holder → attach-report, exit 0 (was exit 3) |
| revert (bare `serve --restart`) | P4 | bare restart cycles a proven server (was AttachRequired) |
| proof of identity via private key | P1, P2 | key minted only by serve in the state dir; `/identity/prove` returns a P1363 signature verifying under it; verifier never mints |
| protocol freedom (custom non-MCP channel) | P2 | allowlisted `POST /identity/prove` on the existing loopback listener; `/mcp`, `/settings`, `/shutdown` stay gated |
| security invariant | P2, P4, P5 | zero secret bytes to any unproven listener across acquire/restart/dispose + the E2E threat matrix |
| F49 | P1, P5 | token + key in the state dir; nothing 0600 at a data-root top level; backup carries both |
| F39 | P3, P5 | empty non-default root on auto-launch → exit 22, zero files; default root + creation verbs preserved |
| release surface | P5 | README What's new (BREAKING: `--attach` removed, F39 exit 22) + ADR-0106 link; VERSION untouched |

## 5. Execution rules for every lane
- Work in the task worktree (`.ai-badger/worktrees/air-backend-launch-identity-proof`, branch
  `task/air-backend-launch-identity-proof`); P2/P3 may use their own worktrees branched from the
  task tip, merged back sequentially after review.
- TDD: watch every RED gate at the unfixed code first (`dotnet test --project tests/AiRaccoon.Tests
  --filter-class '*X'`, never `--nologo`), record the output, then fix. Positive control beside
  every hostile gate. Mutation checks for RED-B (contract-doesn't-exist) gates where stated.
- One commit per coherent step; push after EVERY commit; never push main; never force-push; no
  `git stash`; never touch `~/.ai-raccoon` or port 7721; never edit VERSION.
- Every refusal message names its remedy; every new `[LoggerMessage]` id goes in the 690-691 block
  with the registry re-measured in the same commit.
- ADR/docs wording: the invariant is the D5 wording; the TOCTOU claim is "shrinks", never "closes".