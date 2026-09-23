All sources are read and every load-bearing claim below is grounded in the tree or in a measurement I ran this session. The plan is complete — no files edited, no builds run.

---

# Implementation plan — attach-or-start + cryptographic proof of backend identity

**Task:** `air-backend-launch-identity-proof` · ruling: `docs/work/2026-09-22-backend-launch-identity-proof-research.md` · overrules ADR-0105's "Alternatives rejected — Mutual proof" (which evaluated only name-based/handshake proofs, per `docs/adr/0105-private-spawn-is-the-launch-default.md`, "Alternatives rejected") · folds in F49 (secret placement) and F39 (no silent bank mint).

## 0. Context Map (edit sequence before any edit happens)

| Role | Files | Owner package |
|---|---|---|
| Primary (state/secrets) | `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs` (`BankDirectoryFor`/`BankPathFor`, :167-176), `src/AiRaccoon/Hosting/Common/McpTokenFile.cs` (:41-44 path, :154-176 mint) | P1 |
| Primary (guard) | new `src/AiRaccoon/Setup/BankState.cs`; `src/AiRaccoon/Settings/CliSettingsBackend.cs` (:48-84), `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs` (:178-190), `src/AiRaccoon/Hosting/Node/NodeRunner.cs` (:74-93) | P1/P3/P4 |
| Primary (proof) | new `src/AiRaccoon/Hosting/Common/{IdentityKeyFile,ProofBinding,BackendIdentityVerifier}.cs`, new `src/AiRaccoon/Hosting/Node/IdentityProofEndpoint.cs`, `src/AiRaccoon/Setup/McpServerSetup.cs` (:83-85 gate registration, `OpenPaths` in `McpTokenGate.cs:28-31`) | P2 |
| Primary (acquire revert) | `src/AiRaccoon/Hosting/Proxy/{BackendLauncher,BackendSessions,IBackendLauncher}.cs`, `src/AiRaccoon/Hosting/Common/{ServerConfig,BackendLaunchArguments}.cs` | P3 |
| Primary (serve/restart) | `src/AiRaccoon/Hosting/Node/{ServerRestart,IServerRestart,RestartOutcome,NodeRunner}.cs` | P4 |
| Primary (flag surface) | `src/AiRaccoon/Setup/Cli/{CliCommandTree,CliArgs,RootCliOptions,CliOptionsExtensions}.cs` (:59-67,:138,:645 / :196 / :28 / :33), `src/AiRaccoon/Setup/Cli/Commands/ServeCommands.cs` (:29, :64-73, :85) | P3 |
| Secondary (docs/registry) | `docs/adr/0105-…` + new `docs/adr/0106-…`, `docs/adr/README.md`, `docs/reference/logging-event-ids.md` (re-measure per `LoggerMessageEventIdTests.cs:78`), `docs/reference/agent-memory-server.md`, `docs/how-to/configure-ai-raccoon-server.md`, `README.md` | P5 (final) |
| Tests | new/reworked `tests/AiRaccoon.Tests/...` per §4; reuse `TestHelpers/{Squatter,FakeRaccoon}.cs` | all |
| Patterns to follow | `McpTokenFile`'s exclusive-create + heal mint (`McpTokenFile.cs:50-80,154-176`); `McpTokenGate`'s default-closed `OpenPaths` (`McpTokenGate.cs:28-31`); F53 refusal-lines-with-remedy (`NodeRunner.cs:232-236`); `[LoggerMessage]` owner blocks (`NodeRunner.cs:301-339`) | all |

Edit sequence: **P1 → P2 → P3 → P4 → P5** (test-first in each; see §3 for the one parallel window). Hand-off: `dotnet-engineer` per `.ai-badger/delegation.md` (each dispatch names its model there).

## 1. Trust-architecture panel

### 1.1 Threat model

**Attacker:** a different local user on a shared machine who binds the configured loopback port (machine-global — ADR-0105 "Context") before us, or in the gap between our probe and use. Capabilities: accept TCP on 127.0.0.1, answer anything, replay anything previously seen on the wire. **Not** capable of: reading our `0600`/`0700` state files, running as our uid. Out of scope (documented, not fixed): same-uid malware — it reads `mcp-token` and `identity-key` directly (`McpTokenFile.cs:8-11` precedent: the file permission is the trust boundary), and root.

**Assets:** the bearer token (`X-AiRaccoon-Token`, `McpTokenGate.HeaderName`, `McpTokenGate.cs:15`), the identity private key, request payloads (`memory_write` bodies — F70 measured these leaking).

**Trust anchor:** the `identity-key` file in the bank state directory (§1.3). A listener is "ai-raccoon for this data root" **iff** it produces an ECDSA signature under the public key derived from that file, over a fresh verifier nonce with the response binding below. Everything else (the `/observability` name — `ServerRestart.cs:105-113`, `ServerProbe`'s `jsonrpc` body rule — `ServerProbe.cs:69`) is *discovery*, never identity.

### 1.2 Proof protocol end to end (ruling clause 3: custom, non-MCP channel — explicitly allowed)

Single round trip on the backend's existing loopback listener:

1. **Challenge** — `POST /identity/prove`, `{"v":1,"nonce":"<base64url 32 bytes CSPRNG>"}`. Unauthenticated *by necessity* (it precedes any credential) → added to `McpTokenGate.OpenPaths` (`McpTokenGate.cs:30`), which stays default-closed — a forgetting mistake costs a 401, not a leak. Side-effect-free; the request carries only a public nonce.
2. **Response** — `200 {"keyId","alg":"ECDSA-P256-SHA256","dataRootFp","serverPort","clientPort","signature"}` where `signature = ECDSA-Sign(sk, utf8("ai-raccoon/identity-proof/v1\n" ‖ nonce ‖ "\n" ‖ dataRootFp ‖ "\n" ‖ serverPort ‖ "\n" ‖ clientPort ‖ "\n" ‖ keyId))`. The **listener** computes `dataRootFp = base64url(SHA-256(utf8(canonical bank-state-dir path)))` from its own configuration (`SqliteConnectionFactory.BankDirectoryFor`, :167-172 — encodes the install scope, so user/project confusion cannot pass), `serverPort` from its own bound address, and `clientPort` from `HttpContext.Connection.RemotePort` — **never** from request body or headers. All five bound fields are echoed for the verifier to compare.
3. **Verify** — `BackendIdentityVerifier`: load `identity-key` from our state dir, derive SPKI + `keyId`; checks, all mandatory and all fail-closed: `keyId` equals ours → signature valid under **our** key → `dataRootFp` equals ours → `serverPort` equals the port we dialled → `clientPort` equals this connection's local port (`Socket.Client.LocalEndPoint`) → constant-time comparisons (`CryptographicOperations.FixedTimeEquals`, the `McpTokenGate.Matches` pattern at `McpTokenGate.cs:104`).
4. **Handover** — only now is `McpTokenFile.Read()` (`McpTokenFile.cs:134-142`) called and the token presented. The invariant is ordering-shaped and testable: *no secret byte is composed into a request before step 3 returns proven* — this includes the private/fallback path and the `/shutdown` sends in `BackendSessions.RequestStopAsync` (`BackendSessions.cs:124-141`) and `ServerRestart.RequestShutdownAsync` (`ServerRestart.cs:122-139`).

**Replay defense:** the verifier's 256-bit CSPRNG nonce is single-use per attempt; the attempt window is one HTTP round trip inside the existing acquire budget (`BackendLauncher.DefaultBudget`, `BackendLauncher.cs:28`). No server-side replay window or state exists — a captured response is bound to a nonce the verifier never reissues, and the `clientPort` binding additionally kills cross-connection replay. (Rejected: timestamp windows — clock skew becomes a failure mode and buys nothing the nonce doesn't.)

**Relay/MITM defense:** response binding is the defense. Cross-port relay (squatter forwards our challenge to a genuine server on another port): that server signs *its own* `serverPort` and *itser* peer `clientPort` — mismatch on both. Same-port relay is impossible (the port is held). Real-time TCP-terminating MITM: it cannot re-bind `clientPort` to our connection's ephemeral port. Cross-root relay: `dataRootFp` mismatch. A squatter running a real ai-raccoon against a root it fabricated signs under *its* key ≠ our anchor `identity-key` → `keyId` mismatch.

**Oracle surface:** the endpoint returns signatures on attacker-chosen nonces. Signature oracles do not leak the private key (no decryption/MAC-over-shared-secret involved — this is precisely why the rejected 0105 handshake (token-derived value) was dangerous and this is not). Documented in the ADR.

### 1.3 Key: shape, generation, location, permissions, rotation (F49-constrained)

- **Shape: ECDSA P-256 (asymmetric)** via BCL `System.Security.Cryptography.ECDsa` — **no new NuGet dependency** (owner constraint). Measured this session against the installed ref pack (`~/.dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0-…/ref/net10.0/System.Security.Cryptography.xml`): `ECDsa`, `ECDiffieHellman`, `HMACSHA256`, `RSA` exist; **no Ed25519/Ed448** in the BCL — Ed25519 would require a third-party package and is rejected on the dependency constraint alone.
- **Location (F49 hard constraint):** `identity-key` lives in the **bank state directory** — exactly where `mcp-token` moves to in P1: `<data-root>/.ai-raccoon/` for project scope, `<data-root>/` (= `~/.ai-raccoon/`) for user scope (`SqliteConnectionFactory.BankDirectoryFor`, :167-172). Same file: single PKCS#8 PEM (`ECDsa.ExportPkcs8PrivateKey`); the public half is derived in-memory (`ExportSubjectPublicKeyInfo`) — one `0600` file to protect, and under this threat model any process that could read a separate public file can read the private one anyway.
- **Permissions:** file `0600` on POSIX (the `McpTokenFile.TryMintAsync` `UnixCreateMode` pattern, `McpTokenFile.cs:163-167`), state directory created `0700` where the platform supports it (Windows inherits the ACL — same accepted non-goal as `McpTokenFile.cs:159-160`).
- **Generation/mint:** by `serve` before it binds — the exact `McpTokenFile.EnsureAsync` lifecycle (exclusive `CreateNew` + debris heal + in-process gate, `McpTokenFile.cs:50-80`), so concurrent starters converge on one key. Clients are read-only.
- **Rotation:** delete `identity-key`, cycle with `serve --restart`; verifiers re-read the file per attempt so nothing caches a stale key. Corrupt/debris key heals by re-mint (mirroring `TryDeleteDebris`, `McpTokenFile.cs:107-120`) — an attacker who can corrupt it can overwrite it, so heal loses nothing. No rotation command in scope; documented in the ADR. `keyId = base64url(SHA-256(SPKI)[..8])` makes rotation observable.

### 1.4 Trust-flow state machine (proxy / settings acquire; serve & restart variants noted)

Precondition on every auto-launch path (**F39 no-mint rule**): resolve the bank state dir → if `--data-root` was passed explicitly (`OptionResult.Implicit == false` — the exact seam `ServeCommands.ResolveAttach` already uses, `ServeCommands.cs:66-73`) **and** `File.Exists(BankPathFor(options))` is false → **refuse `ExitCode.NoBank` (22)** (`ExitCode.cs:64-66`), naming the path and the remedy (F53): *"no bank at <path> — run `ai-raccoon serve --data-root <path>` once to create it, or fix `--data-root`"*. Nothing is minted, nothing is spawned. (Explicit `serve` and the `encryption` bootstrap remain the only bank-creating commands — `encryption` already mints via `OpenBankAsync`, `EncryptionCommands.cs:122-128`.)

| State | Transition / action | Next |
|---|---|---|
| `Resolved` (state dir + anchor known) | probe the configured port (`ServerProbe.ProbeAsync`) | `Free` or `ListenerPresent` |
| `Free` | F39 guard ✓ → spawn `serve --port N` (attach-or-start: "launch only if none running") → wait for its URL line | `Challenging` (our own child) |
| `ListenerPresent` | `POST /identity/prove` with fresh nonce | `Proven` or `Unproven` |
| `Proven` (all six checks pass) | read token (`McpTokenFile.Read`) → open session / send `/shutdown` | `Trusted` |
| `Unproven` (404 = legacy, non-200, bad sig, any binding mismatch, malicious prover) | **acquire paths:** fall back to private spawn `serve --port 0` (F70 machinery: URL only from the child's stdout — `BackendLauncher.StartPrivateAsync`, `BackendLauncher.cs:44-95`) + loud stderr warning naming the squatted port and the remedy; the fallback child is itself `Challenging`-gated before its token handover. **serve attach-report:** refuse `PortInUse` (3), never print a usable URL for an unproven listener (an operator pasting it into `.mcp.json` would hand the token over out-of-band). **`serve --restart`:** refuse 3, nothing read, nothing sent, remedy = the manual stop already named at `NodeRunner.cs:251` | `FallbackTrusted` or `Refused` |
| `Trusted`/`FallbackTrusted` | proxy stops only what it spawned on shutdown (rule kept from ADR-0105 K1a: `BackendSessions.StopPrivateBackendsAsync`, :104-121 — and now itself proof-gated) | — |

There is **no trust-without-proof escape anywhere** — the security invariant ("the token and any key material must never reach a listener that has not proven identity") is absolute in the ruling, so no `--attach`/`--trust-port`-style bypass survives. A pre-proof server is cycled by stopping it manually first (named in every refusal, F53).

### 1.5 ADR revision shape

**Supersede, don't amend.** ADR-0105's title and Decision statement ("The default launch starts its own backend; attaching is opt-in") are being reversed at their root; folding this in as a third same-day amendment (the K1a/N1 precedent, which were riders) would leave a record whose title contradicts its body. Concretely: new **ADR-0106 "Proof of backend identity gates every token handover; attach-or-start is the launch default"** carrying the full Decision + the six design rulings below; **ADR-0105 minimally edited**: `Status: Accepted` → `Status: Superseded by ADR-0106 (2026-09-22 later owner ruling)`, plus an **"Overruled"** annotation on the "Alternatives rejected — Mutual proof" entry recording *why* it no longer binds: it evaluated token-derived handshakes and self-asserted-name endpoints only; a private-key signature over a verifier nonce on a custom channel was never in its option set. ADR-0105's surviving riders (N1 disclosure, CliArgs arity fix, concurrent-access findings, TOCTOU residual) are re-affirmed by reference in 0106 — 0106 also records that the proof closes the private-path TOCTOU residual ADR-0105 left standing ("the child dies between that check and the caller's first request"). `docs/adr/README.md` index updated. (Rejected: in-place amendment — self-contradictory record; rewriting 0105 — destroys the measured F70 evidence trail.)

## 2. (a) Design decisions — one recommendation per known unknown

| # | Decision (ONE) | One-line rationale | Rejected alternatives |
|---|---|---|---|
| 1. Key shape | **Asymmetric ECDSA P-256 keypair**, single PKCS#8 file, public half derived in-memory (BCL `System.Security.Cryptography`, zero new packages) | Matches the ruling's literal "private key … prove identity" and keeps the verifier's proof a pure signature — no shared-secret oracle; measured: Ed25519 is absent from the .NET BCL ref pack so it would need a NuGet dep the constraint forbids | Symmetric HMAC secret (contradicts "private key", turns the unauthenticated endpoint into a PRF keyed on the guard secret); Ed25519 (needs a third-party package); RSA (needlessly large) |
| 2. Key scope/location | **Per bank state directory** (`<data-root>/.ai-raccoon/` project scope, `<data-root>/` user scope), `0600` file in a `0700` dir, same lifecycle as `mcp-token` | F49 fixes location as a hard constraint and the per-root scope isolates banks from each other and from the machine-global port | Per-installation/user key (one leak compromises every root); today's top-level location (violates F49: unignored secret in repo working trees — REVIEW-ASSEMBLED F49 measurement) |
| 3. Proof wire | **One round trip, `POST /identity/prove`** on the existing loopback listener: verifier-generated 32-byte nonce in; `keyId, alg, dataRootFp, serverPort, clientPort, signature` out; signature over the domain-separated canonical string binding all five; **replay window: none server-side** (stateless; verifier single-use nonce); errors: 404/405 = legacy-or-absent → unproven, 400 = malformed, anything else = unproven (fail-closed) | The binding fields are exactly what kills relay (port pair), replay (nonce) and cross-root confusion (state-dir fingerprint), and a stateless endpoint keeps the server free of session state | Two-round challenge-then-fetch (extra RTT, no added strength); timestamp replay windows (clock skew becomes a failure mode); binding to `serverInfo.name` (rides after the token — ADR-0105 rejection) |
| 4. Failure mode on failed proof | **Fail closed to secrets, fail open to function:** never trust the unproven listener with anything; proxy and settings acquire fall back to the private spawn (F70 machinery) with a loud stderr warning naming the squatted port + remedy; serve attach-report and `serve --restart` refuse exit 3 with the named remedy | A squatter must not be able to DoS the product (refuse-only) nor silently downgrade trust (blind fallback); the fallback reuses proven machinery and keeps zero-secret-byte guarantees | Refuse-only (trivial liveness attack against every user of a shared machine); silent fallback (F70 showed unexplained trust decisions are operator traps) |
| 5. Survival of #643's non-default changes | **Keep** the `CliArgs` bool-arity fix, the N1/F38 disclosure (text updated to the proof-gated stop command), and proxy-stops-what-it-spawned (extends to fallback backends, itself proof-gated); **remove** the `--attach` flag entirely (no replacement — it existed to express trust-without-proof, which the invariant forbids), the plain-`serve` exit-3 refusal (attach-report restored — but only for a **proven** listener), and the `AttachRequired` restart gate; **restore** bare `serve --restart`, proof-gated | The revert clause demands the pre-F70 default back; only the bug fix and the disclosure ruling (N1) were ever separately ruled, and the proof replaces every trust decision `--attach` used to carry | Keeping `--attach` as an escape (violates the invariant's absolute wording); keeping exit-3-on-held-port (that refusal was the never-attach defense itself) |
| 6. Proof vs bearer token | **Proof gates the token handover; the bearer token stays unchanged** (`McpTokenGate`, `ServerSettingsStore`, the hermes python transport at `integrations/hermes/ai-raccoon/client.py` all keep working) | Minimal blast radius: one new gate in front of an existing read, no change to per-request auth or any consumer contract | Deriving the channel token from the key and deleting `mcp-token` (smaller secret count but rewrites every MCP client incl. the python integration and conflates channel setup with request auth) |

**F39 boundary decision (logged assumption, autonomous session):** the NoBank guard fires on auto-launch paths when `--data-root` was given explicitly and holds no bank. The default root is exempted from the guard because the documented Quick Start runs `ai-raccoon model embedding set local` (README.md:60-64) — a settings verb — as the *first* command against a not-yet-existing default bank (F39's own "Smallest fix" targets exactly "about to auto-start … a data root whose bank does not exist"; the ruling sentence is scoped to "a mistyped `--data-root`"). Explicit `serve` and `encryption` remain the named bank creators. If the owner wants the stricter "only `encryption` creates banks" reading, it is one extra gate in P1 and one docs row — flagged here rather than silently chosen.

## 3. (b) Packages

Every package: tests first, **watched-red gate named** (all fail at today's code unless marked control), **positive control named**, refusals name their remedy (F53), new `[LoggerMessage]` ids land in the owning block and `docs/reference/logging-event-ids.md` is re-measured in the same commit (`LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners` / `EveryEventIdInSource_FallsInsideADocumentedBlock`, `LoggerMessageEventIdTests.cs:35,78`). No exit codes added; `ExitCode.NoBank`'s doc broadened from doctor-only (`ExitCode.cs:64-66`) — `ExitCodeTests.NoBank_IsTwentyTwo` (`ExitCodeTests.cs:20-22`) and `HowToExitTableTests` (:30) stay green. One PR per package, squash-merge after origin/main merged in, push after every commit.

### P1 — Bank state directory + NoBank guard (F49 + F39)
**Scope:** secrets move into the bank state directory; silent bank minting dies on auto-launch paths.
**Touched:** `SqliteConnectionFactory.cs` (publish `BankDirectoryFor` as `BankState.DirectoryFor`), `McpTokenFile.cs` (resolve via state dir + legacy move), new `src/AiRaccoon/Setup/BankState.cs`, `CliSettingsBackend.cs`, `BackendSessions.cs` (guard at acquire entry), `NodeRunner.cs` (guard before pre-bind cycle only on the auto path… explicit `serve` is exempt by construction — the guard lives in the *clients*), `InfrastructureOptions`/`CliOptionsExtensions.cs` (carry the "explicit `--data-root`" bit).
**AC:**
1. Project scope: `mcp-token` resolves at `<data-root>/.ai-raccoon/mcp-token`; user scope unchanged at `<data-root>/mcp-token` (hermes client path intact). A legacy top-level token is **moved** (never copied) into the state dir; nothing secret remains at the top level. State dir `0700`, token `0600`.
2. The measured F39 repro (`settings access show --data-root <empty dir>`, REVIEW-ASSEMBLED F39) now exits `NoBank` (22) with remedy and the directory stays **empty** (assert `[]`, exactly inverting the measured `['mcp-token','memory.db','memory.db-shm','memory.db-wal']`).
3. Same for a bare proxy launch and `serve --restart` at an explicit typo root.
4. Positive controls: default-root settings verb still bootstraps (Quick Start intact); explicit `serve --data-root <empty>` still creates the bank; `encryption` bootstrap untouched (`EncryptionCommands.cs:122-128`).
**Named watched-red gate:** `McpTokenFileStateDirTests.Token_ResolvesInsideTheBankStateDirectory_NotTheDataRootTopLevel` — red today (`McpTokenFile.cs:42` puts it at the top level).
**Test classes:** `McpTokenFileStateDirTests`, `TokenFileLegacyMoveTests`, `NoBankAutoLaunchTests`.

### P2 — Identity key + proof protocol core
**Scope:** the key (F49 location), the canonical binding, the server endpoint, the client verifier. Pure addition; touches no acquire logic.
**Touched:** new `Hosting/Common/{IdentityKeyFile,ProofBinding,BackendIdentityVerifier}.cs`, new `Hosting/Node/IdentityProofEndpoint.cs` (mapped beside `ShutdownEndpoint.MapShutdown`, `McpServerSetup.cs:85-101`), `McpTokenGate.OpenPaths` (`McpTokenGate.cs:30`).
**AC:**
1. `serve` mints `identity-key` (ECDSA P-256, PKCS#8) in the state dir, `0600`, exclusive-create + debris-heal + convergence exactly like `McpTokenFile` (`McpTokenFile.cs:50-80`).
2. `POST /identity/prove` is reachable **without** the token, is side-effect-free, and answers only with `keyId/alg/dataRootFp/serverPort/clientPort/signature`; `clientPort` comes from the socket peer, `serverPort` from the bound address — spoofed body/header values change nothing.
3. `BackendIdentityVerifier` accepts iff all six checks pass (keyId, signature under the state-dir key, root fingerprint, server port, client port, constant-time compares); any 404/400/5xx/garbage/wrong-key/replayed/binding-mismatched response = `Unproven`.
4. Positive control: a round trip against a real `serve`d listener returns `Proven`; `/mcp` without the token is still 401 (default-closed gate preserved, `McpTokenGate.cs:28-31`).
**Named watched-red gate:** `IdentityKeyFileTests.EnsureAsync_MintsAnOwnerOnlyEcdsaKey_InTheBankStateDirectory` — red today (no such type).
**Test classes:** `IdentityKeyFileTests`, `ProofBindingTests`, `IdentityProofEndpointTests`, `BackendIdentityVerifierTests`. **No new NuGet** (BCL only — justification in §1.3).

### P3 — Attach-or-start acquire + proof gate (proxy & settings) + flag-surface revert
**Scope:** restore attach-or-start as the acquire default (ruling clause 1), put the proof gate in front of every token read/send on those paths (clause 2 + invariant), implement the fallback policy (unknown 4), remove `--attach` end to end (unknown 5).
**Touched:** `BackendLauncher.cs` (acquire = probe → challenge → attach | spawn), `BackendSessions.cs` (:178-190 branch, :64-67 and :113-115 token reads become post-proof), `IBackendLauncher.cs`, `CliSettingsBackend.cs` (:48-84; disclosure line reworded to `serve --restart --port N`), `ServerConfig.cs` (`Attach` removed, :20-23), `BackendLaunchArguments.cs` (`PrivateServeArguments` becomes the fallback shape), `CliCommandTree.cs:59-67,138,645`, `CliArgs.cs:196`, `RootCliOptions.cs:28`, `CliOptionsExtensions.cs:33`, `ServeCommands.cs:29,64-73,85` (flag excision incl. the `NodeCliOptions.Attach` property and `ResolveAttach`).
**AC:**
1. No listener on the configured port → one backend is started **on the configured port** (pre-F70 shape).
2. A listener that proves → attach, zero spawns, token handed over only after `Proven` (ordering asserted).
3. The F70 squatter scenario in proof-gated form: `squatter.Requests ⊆ {POST /identity/prove}` and **zero secret bytes** (no `X-AiRaccoon-Token` header anywhere in `Squatter.Requests`/`TokenHeaderValues`, `Squatter.cs:44-55`); the run still serves via the private fallback and the warning names the squatted port + remedy.
4. A malicious prover (wrong key) and a replaying prover (captured response) are `Unproven` → same fallback, zero secret bytes.
5. `--attach` is gone from parse, help, and behavior (grep gate); `CliArgsTests.Parse_RootAttachBeforeABrokenVerb_KeepsTheVerbPath` is retired with the flag and the arity fix's *other* tests stay green.
6. Positive controls: proven-attach read/write round trip through the token (`CliSettingsTokenExposureTests` positive-control lineage); fallback backend serves a full settings round trip; `CliSettingsSharedBackendTests.NSettingsCommands_…ReuseOneSharedBackendOnTheConfiguredPort` stays green (attach-or-start reuse, K1a shape).
**Named watched-red gate:** `BackendSessionsAttachOrStartTests.Acquire_WithNoListener_StartsOnTheConfiguredPortNotAnEphemeralOne` — red today (private spawn is the default, `BackendSessions.cs:181-185`).
**Test classes:** `BackendSessionsAttachOrStartTests`, `BackendSessionsTokenExposureTests` (proof-gated rewrite), `CliSettingsAttachOrStartTests`, `CliSettingsTokenExposureTests` (proof-gated rewrite), `CliSettingsBackendTests`, `CliSettingsSharedBackendTests`, `ProxyPrivateBackendLifetimeTests` (fallback lifetime + `Shutdown_WithAttach_NeverStopsTheSharedBackend` successor = `Shutdown_NeverStopsAnAttachedSharedBackend`).

### P4 — serve / restart trust flow
**Scope:** attach-report restored but proof-gated; bare `serve --restart` restored but proof-gated; `/shutdown` token sends behind proof; F39 guard on the restart bind path.
**Touched:** `NodeRunner.cs` (:74-93 pre-bind, :232-236 `RefuseExistingServerAsync` becomes the unproven remedy line, :272-283 `ReportAttachedAsync` gains the proof step + pid/version from a *proven* `/observability`), `ServerRestart.cs` (:83-90 `!attaching` gate → proof gate before `tokenFile.Read()` at :92), `IServerRestart.cs` (drop `attaching`), `RestartOutcome.cs` (`AttachRequired` → `Unproven`; doc updates).
**AC:**
1. `serve` on a port held by a **proven** ai-raccoon reports the attach and exits 0 (pre-#643 behavior restored); it never prints a usable URL for an unproven listener (refuses 3, remedy = manual stop / other port).
2. Bare `serve --restart` against a **proven** server cycles it (token sent only after `Proven`) — the `dotnet tool update && serve --restart` rewrite flow works against current servers again.
3. Against a listener that cannot prove (incl. a legacy pre-proof ai-raccoon answering 404): zero requests carrying secrets, exit 3, stderr names the manual stop (the line at `NodeRunner.cs:251` minus `--attach`).
4. Foreign-listener and free-port semantics unchanged (`RestartOutcome.Foreign`/`Nothing`/`Unknown`, `ServerRestart.cs:71-77`); `ServeRestartTests` keeps its real-server cycle green (now bare).
5. Positive control: the real-server restart suite (11/11 lineage) passes with the proof in place.
**Named watched-red gate:** `ServeRestartProofTests.Restart_Bare_AgainstAProvenServer_CyclesIt` — red today (`RestartOutcome.AttachRequired`, `ServerRestart.cs:84-89`).
**Test classes:** `ServeRestartProofTests` (rework of `ServeRestartTests`), `ServeAttachProofTests`, `NodeRunnerTests` (update).

### P5 — Integration package (LAST): cross-package tests + ADR + docs
**Scope:** prove the packages compose under attack; land the record.
**Touched:** tests only under `tests/AiRaccoon.Tests/{E2E,Integration}` + `docs/adr/{0105,0106}-…`, `docs/adr/README.md`, `docs/reference/{agent-memory-server,logging-event-ids}.md`, `docs/how-to/configure-ai-raccoon-server.md`, `README.md`, `tests/…/TestHelpers/{Squatter,FakeRaccoon}.cs` (FakeRaccoon gains an optional real key/proof endpoint = "proven fake"; Squatter gains a "malicious prover" mode).
**AC (cross-package):**
1. End-to-end threat matrix across **all three** paths (proxy acquire, settings acquire, serve/restart): jsonrpc squatter, `/observability`-claiming name squatter (`FakeRaccoon` without key), malicious prover (wrong key), replaying prover (captured valid response), relaying prover (valid response from another root/port) → every cell: zero secret bytes to the attacker and no trust; function preserved via fallback where the path has one.
2. Positive control end-to-end: bare launch against a real proven `serve` attaches, hands the token over after proof, and a memory round trip works.
3. F49 cross-package: a project-scope run leaves **no** secret at the data-root top level and a backup of `<data-root>/.ai-raccoon/` carries both `mcp-token` and `identity-key` (the REVIEW-ASSEMBLED F49 "backups silently omit the credential" defect inverted).
4. F39 cross-package matrix: proxy, representative settings verbs, `serve --restart` at an explicit typo root → exit 22, directory still empty; default-root Quick Start simulation (`model embedding set local` then bare proxy) works.
5. Record: ADR-0106 (Decision = §2 table + §1 protocol + §1.5 supersession), ADR-0105 superseded + overruled-annotation, docs updated (no `--attach` anywhere — post-change grep gate, ADR-0105 Evidence tradition), `logging-event-ids.md` re-measured (`EveryEventIdInSource_FallsInsideADocumentedBlock` is the watched gate for it), `ExitCode.NoBank` doc broadened.
**Named watched-red gate:** `IdentityProofE2ETests.FullFlow_ProvenAttach_HandoverWorksAcrossProxySettingsAndRestart` — red today (today's default never attaches — private spawn, `BackendSessions.cs:181-185` — and bare restart refuses, `ServerRestart.cs:84-89`).
**Test classes:** `IdentityProofE2ETests`, `SecretPlacementE2ETests`, `NoBankE2ETests`.

## 4. (c) Parallelism map

```
Wave 0   P1  ────────────── (serial: state-dir accessor is everyone's foundation)
Wave 1   P2  ────────────── (files disjoint from P1; compiles against P1's BankState)
Wave 2   P3 ═╤═ P4         parallel ONLY on disjoint lanes:
             │             P3: Proxy/*, Settings/*, CliCommandTree/CliArgs/RootCliOptions/
             │                CliOptionsExtensions/ServeCommands (flag excision), ServerConfig
             │             P4: Hosting/Node/* semantics (NodeRunner logic, ServerRestart,
             │                IServerRestart, RestartOutcome)
             └ shared-file order: P3 first (it removes `--attach` incl. ServeCommands.cs:29,64-85
               and NodeRunner's `Attaching = options.Attach` use at NodeRunner.cs:41);
               P4 rebases on P3's parser commit before its own NodeRunner edits.
Wave 3   P5  ────────────── (serial last: integration + record; depends on P1..P4)
```

Everything else serialises: `ServerConfig.cs` and `NodeRunner.cs` are the two files both Wave-2 lanes touch, so those two files follow P3→P4 strictly. Lane rule (worktree-agent-isolation): each lane its own worktree, main checkout read-only.

## 5. (d) Complete test list — designed before implementation

Format: class · test · failure mode · watched-red (today's code = **T**, or the named mutation) · positive control. Command shape for every run: `dotnet test --project tests/AiRaccoon.Tests --filter-class '<Class>'` (never `--nologo`).

**P1 — `McpTokenFileStateDirTests`**
1. `Token_ResolvesInsideTheBankStateDirectory_NotTheDataRootTopLevel` — F49 placement — **T** (red: `McpTokenFile.cs:42`) — control: user scope still `<dataRoot>/mcp-token`.
2. `StateDirectory_IsOwnerOnlyAndTokenIsOwnerReadWrite` — permissive secret file — T — control: mint still converges for racing callers.
**P1 — `TokenFileLegacyMoveTests`**
3. `LegacyTopLevelToken_IsMovedIntoTheStateDirectory` — old secret left unignored in the working tree — T (no move exists) — control: an existing state-dir token is returned, never re-minted.
4. `BothTokensPresent_StateDirWinsAndTheLegacyIsReportedForRemoval` — ambiguity — T — control: differing legacy token never shadows the state-dir one.
**P1 — `NoBankAutoLaunchTests`**
5. `SettingsVerb_WithExplicitDataRootAndNoBank_RefusesNoBankAndMintsNothing` — F39 measured repro — **T** (today: exit 0 + 4 files) — control: default root still bootstraps.
6. `ProxyLaunch_WithExplicitDataRootAndNoBank_RefusesNoBankAndMintsNothing` — bare-launch mint — **T** — control: default-root proxy still spawns and serves.
7. `ServeRestart_WithExplicitDataRootAndNoBank_RefusesNoBankBeforeCycling` — restart bind mints at typo root — **T** — control: `Serve_WithExplicitDataRootAndNoBank_StillCreatesTheBank` (explicit creation path preserved).

**P2 — `IdentityKeyFileTests`**
8. `EnsureAsync_MintsAnOwnerOnlyEcdsaKey_InTheBankStateDirectory` — no proof anchor / wrong location — **T** (type absent) — control: two `EnsureAsync` calls converge on one key.
9. `EnsureAsync_AfterDebris_RemintsInsteadOfWedging` — crash between create and write — T — control: a valid key is never touched (mutation: heal deletes valid keys → control red).
**P2 — `ProofBindingTests`**
10. `SignedPayload_BindsNonceRootPortAndClientPort` — relay/replay/cross-root — T — mutation: drop any one field from the canonical string → that field's assert goes red — control: identical inputs → identical bytes.
**P2 — `IdentityProofEndpointTests`**
11. `Prove_WithFreshNonce_ReturnsASignatureVerifyingUnderTheStateDirKey` — proof doesn't actually prove — **T** (404 today) — control: round trip accepted.
12. `Prove_BindsTheSocketPeerPort_NotTheBody` — spoofed client port (relay) — T — mutation: read port from request → test's spoof flips the result → red.
13. `Prove_IsReachableWithoutTheToken_AndRevealsNothingButASignature` — token-before-proof ordering leak — T — control: `/mcp` without token still 401 (default-closed preserved).
**P2 — `BackendIdentityVerifierTests`**
14. `Verify_AgainstAKeyItDidNotGenerate_Refuses` — malicious prover — T — control: honest signer passes.
15. `Verify_ReplayedResponse_ForAFreshNonce_Refuses` — replay — T — control: fresh response passes.
16. `Verify_WhenTheRootFingerprintOrPortsDiffer_Refuses` — relay — T — control: matching binding passes.

**P3 — `BackendSessionsAttachOrStartTests`**
17. `Acquire_WithNoListener_StartsOnTheConfiguredPortNotAnEphemeralOne` — revert clause 1 — **T** (red: `BackendSessions.cs:181-185`) — control: `Acquire_WithAProvenListener_AttachesWithoutSpawning`.
18. `Acquire_NeverComposesTheTokenIntoARequestBeforeProofCompletes` — security invariant (ordering) — T — mutation: move `tokenFile.Read()` ahead of verify → red (assert request capture timeline).
**P3 — `BackendSessionsTokenExposureTests` (proof-gated rewrite)**
19. `Acquire_WithASquatterHoldingTheConfiguredPort_SendsZeroSecretBytes_AndServesViaFallback` — F70 leak, proof-gated form — T — control: proven attach read-through works.
20. `Acquire_WithAReplayingOrWrongKeyProver_SendsZeroSecretBytes` — subverted prover — T — control: honest prover passes.
21. `Shutdown_TheFallbackBackendIsStoppedTheAttachedOneIsNot` — lifetime ownership (K1a rule kept) — T (fallback lifetime untested today) — control: `Shutdown_NeverStopsAnAttachedSharedBackend`.
**P3 — `CliSettingsAttachOrStartTests` / `CliSettingsTokenExposureTests` (rewrite) / `CliSettingsSharedBackendTests` / `CliSettingsBackendTests`**
22. `Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes` — F70 settings path — T — control: proven read-through (`CliSettingsTokenExposureTests` positive lineage).
23. `ASettingsCommand_AfterFallback_DisclosesTheFallbackBackendAndHowToStopIt` — F38/N1 disclosure under the new shape — T (text names `--attach` today, `CliSettingsBackend.cs:85`) — control: `NSettingsCommands_…ReuseOneSharedBackendOnTheConfiguredPort` stays green (control, green today by K1a ruling).
24. `AcquireAsync_WhenItSucceeds_LogsThatTheBackendOutlivesTheCommand` — N1 log regression — control (green today; guards the kept ruling).

**P4 — `ServeAttachProofTests`**
25. `Serve_OnAProvenListener_ReportsAttachAndExitsZero` — revert clause 1 (serve) — **T** (today: exit 3, `NodeRunner.cs:232-236`) — control: no token bytes leave during attach-report.
26. `Serve_OnAnUnprovenListener_RefusesPortInUse_NamingTheManualStop_AndPrintsNoUsableUrl` — operator-relay token leak — T (today's line names `--attach`) — control: foreign-listener line unchanged.
**P4 — `ServeRestartProofTests` (rework of `ServeRestartTests`)**
27. `Restart_Bare_AgainstAProvenServer_CyclesIt` — revert clause 1 (restart) — **T** (red: `RestartOutcome.AttachRequired`, `ServerRestart.cs:84-89`) — control: real-server cycle green (11/11 lineage).
28. `Restart_Bare_AgainstAListenerThatCannotProve_SendsNoToken_AndNamesTheRemedy` — F70 restart route — T (red under pre-K1 mutation: FakeRaccoon receives `/shutdown` + token; today's green form asserts the invariant holds with zero token headers — kept as the proof-gated gate) — control: legacy-server refusal names the manual stop.

**P5 — `IdentityProofE2ETests`**
29. `FullFlow_ProvenAttach_HandoverWorksAcrossProxySettingsAndRestart` — composition of P2+P3+P4 — **T** (today never attaches) — control: memory write/read round trip through the proven channel.
30. `ThreatMatrix_FiveAttackers_ThreePaths_ZeroSecretBytesEverywhere` — the security invariant end to end — T (matrix of §P5 AC1) — control: honest cell green.
**P5 — `SecretPlacementE2ETests`**
31. `ProjectScopeRun_LeavesNoSecretAtTheDataRootTopLevel` — F49 — **T** (today: `mcp-token` at top level) — control: state-dir backup carries `mcp-token` + `identity-key`.
**P5 — `NoBankE2ETests`**
32. `EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBankAndLeavesTheDirectoryEmpty` — F39 matrix — **T** — control: default-root Quick Start simulation green.
**P5 — registry/doc gates (existing, watched):**
33. `LoggerMessageEventIdTests.EveryEventIdInSource_FallsInsideADocumentedBlock` + `EventIdBlocks_DoNotInterleaveBetweenOwners` (`LoggerMessageEventIdTests.cs:35,78`) — new log lines outside the registry — go red the moment a P2-P4 log line lands without the re-measure — control: `EventIds_AreUniqueAcrossTheAssemblies` (:18).
34. `HowToExitTableTests` (`tests/AiRaccoon.Tests/Unit/Setup/Diagnostics/HowToExitTableTests.cs:30`) — exit-code doc drift from the `NoBank` doc broadening — control: `ExitCodeTests.NoBank_IsTwentyTwo` (:20).

## 6. (e) Risks

1. **Upgrade to a pre-proof server.** A running old binary can't answer `/identity/prove` (404) — attach, restart and settings reuse all refuse/fallback until it's stopped manually. Mitigation: every refusal names the manual stop (F53) and the ADR/tutorial carry the one-time rewrite note (this replaces ADR-0105's `--attach` note). Accepted cost of the absolute invariant.
2. **Same-uid attacker.** Reads `identity-key` and `mcp-token` directly and can forge everything. Out of scope by the unknown-#2 rationale; recorded in ADR-0106's threat model so nobody mistakes the proof for a same-user defense.
3. **Windows ACLs.** `0700`/`0600` are POSIX-only (`McpTokenFile.cs:159-160` precedent) — Windows inherits the data-root ACL (accepted non-goal, same as ADR-0020).
4. **Path canonicalization for `dataRootFp`** across macOS/Linux/Windows (case, separators, symlinks). Mitigation: one canonicalizer in `ProofBinding` used by both sides, and the binding is fail-closed — a mismatch refuses rather than trusts. Tests 10/16 cover it.
5. **Fallback double-writer** (squat forces a second server on the same bank). Safe per the ADR-0105 "Concurrent access" findings (WAL + `busy_timeout=5000`, defined `bank-busy` refusal) but re-widens the dual-writer states K1 had removed — covered by keeping `ProxyPrivateBackendLifetimeTests` green.
6. **Quick Start / first-run doc drift** from the F39 guard. Mitigation: the default-root carve-out (§2 boundary decision) keeps README.md:50-74 working; P5 ships the docs edits and the Quick Start simulation gate (test 32's control).
7. **Signature-oracle optics** on the unauthenticated endpoint. Signatures over verifier nonces leak nothing about the key (§1.2); the ADR records the analysis so the 0105 "unauthenticated cryptographic oracle" objection is answered, not dodged.
8. **EventId registry churn** (2-4 new log lines). Mitigation: prefer stderr-only refusal lines (the `NodeRunner` pattern), re-measure `logging-event-ids.md` in the same commit; tests 33 enforce it.

---

## Final mapping table

| ruling clause (incl. F49, F39) | package | acceptance criterion |
|---|---|---|
| R1 — revert to attach-or-start; launch only if none running | P3 | `Acquire_WithNoListener_StartsOnTheConfiguredPortNotAnEphemeralOne` green; `Acquire_WithAProvenListener_AttachesWithoutSpawning` green (control) |
| R1 — revert …(settings path) | P3 | `NSettingsCommands_…ReuseOneSharedBackendOnTheConfiguredPort` green (K1a shape kept) |
| R1 — revert …(serve attach-report) | P4 | `Serve_OnAProvenListener_ReportsAttachAndExitsZero` green |
| R1 — revert …(bare `serve --restart`) | P4 | `Restart_Bare_AgainstAProvenServer_CyclesIt` green |
| R2 — private-key proof of identity before trust | P2 | `Prove_WithFreshNonce_ReturnsASignatureVerifyingUnderTheStateDirKey` + `IdentityKeyFileTests.EnsureAsync_MintsAnOwnerOnlyEcdsaKey_InTheBankStateDirectory` green |
| R2 — proof gates every handover (proxy/settings/restart/private) | P3, P4 | `Acquire_NeverComposesTheTokenIntoARequestBeforeProofCompletes` green; `Restart_Bare_AgainstAListenerThatCannotProve_SendsNoToken_AndNamesTheRemedy` green |
| R3 — custom non-MCP proof channel allowed | P2 | `IdentityProofEndpointTests.Prove_BindsTheSocketPeerPort_NotTheBody` + `Prove_IsReachableWithoutTheToken_AndRevealsNothingButASignature` green (endpoint outside `MapMcp`, inside `OpenPaths`) |
| SI — token/key material never reaches an unproven listener | P3, P4, P5 | `ThreatMatrix_FiveAttackers_ThreePaths_ZeroSecretBytesEverywhere` green: `Squatter.Requests ⊆ {POST /identity/prove}`, zero token headers/bodies in every attacker cell |
| F49 — token + key in the bank state directory, never the data-root top level | P1, P2, P5 | `Token_ResolvesInsideTheBankStateDirectory_NotTheDataRootTopLevel` green; `ProjectScopeRun_LeavesNoSecretAtTheDataRootTopLevel` green; state-dir backup carries `mcp-token` + `identity-key` |
| F39 — a mistyped `--data-root` never mints a bank on auto-launch (NoBank guard) | P1, P5 | `SettingsVerb_WithExplicitDataRootAndNoBank_RefusesNoBankAndMintsNothing` and `EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBankAndLeavesTheDirectoryEmpty` green: exit 22 (`ExitCode.cs:64-66`), directory `[]`; default-root bootstrap + explicit `serve`/`encryption` creation preserved (controls) |

**Stop-condition check:** every clause above maps to ≥1 AC ✓; every package names a gate red at today's code (P1: tests 1/5, P2: test 8, P3: test 17, P4: test 27, P5: tests 29/31/32) ✓; every unknown (§2) has exactly one recommendation ✓.