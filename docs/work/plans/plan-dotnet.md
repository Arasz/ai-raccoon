# Implementation plan — attach-or-start + cryptographic proof of backend identity (task `air-backend-launch-identity-proof`)

Sources of truth read in full: `docs/work/2026-09-22-backend-launch-identity-proof-research.md` (owner ruling verbatim, p34 fold-in, F1–F70 facts, 6 unknowns, constraints) and `docs/adr/0105-private-spawn-is-the-launch-default.md` (its "Alternatives rejected → Mutual proof" is **overruled** by the 2026-09-22 owner ruling; its "Concurrent access" and K1a/N1 lifetime/disclosure rulings stand). All code anchors below verified this session against the worktree at `d2532be7`+.

**Ruling clauses** (the 5 rows the final table must cover):
- **C1** — revert the launch default: the CLI/proxy attaches to a running instance and launches one **only if none is running** (attach-or-start).
- **C2** — squatter defense = proof of identity: ai-raccoon generates a **private key**; a listener must prove possession **before any token or secret reaches it**.
- **C3** — protocol freedom: a custom **non-MCP** proof channel is explicitly allowed.
- **F49** — the token **and the new identity key** live in the bank state directory (`<data-root>/.ai-raccoon/` in project scope), never the data-root top level (`McpTokenFile.cs:41` vs `SqliteConnectionFactory.cs:166-176`).
- **F39** — a mistyped `--data-root` must **never mint a bank** outside `doctor`: the attach-or-start auto-launch path checks for the bank **before** the server's first open runs `MemorySchema.EnsureAsync` (`SqliteConnectionFactory.cs:253-261`, `MemorySchema.cs:623`).

---

## §A Design decisions — one recommendation per known unknown

**D1 — Key shape: ECDSA P-256 keypair (`System.Security.Cryptography.ECDsa.Create(ECCurve.NamedCurves.nistP256)`), PKCS#8 PEM on disk, 0600.**
Rationale: the ruling says "private key … only ai-raccoon will have this key" — a signature scheme where possession is proven without the verifier holding a secret is exactly asymmetric crypto, and `ECDsa` is BCL (constraint: no new NuGet; prefer `System.Security.Cryptography`).
Rejected: Ed25519 (not in the BCL — would force NSec/BouncyCastle, violating the NuGet constraint for no loopback-local benefit); symmetric HMAC (contradicts "private key"; turns the verifier into a second secret-holder and degenerates the proof into "knows the shared secret", i.e. a second bearer token, collapsing D6).

**D2 — Key scope/location: one keypair per bank state directory, sibling of `mcp-token`, minted by `serve` like the token.**
Rationale: F49 hard-constrains location, and per-data-root scope is the only scope that makes a *different-data-root* ai-raccoon fail the proof (its key differs) while a same-data-root instance passes — per-install keys would let any ai-raccoon on the machine prove.
Rejected: per-install/user key (wrong-instance attach would verify); separate `mcp-identity.pub` beside the private key (a second file that can drift; the verifier derives the public half via `ExportSubjectPublicKeyInfo()`).

**D3 — Proof wire: `POST /identity` on the existing loopback listener; request `{"nonce":"<base64url 32 bytes>"}`, response `{"keyId":"<base64url sha256-SPKI>","signature":"<base64url DER ECDSA>"}` where the signature covers `SHA-256("ai-raccoon/proof/v1\n" + nonce)`; freshness = a verifier-generated, single-use, in-memory 32-byte RNG nonce per acquire.**
Rationale: one round trip on the custom channel C3 allows; the domain-separated label blocks cross-protocol use of the signing oracle; the per-data-root key **is** the identity, so port/data-root/version fields add nothing verifiable (the verifier cannot recompute the server's private context — it would be self-asserted equality anyway).
Rejected: timestamp replay windows (clock discipline, no gain over unique nonces — **replay window: none, by construction**); signing port+dataRootId context (unverifiable reconstruction, wire bloat); a GET handshake then POST (extra RTT for nothing); putting the proof behind `McpTokenGate` (it is pre-token by definition — it goes into `OpenPaths`, `McpTokenGate.cs:31`).

**D4 — Failure mode when proof fails: proof failure means "no instance is running" — so C1 fires: launch one.** The configured port is held, so the launch takes an ephemeral port (`StartPrivateAsync`) and a **loud warning** (new `[LoggerMessage]`, re-measure `docs/reference/logging-event-ids.md`) names the squatted port and the remedy ("stop the listener on port N, or run with another --port"). The one exception: an explicit `serve` / `serve --restart` **on the configured port** refuses exit 3 (`PortInUse`) + remedy — there the operator asked for *that* port and no fallback exists.
Rationale: this is the ruling's own logic ("launch only if no instance is running" — a squatter is not an instance); it keeps the F70 machinery earning its keep and never breaks the user for the squatter's fault.
Rejected: hard-refuse everywhere (user failure for an attacker's action; discards tested fallback machinery); silent fallback (violates F53/N1 disclosure spirit).

**D5 — Survivors of #643 (d09d4ead):**
| item | verdict |
|---|---|
| `--attach` flag (both spellings) | **Remove** — attach-or-start is again the default and a "trust without proof" flag reopens F70; a one-day-old flag needs no deprecation shim (rejected: keep-as-no-op = a lying flag; `--transport stdio`-style hint shim = parser surface for nothing). Named in release notes (`traceable-releases`). |
| plain-`serve` exit-3 refusal | **Revert** to pre-#643 "attach and exit 0" (`NodeRunner.cs:87,208` `ReportAttachedAsync`), now **proof-gated**; exit 3 only for a holder that cannot prove (`RefuseExistingServerAsync`, `NodeRunner.cs:231`, re-worded with remedy). |
| bare `serve --restart` | **Restore** bare cycling, proof-gated: `RestartOutcome.AttachRequired` → `RestartOutcome.Unproven` (`RestartOutcome.cs`, `ServerRestart.cs:81-84`); the token read at `ServerRestart.cs:88` happens only after a verified proof. |
| proxy-stops-private-backend | **Keep** (`BackendSessions.cs:99,110`, EventIds 688-689) — K1a lifetime ruling stands; the fallback child is "private", so it is stopped with the proxy. |
| F38/N1 disclosure | **Keep** (`CliSettingsBackend.cs`, EventId 687). |
| `CliArgs` bool-flag arity fix | **Keep** (`CliArgs.cs:136-167`) — independent bugfix. |

**D6 — The proof gates the token handover; the bearer token stays.**
Rationale: minimal scope — `McpTokenGate` (default-closed, `McpTokenGate.cs:31,78`), the settings routes and `/shutdown` all ride the token; replacing it means deriving a channel token from the key and touching every request path and error body, and a key leak becomes full-channel compromise (the token stays an independent secret).
Rejected: derive-and-delete the token file (blast radius ×10 for one fewer 0600 file).

**Cross-cutting (the security invariant's enforcement point).** The proof runs **between acquire and `tokenFile.Read()` at every trust point, for every URL** — `BackendSessions.OpenAsync` (`BackendSessions.cs:70`), `CliSettingsBackend.AcquireAsync` (`CliSettingsBackend.cs:73`), `ServerRestart.CycleAsync` (`ServerRestart.cs:88`) — including private children. Proving the private child too closes the TOCTOU residual ADR-0105 admits ("the child dies between that check and the caller's first request") and leaves **one** gate, no "creation counts as proof" special case.

**Gate-assertion amendment (stated honestly).** The constraint "F70 gates … zero requests to a squatter" is achievable only under the never-attach default this ruling reverts: attach-or-start inherently dials the configured port — `ServerProbe` already sends `POST /mcp` body `"x"` (`ServerProbe.cs:55-58`), and the proof challenge is one more request. The gates therefore assert the invariant's substance: **zero secret bytes** (no `X-AiRaccoon-Token` value, no key material, no tool/settings payloads) and **zero data-bearing requests** — the only traffic an unproven listener sees is the probe's `"x"` and the `/identity` challenge (public randomness). This is the "proof-gated form" of the gates.

---

## §B Emphasis answers (.NET/hosting implementation expert)

### B1 Per-file revert-vs-rework on the d09d4ead surface

| file | verdict | what happens |
|---|---|---|
| `Hosting/Proxy/BackendLauncher.cs` | **KEEP mechanics, demote role** | `AcquireAsync` (:98) regains primacy (probe → start → poll → last-chance, untouched); `StartPrivateAsync` (:56) becomes the D4 launch path when the configured port holds an unproven listener — **byte-for-byte kept**, including the `HasExited` TOCTOU close. No signature changes. Doc comments re-worded (default ↔ fallback). |
| `Hosting/Proxy/IBackendLauncher.cs` | **docs only** | Both methods stay; the "default path is private spawn" doc becomes "attach-or-start; private spawn is the no-instance launch path". |
| `Hosting/Proxy/BackendSessions.cs` | **REWORK** | `AcquireBackend` (:174-185): `config.Attach` ternary (:183-185) → `AcquireAsync` primary → `IIdentityProver.ProveAsync(url)` → fail ⇒ warn (new EventId 690) + `StartPrivateAsync`. `OpenAsync` (:53-77): proof completes **before** `_tokenFile.Read()` (:70); the `config.Attach` reason branches (:56,71) collapse to one wording. `_privateBackends`/`StopPrivateBackendsAsync` (:63-67,110) KEEP (K1a). F39 pre-check at the top of `AcquireBackend`. |
| `Hosting/Node/IServerRestart.cs` | **REWORK** | `CycleAsync(int, McpTokenFile, CancellationToken)` — the `attaching` gate is replaced by proof inside `CycleAsync`. |
| `Hosting/Node/ServerRestart.cs` | **REWORK** | The `!attaching` block (:81-84) → `IIdentityProver` proof against `BaseUrl:{port}`; unproven ⇒ `RestartOutcome.Unproven` with re-worded EventId 657 (message change → re-measure registry). `tokenFile.Read()` (:88) and `/shutdown` request flow unchanged after the gate. |
| `Hosting/Node/NodeRunner.cs` | **REVERT + REWORK** | `RestartServer` (:85-88): the Answered branch becomes *prove → `ReportAttachedAsync` (exit 0, pre-#643) / refuse exit 3 with remedy* (`RefuseExistingServerAsync` :231 re-worded); `RestartRefusal` `AttachRequired` arm (:249) → `Unproven`; `NodeLaunchDescriptor.Attaching` deleted; token mint (:53-60) kept (path moves via P1); `ReportAttachedAsync` (:272) kept. |
| `Hosting/Node/RestartOutcome.cs` | **REWORK** | `AttachRequired` → `Unproven` ("the listener did not prove possession of this data root's identity key"). `RestartTransition.MayBind` already excludes it — `RestartTransition.cs` **unchanged**. |
| `Settings/CliSettingsBackend.cs` | **REWORK** | K1a attach-or-start acquire (:57) stays primary; F39 pre-check before the launcher call (:57); proof before `_tokenFile.Read()` (:73); proof fail ⇒ warn + `PrivateServeArguments` launch (D4); port-range check (:47) and EventId 687 disclosure KEEP. |
| `Setup/Cli/CliArgs.cs` | **REVERT (partial)** | Drop the `Attach` read (:196). **Keep** the `ContainsVerb` bool-flag arity fix (:136-167) — separate bugfix. |
| `Setup/Cli/CliCommandTree.cs` | **REVERT** | Delete `AttachOption` (:59), `ServeAttachOption` (:65), their registrations (:138, :645); reword `LaunchPortOption`/`ServeRestartOption` descriptions (drop `--attach` mentions). |
| `Setup/Cli/CliOptionsExtensions.cs` | **REVERT** | Drop `Attach = options.Attach` (:33). |
| `Setup/Cli/RootCliOptions.cs` | **REVERT** | Drop `Attach` (:28). |
| `Setup/Cli/Commands/ServeCommands.cs` | **REVERT** | Drop `ResolveAttach` (:64-81) and `NodeCliOptions.Attach` (:29, :85); keep `ResolvePort`. |
| `Hosting/Common/ServerConfig.cs` | **REVERT** | Drop `Attach` (:23); `PrintMembers` (which never prints `McpToken`) keeps excluding all secret-bearing members. |
| `Hosting/Common/McpTokenFile.cs` | **REWORK (F49)** | see B4. |

### B2 Where the proof endpoint lives
**Minimal-API endpoint class `IdentityProofEndpoint` in `Hosting/Node/`** (the `ShutdownEndpoint` shape, `ShutdownEndpoint.cs:17-30`), mapped in `McpServerSetup.ConfigureMcpEndpoints` next to `MapObservability()` (`McpServerSetup.cs:92`) and `MapShutdown()` (:95), and listed in `McpTokenGate.OpenPaths` (`McpTokenGate.cs:31`) — the gate is default-closed (`McpTokenGate.cs:78`), so the pre-token proof passes through the one existing escape hatch and everything else stays gated. The endpoint loads its signer from `McpIdentityFile` at map time (key mint included), so **no key material ever transits `ServerConfig`** (whose `PrintMembers` deliberately hides the token, `ServerConfig.cs:28-35`).
Rejected: middleware intercepting before `McpTokenGate` (bypasses the default-closed design and is invisible in `OpenPaths`); a second listener/port (a second squat surface); anything inside `/mcp` (would need the token it is supposed to gate).

### B3 Key generation / storage / permissions (BCL)
`McpIdentityFile` (new, `Hosting/Common/`, beside `McpTokenFile`): mint with `ECDsa.Create(ECCurve.NamedCurves.nistP256)`, persist `ExportPkcs8PrivateKeyPem()` via the exact `McpTokenFile.TryMintAsync` shape (`McpTokenFile.cs:158-186`): `FileMode.CreateNew`, `FileShare.None`, `UnixCreateMode = UserRead|UserWrite` (0600 POSIX; Windows inherits the state-dir ACL, same accepted gap as ADR-0020 records for the token). Heal semantics mirror the token: content that could not have been written by the mint is debris (delete + re-mint under the same gate); a **valid** key is never rotated. Verify side: import PEM → `VerifyData(SHA256, DER)` + `keyId` = base64url-sha256 of `ExportSubjectPublicKeyInfo()`. RNG for the nonce: `RandomNumberGenerator.GetBytes(32)`. Zero new NuGet packages.

### B4 F49 file-relocation mechanics
Readers/writers of the token path today (complete, from grep):
- **mint/write**: `McpTokenFile.EnsureAsync`/`TryMintAsync` — called by `serve` (`NodeRunner.cs:53`) and test seeding (13 call sites in `McpTokenFileTests`, `ServeRestartTests`, `ProxyLaunchE2ETests`, …).
- **read**: `McpTokenFile.Read` — `BackendSessions.cs:70,112`, `CliSettingsBackend.cs:73`, `ServerRestart.cs:88` (via injected instance), `NodeRunner` (descriptor, :42), test helpers (`RaccoonBackendCleanup.cs:17`).
- **path display**: `McpServerSetup.cs:83` (gate error bodies name the file), refusal lines in `NodeRunner`/`BackendSessions`/`CliSettingsBackend`.
- **hard-coded `Path.Combine(dataRoot, McpTokenFile.FileName)`**: `ProxySpawnedBackendE2ETests.cs:110,132`, `McpServerLaunchArgsE2ETests.cs:94,133`, `AppRunnerProxyRoutingTests.cs:41`, `TransportRemovalTests.cs:88`.

Mechanics: promote `SqliteConnectionFactory.BankDirectoryFor` (`SqliteConnectionFactory.cs:166-171`, today `private`) to public beside `BankPathFor` (:176) — one source of truth for "bank state directory" (`derive-or-delete-the-list`). `McpTokenFile` and `McpIdentityFile` take `InfrastructureOptions` and compute `Path = Path.Combine(BankDirectoryFor(options), FileName)` — user scope is the data root itself (which is its bank dir), project scope is `<data-root>/.ai-raccoon/`. **Migration:** `Read` falls back to the legacy top-level file when the new path misses (upgrade window: a running old `serve` keeps its in-memory token; a new client still finds it); `EnsureAsync` **migrates**: a parseable legacy token is copied through the exclusive-create mint to the new path and the legacy file deleted (never two live secrets); `TryMintAsync` writes **only** the new path. If neither parses: fail closed (`Read` → null → the existing "holds no token" refusals), never a top-level mint again. All 6 hard-coded test paths and the 5 constructor call sites update in the same package.

### B5 F39 NoBank pre-check placement
At the top of the two auto-start acquires — `BackendSessions.AcquireBackend` (`BackendSessions.cs:174`) and `CliSettingsBackend.AcquireAsync` (`CliSettingsBackend.cs:43`) — before `launcher.AcquireAsync` runs, i.e. **before any spawn whose first open would run `MemorySchema.EnsureAsync`** (`SqliteConnectionFactory.cs:253-261`). Check: `File.Exists(SqliteConnectionFactory.BankPathFor(config.Options))`; miss ⇒ new `BankMissingException(bankPath)` with the F53 remedy line ("create it first — `ai-raccoon encryption …` — or fix `--data-root`"), caught at the two composition roots to return **`ExitCode.NoBank` (22)**: `ProxyRunner.RunAsync` (`ProxyRunner.cs:29-33`, before the `BackendUnavailableException` catch) and `ConfigCommands` dispatch (`ConfigCommands.cs:147-160`, before `SettingsServerUnavailableException`). `ExitCode.cs:64-66`'s comment is amended to record that `NoBank` is now also the auto-start guard. Deliberately strict (checked before attach too): it can only move a failure earlier and better-worded — the attach branch needs the same state dir's token anyway (`BackendSessions.cs:70`). Manual `serve` and `encryption` keep minting (F39's named scope is the auto-launch path; `encryption` is the sanctioned bootstrap, `CliWriteOptOuts.cs:11-17`). The pre-check runs before the spawn **and** before the D4 fallback spawn (which would otherwise mint via its child).

### B6 What `StartPrivateAsync`'s machinery becomes
Demoted from *default* to *the "no instance running" launch path while the configured port is held* (D4) — a promotion in standing, actually: it is now the ruling's C1 clause made executable. `BackendLauncher.StartPrivateAsync` + `BackendLaunchArguments.PrivateServeArguments` + `BackendSessions._privateBackends`/`StopPrivateBackendsAsync` (K1a) all stay as-is; the URL-from-stdout-only rule (F70/K1's attach-proofing) is exactly what makes it a safe landing zone next to a squatter, and the proof-at-handover covers its TOCTOU residual.

---

## §C Packages

Discipline for every package: TDD — failing behavior test first, **watched red**, then implementation, then watched green, then the named **positive control**; mutation listed must flip the gate red again (`prove-the-check-fails`). Gate command form (never `--nologo`): `dotnet test --project tests/AiRaccoon.Tests --filter-class '*<Class>'`. All tests use scratch data roots (`TestData.CreateTempRoot`) and ephemeral ports — `~/.ai-raccoon` and port 7721 are never touched. One PR per package, small commits pushed after each (`small-commits-early-draft-pr`), squash-merge with origin/main merged in first. No new NuGet packages anywhere.

### P1 — Secret relocation + identity primitive (F49, C2 groundwork)
**Scope:** bank-state-directory plumbing (F49) and the key/proof types (no launch-behavior change). 
**Touched files:** `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs` (`BankDirectoryFor` public), `src/AiRaccoon/Hosting/Common/McpTokenFile.cs` (location, legacy read-fallback, ensure-time migration), `src/AiRaccoon/Hosting/Common/McpIdentityFile.cs` (new), `src/AiRaccoon/Hosting/Common/IdentityProof.cs` (new: domain-separated `Sign`/`Verify`, keyId — static: pure functions only), call-site constructor updates: `BackendSessions.cs:45`, `CliSettingsBackend.cs:72`, `NodeRunner.cs:42`, `McpServerSetup.cs:83`, test-helper updates (`RaccoonBackendCleanup.cs:17` + 6 hard-coded paths).
**AC:**
1. *F49*: `mcp-token` and `mcp-identity` are minted only inside the bank state directory (`.ai-raccoon/` in project scope; data root in user scope); nothing 0600 is ever created at the data-root top level.
2. *F49*: a legacy top-level `mcp-token` is read in preference to nothing, and is migrated (single surviving secret) on the next `EnsureAsync`; unparseable content at either path fails closed.
3. *C2*: `McpIdentityFile` mints a 0600 PKCS#8 PEM ECDSA P-256 keypair, converging under concurrent minters; `IdentityProof.Verify` accepts only a signature over the exact domain-separated nonce under that data root's own key.
4. Watched-red gate: `McpTokenFileTests.EnsureAsync_ProjectScope_MintsInTheBankStateDirectory_NotTheDataRootTopLevel` (red today: `McpTokenFile.cs:41` writes top level). Positive controls: `McpTokenFileTests.Read_FallsBackToTheLegacyPath`, `McpIdentityFileTests.EnsureAsync_ConcurrentMintersConvergeOnOneKey`; mutation: revert `Path` to `Combine(dataRoot, …)` → red.

### P2 — Proof endpoint on the loopback listener (C2 server half, C3)
**Scope:** the custom non-MCP challenge endpoint + key mint at host build. *(parallel lane with P3, see §D)*
**Touched files:** `src/AiRaccoon/Hosting/Node/IdentityProofEndpoint.cs` (new), `src/AiRaccoon/Setup/McpServerSetup.cs` (map beside :92/:95; mint `McpIdentityFile` at map time), `src/AiRaccoon/Hosting/Node/McpTokenGate.cs` (`OpenPaths` :31 += `/identity`).
**AC:**
1. *C3*: `POST /identity` on the existing loopback listener answers any 32-byte base64url nonce with `keyId` + a signature that verifies under `IdentityProof`; malformed nonce ⇒ 400 naming the field and its remedy (F53).
2. *C2*: the endpoint is reachable **without** a token (it is pre-token by definition) while `/mcp` and `/shutdown` still 401 without one — the `OpenPaths` widening opens exactly one path (mutation: also open `/mcp` → `McpTokenGateTests` red).
3. *C2*: no key material appears in any response body or log line (server holds the signer in the endpoint only).
4. Watched-red gate: `IdentityProofEndpointTests.PostIdentity_WithAValidNonce_AnswersASignatureThatVerifies` (red today: no endpoint ⇒ 404). Positive controls: `IdentityProofEndpointTests.PostIdentity_WithAMalformedNonce_400sNamingTheField`, `McpTokenGateTests.McpPath_StillRefusesWithoutToken_WhenIdentityEndpointIsOpen`.

### P3 — Attach-or-start + proof-gated handover on the acquire channels (C1, C2, F39, D4/D6)
**Scope:** proxy and settings channels: attach-or-start primary, proof before every token read, private-spawn launch on unproven holders with loud warning, F39 pre-check, `NoBank` exit mapping. *(P2 in parallel; `--attach` flag untouched here — P4 owns its deletion.)*
**Touched files:** `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs`, `src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs` + `IBackendLauncher.cs` (docs only), `src/AiRaccoon/Settings/CliSettingsBackend.cs`, `src/AiRaccoon/Hosting/Common/IdentityProver.cs` (new: `IIdentityProver` — nonce, POST `/identity`, `keyId`+signature verify, verdict + reason), `src/AiRaccoon.Hosting…/BankMissingException` (new, `Hosting/Common`), `src/AiRaccoon/Hosting/Proxy/ProxyRunner.cs` (catch → 22), `src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs` (catch → 22), `src/AiRaccoon/ExitCode.cs` (NoBank doc), `TestHelpers/{Squatter,FakeRaccoon}.cs` (scriptable `/identity` responses).
**AC:**
1. *C1*: with a **proven** ai-raccoon already answering `--port`, the proxy and a settings verb attach to it — no second backend is spawned (watched-red: red today, private-spawn default spawns).
2. *C2 (invariant)*: against a squatter holding `--port`, zero secret bytes and zero data-bearing requests reach it (only `ServerProbe`'s `"x"` and the nonce challenge); the run continues on a private fallback URL (proxy) per D4.
3. *C2 (invariant)*: the token is read (`BackendSessions.cs:70`, `CliSettingsBackend.cs:73`) only after a verified proof — including for private children (TOCTOU close).
4. *F39*: an empty/mistyped `--data-root` on either channel exits **22 (`NoBank`)** with the creation remedy and leaves the directory untouched (no `memory.db`, no `mcp-token`, no `mcp-identity`) — watched-red: red today (measured F39 shape: exit 0 + 4 minted files).
5. *C1/F53*: the fallback warning names the squatted port and the remedy; N1 disclosure (687) and K1a stop (688-689) still fire.
6. Watched-red gates: `CliSettingsTokenExposureTests.AcquireAsync_WithASquatterHoldingThePort_ProvesBeforeAnythingSecretMoves` (red today: the measured F70 token handover) and `BackendSessionsTokenExposureTests.Acquire_WhenAProvenBackendAnswersTheConfiguredPort_AttachesInsteadOfSpawning` (red today: private spawn). Positive controls: `CliSettingsSharedBackendTests` round trip through a proven backend, `ProxyPrivateBackendLifetimeTests` (fallback child still stopped). Mutations: delete the proof call → squatter receives the token (red); delete the F39 pre-check → typo'd root mints (red).

### P4 — Serve/restart channel + CLI surface (C1, C2) — serial after P3
**Scope:** revert `serve`'s bind/restart behavior to pre-#643 semantics behind the proof, and delete `--attach` everywhere.
**Touched files:** `src/AiRaccoon/Hosting/Node/NodeRunner.cs`, `ServerRestart.cs`, `IServerRestart.cs`, `RestartOutcome.cs` (`AttachRequired`→`Unproven`), `src/AiRaccoon/Setup/Cli/{CliArgs,CliCommandTree,CliOptionsExtensions,RootCliOptions}.cs`, `src/AiRaccoon/Setup/Cli/Commands/ServeCommands.cs`, `src/AiRaccoon/Hosting/Common/ServerConfig.cs` (delete `Attach`), `ExitCode.cs` if any doc line names `--attach`.
**AC:**
1. *C1*: a second `serve` against a **proven** ai-raccoon port attaches and exits 0 (`ReportAttachedAsync`, `NodeRunner.cs:272`) — watched-red: red today (exit-3 refusal, `NodeRunner.cs:88`).
2. *C2*: `serve` and bare `serve --restart` against a holder that cannot prove refuse exit 3 with remedy and **send no token** (`ServerRestart.cs:88` unreachable) — bare `--restart` cycles a proven server (red today: `RestartOutcome.AttachRequired`).
3. *C1/D5*: `--attach` is gone from both roots (`CliCommandTree.cs:59,65,138,645`) and from `ServerConfig`/`RootCliOptions`/`NodeCliOptions`; `CliArgs` arity fix kept (`CliArgs.cs:136`).
4. Foreign-listener behavior unchanged (exit 3, `RestartOutcome.Foreign`).
5. Watched-red gate: `NodeRunnerTests.Serve_WhenAProvenServerHoldsThePort_AttachesAndExitsZero`. Positive controls: `ServeRestartTests` foreign-listener refusal (green before and after — mutation: skip identify → red), `CliCommandTreeTests`/`CliArgsTests` arity suite kept green. Mutations: drop the proof in `CycleAsync` → FakeRaccoon receives `/shutdown`+token (red); re-add a silent `--attach` no-op → absence test red.

### P5 — Integration package (LAST): cross-package tests + docs
**Scope:** real-binary end-to-end across P1–P4 surfaces, and every doc the behavior change invalidates.
**Touched files:** `tests/AiRaccoon.Tests/E2E/BackendLaunchIdentityProofE2ETests.cs` (new), `tests/…/E2E/TokenPathMigrationE2ETests.cs` (new), `tests/…/E2E/NoBankE2ETests.cs` (new), reworked `E2E/{ProxyLaunchE2ETests,ServeRestartE2ETests,ProxySpawnedBackendE2ETests,McpServerLaunchArgsE2ETests,McpTokenGateE2ETests}.cs`, `docs/adr/0105-private-spawn-is-the-launch-default.md` (revision: K1 → 2026-09-22 ruling; "Alternatives rejected → Mutual proof" marked overruled with the cryptographic-proof premise), `docs/adr/README.md`, `docs/SECURITY.md`, `docs/reference/agent-memory-server.md`, `docs/how-to/configure-ai-raccoon-server.md`, `docs/reference/logging-event-ids.md` (**re-measure** per constraint — P3's new EventId 690 and P4's 657 re-word).
**AC:**
1. *C1 end-to-end*: `ai-raccoon` (proxy) auto-starts a backend against a banked data root; a second invocation attaches to it (exactly one backend process); the settings verb attaches to the same one (red today).
2. *C2 end-to-end*: a squatter on `--port` receives zero secret bytes; the session completes over the private fallback (red today under the attach-or-start revert).
3. *F39 end-to-end*: `settings access show --data-root <empty>` exits 22 and the directory stays empty (red today).
4. *F49 end-to-end*: legacy top-level token migrates; a `--data-root` pointing at a scratch "repo" leaves no unignored 0600 file at the top level after a full start (red today).
5. Watched-red gates: `BackendLaunchIdentityProofE2ETests.AttachOrStart_WithAProvenBackend_AttachesInsteadOfSpawning` (fails at today's code on both halves of the suite); positive controls: `E2E_SquattedPort_NoSecretBytes_SessionStillCompletes`, `TokenPathMigrationE2ETests.Ensure_MovesTheLegacyTokenAndLeavesOneSecret`, full `ServeRestartTests` 11+/11.

---

## §D Parallelism map

```
P1 ──┬── P3 ── P4 ── P5
     └── P2 ────────┘        (P2 merges before P5)
```
- **P1 is the serial root** — it rewrites the four `McpTokenFile` constructor call sites (`BackendSessions.cs:45`, `CliSettingsBackend.cs:72`, `NodeRunner.cs:42`, `McpServerSetup.cs:83`) that P2–P4 then rework further.
- **P2 ∥ P3** — fully disjoint file sets (P2: `McpServerSetup.cs`, `McpTokenGate.cs`, new endpoint; P3: Proxy/Settings/`ProxyRunner`/`ConfigCommands`/`ExitCode`/new `IdentityProver`+`BankMissingException`/test helpers). P3's gates must not depend on P2's types: its test listeners answer `/identity` from a helper built on P1's `IdentityProof` primitive; the real-endpoint cross-checks land in P5.
- **P4 serialises after P3** — `ServerConfig.Attach`'s deletion (`ServerConfig.cs:23`) only compiles once P3 has stopped reading it (`BackendSessions.cs:56,63,71,183`); both also edit the `--attach` CLI surface.
- **P5 last**, after P2 has merged (its E2E crosses the endpoint and the client gates).
- Parallel dispatches each name their own `isolation` (worktrees), and per `announce-parallel-work`: each lane announces start/PR/merge on the project bus.

---

## §E Complete test list — designed before implementation

Every test: scratch data root, ephemeral/known-free ports, gate form `dotnet test --project tests/AiRaccoon.Tests --filter-class '*<Class>'`. Roles: **RED** = watched-red gate (fails at today's code), **PC** = positive control (must be green before *and* after), **M** = named mutation that must flip a gate red.

**P1** — `McpTokenFileTests` (Integration/Setup/Serve, extended):
1. `EnsureAsync_ProjectScope_MintsInTheBankStateDirectory_NotTheDataRootTopLevel` — **RED** — F49 unignored secret — M: `Path = Combine(dataRoot, …)`.
2. `EnsureAsync_UserScope_MintsAtTheDataRootWhichIsItsBankDirectory` — **PC** — scope-regression.
3. `Read_FallsBackToTheLegacyTopLevelToken` — **PC→** (red at today in its "legacy" role after P1 re-orients `Read`) — upgrade window.
4. `EnsureAsync_MigratesALegacyToken_AndLeavesExactlyOneSecret` — **RED** — two drifting secrets — M: drop migration → two files.
5. `EnsureAsync_NeverMintsAtTheLegacyPath` — **RED** — fail-closed direction — M: mint fallback → red.
6. `Read_UnparseableContentAtEitherPath_IsAbsent` — **PC** — debris-as-secret.

`McpIdentityFileTests` (new, Integration/Setup/Serve):
7. `EnsureAsync_MintsA256BitEcdsaPkcs8PemKey_InTheBankStateDirectory_0600` — **RED** — key sprawl — M: write top level.
8. `EnsureAsync_ConcurrentMintersConvergeOnOneKey` — **RED** — orphaned identity.
9. `EnsureAsync_WithAValidExistingKey_ReturnsItUnchanged` — **PC** — no silent rotation — M: re-mint on read → red.

`IdentityProofTests` (new, Unit/Hosting):
10. `SignThenVerify_RoundTripsUnderTheSameKey` — **PC**.
11. `Verify_RejectsATamperedNonce` — **RED-shaped** (fails if verify is vacuous) — forgery — M: verify always true → red.
12. `Verify_RejectsASignatureFromAnotherDataRootsKey` — **RED-shaped** — wrong-instance attach — M: single shared key → red.
13. `Verify_RejectsASignatureOverAnotherDomainLabel` — **RED-shaped** — cross-protocol oracle use.

**P2** — `IdentityProofEndpointTests` (new, Integration/Setup/Serve):
14. `PostIdentity_WithAValidNonce_AnswersAKeyFidAndASignatureThatVerifies` — **RED** — proof channel absent.
15. `PostIdentity_IsReachableWithoutAToken` — **RED** — proof impossible otherwise.
16. `PostIdentity_WithAMalformedNonce_400sNamingTheFieldAndRemedy` — **RED-shaped** — F53.
17. `PostIdentity_ResponseCarriesNoKeyMaterialBeyondThePublicKeyFid` — **RED-shaped** — secret leak.
`McpTokenGateTests` (extended):
18. `McpPath_StillRefusesWithoutToken_WhenIdentityEndpointIsOpen` — **PC** + **M** (add `/mcp` to `OpenPaths` → red).
19. `ShutdownPath_StillRefusesWithoutToken_WhenIdentityEndpointIsOpen` — **PC**.

**P3** — `BackendSessionsTokenExposureTests` (reworked):
20. `Acquire_WhenAProvenBackendAnswersTheConfiguredPort_AttachesInsteadOfSpawning` — **RED** — C1.
21. `Acquire_WithASquatterHoldingThePort_FallsBackPrivate_AndSendsZeroSecretBytes` — **RED** under the revert + **M** (drop proof → token at squatter).
22. `Acquire_WithASquatterThatAnswersIdentityWithGarbage_IsUnproven_AndSendsZeroSecretBytes` — **RED-shaped** — bluffing listener — M: trust HTTP 200 alone → red.
23. `Acquire_ThePrivateFallbackUrl_IsAlsoProvenBeforeTheTokenMoves` — **RED-shaped** — TOCTOU close — M: skip proof for children → red.
`CliSettingsTokenExposureTests` (reworked):
24. `AcquireAsync_WithASquatter_ProvesBeforeAnythingSecretMoves` — **RED** — measured F70 route — M: token-first ordering → red.
25. `AcquireAsync_WithASquatter_FallsBackToAPrivateBackend_WithTheN1Disclosure` — **RED-shaped** — D4+687.
`CliSettingsSharedBackendTests` / `BackendSessionsTests` / `CliSettingsBackendTests` (unit, extended):
26. `OpenAsync_ReadsTheTokenOnlyAfterAProof` — **RED-shaped** — ordering.
27. `AcquireAsync_WithAnEmptyDataRoot_RefusesNoBank_AndMintsNothing` — **RED** — F39 — M: drop pre-check → 4 files appear.
28. `AcquireBackend_WithAnEmptyDataRoot_RefusesNoBank_AndSpawnsNothing` — **RED** — F39 proxy twin.
29. `AcquireAsync_WithAnExistingBank_ProceedsNormally` — **PC**.
30. `BankMissingException_MapsToExitCodeNoBank_InBothCompositionRoots` — **RED-shaped** — F39's exit contract (`ProxyRunner.cs:29`, `ConfigCommands.cs:155`) — M: map to 6/18 → red.
`ProxyPrivateBackendLifetimeTests`:
31. `FallbackPrivateBackend_IsStillStoppedWithTheProxy` — **PC** — K1a.

**P4** — `NodeRunnerTests` (extended):
32. `Serve_WhenAProvenServerHoldsThePort_AttachesAndExitsZero` — **RED** — C1 revert — M: refuse again → red.
33. `Serve_WhenThePortHolderCannotProve_RefusesExit3NamingTheRemedy` — **RED** (today refuses without proof attempt) — C2/F53.
34. `Serve_WithAForeignListener_StillRefusesExit3` — **PC**.
`ServeRestartTests` (reworked, keep ≥11 green):
35. `RestartWithoutAttach_AgainstAProvenAiRaccoon_Cycles` — **RED** — bare `--restart` restored.
36. `Restart_AgainstAHolderThatCannotProve_SendsNoTokenAndRefuses` — **RED-shaped** + **M** (skip gate → FakeRaccoon receives `/shutdown`+token).
37. `Restart_AgainstARealServer_CyclesAndThePortFrees` — **PC** (invocation loses `--attach`).
`CliArgsTests` / `CliCommandTreeTests` (reworked):
38. `AttachOption_IsAbsentFromBothRoots` — **RED** (today present).
39. `ServeAttach_IsAnUnrecognizedArgument_NamingTheArgument` — **RED** — removal surfacing.
40. `BoolRootFlag_DoesNotConsumeTheNextToken` (arity fix kept) — **PC**.
`RestartTransitionTests`: 41. `MayBind_ExcludesUnproven` — **PC** (guards the enum rename).

**P5** — `BackendLaunchIdentityProofE2ETests` (new):
42. `ProxyThenSecondProxy_AttachesToOneBackend` — **RED** — C1 e2e.
43. `SettingsVerbThenProxy_ShareTheOneProvenBackend` — **RED** — C1 e2e.
44. `SquattedPort_NoSecretBytes_SessionCompletesOnThePrivateFallback` — **RED-shaped** — invariant e2e.
45. `FullStart_LeavesNo0600FileAtTheDataRootTopLevel` — **RED** — F49 e2e.
`NoBankE2ETests` (new): 46. `SettingsVerb_AgainstAnEmptyDataRoot_Exits22AndCreatesNothing` — **RED** — F39 e2e.
`TokenPathMigrationE2ETests` (new): 47. `Serve_WithALegacyTokenFile_MigratesIt_AndTheProxyStillAttaches` — **RED** — upgrade window.
`ServeRestartE2ETests`/`ProxyLaunchE2ETests`/`McpServerLaunchArgsE2ETests`/`ProxySpawnedBackendE2ETests`/`McpTokenGateE2ETests`: 48-52. path assertions re-pointed at the state directory, `--attach` invocations removed — **PC** role.
`LoggerMessageEventIdTests`: 53. registry still unique + `docs/reference/logging-event-ids.md` re-measured — **PC/gate**.

---

## §F Risks

1. **Mixed-version window** — an old `serve` (no `/identity`) behind the new CLI cannot prove: it gets no token and the CLI falls back to a private backend with a warning (surprising but fail-closed); a new `serve` migrates the token file away and an old CLI then reads nothing and fails closed ("holds no token"). Release note both directions.
2. **`/identity` is a signing oracle** — bounded: fixed 32-byte nonce input, domain-separated message, loopback-only (`McpServerSetup` Kestrel config binds `IPAddress.Loopback`); residual misuse requires local code execution in the user's own session anyway.
3. **`--attach` removal** breaks same-day scripts with exit 9/15 "Unrecognized … `--attach`"; named in release notes (`traceable-releases`), no VERSION edit on main.
4. **First-run UX change** — bare proxy on a bankless data root now exits 22 with a creation remedy instead of silently minting (this is F39's point, but it is a behavior change; the remedy names `ai-raccoon encryption …`, exact sub-verb confirmed against `EncryptionCommands` at implementation).
5. **Squatter persistence ⇒ backend sprawl** — every acquire against a held port spawns a private fallback (4h idle watchdog each, N1); the warning names the remedy (stop the listener); watchdog bounds the cost.
6. **F39 TOCTOU residual** — a bank deleted between pre-check and the child's first open would still mint in the child; narrow window, accepted here; a `serve --no-mint`-style flag is a possible follow-up, out of scope.
7. **ECDSA P-256 not Ed25519** — BCL constraint; wire carries an `algorithm` field is dropped (D3: minimal wire) — keyed by `keyId` + PEM metadata, so a later algorithm swap is a file-format bump.
8. **New `[LoggerMessage]` ids** (fallback warning ~690; 657 re-word) — `docs/reference/logging-event-ids.md` re-measure is a merge gate of P5 (constraint).
9. **Amended gate assertions** (§A amendment) — "zero requests to a squatter" becomes "zero secret bytes + nonce-only challenge" because attach-or-start necessarily dials the port (`ServerProbe.cs:55-58` already does today); recorded so the invariant is claimed honestly.

**Stop-condition check:** every ruling clause (C1, C2, C3, F49, F39) maps to ≥1 package AC in the table below; every package names a gate that fails at today's code (P1 #4, P2 #14, P3 #20/#24/#27-28, P4 #32, P5 #42/45/46); every unknown D1–D6 has exactly one recommendation.

| ruling clause (incl. F49, F39) | package | acceptance criterion |
|---|---|---|
| C1 revert to attach-or-start (launch only if none running) | P3 | AC1: a proven backend on `--port` is attached to; no second backend spawns (watched-red `…Acquire_WhenAProvenBackendAnswers…`) |
| C1 revert to attach-or-start | P4 | AC1: second `serve` on a proven port attaches and exits 0 (watched-red `NodeRunnerTests.Serve_WhenAProvenServerHoldsThePort_AttachesAndExitsZero`) |
| C1 revert to attach-or-start | P5 | AC1: two proxy launches + a settings verb share one auto-started backend (E2E, red today) |
| C2 proof of identity via private key before any secret reaches a listener | P1 | AC3: `McpIdentityFile` mints a 0600 ECDSA P-256 key; `IdentityProof.Verify` accepts only that data root's key over the domain-separated nonce |
| C2 proof of identity | P2 | AC1-2: `POST /identity` answers a verifier nonce with a verifying signature, reachable pre-token while `/mcp`+`/shutdown` stay gated |
| C2 proof of identity (security invariant) | P3 | AC2-3: zero secret bytes to any unproven listener (squatter or bluffing), token read only after verified proof — every URL, children included |
| C2 proof of identity (security invariant) | P4 | AC2: unproven port holder gets no token from `serve`/`serve --restart`; bare restart cycles only a proven server |
| C2 proof of identity (security invariant) | P5 | AC2: E2E squatted-port run — zero secret bytes, session completes on the private fallback |
| C3 protocol freedom (custom non-MCP channel) | P2 | AC1: the custom `/identity` challenge channel on the existing loopback listener (not MCP wire) carries the proof |
| F49 token + identity key in the bank state directory | P1 | AC1-2: both files mint only in `BankDirectoryFor(options)`; legacy token read-fallback + migrate-on-ensure, one surviving secret, fail-closed debris |
| F49 token + identity key in the bank state directory | P5 | AC4: full start leaves no 0600 file at the data-root top level; legacy token migrates end-to-end |
| F39 mistyped `--data-root` never mints a bank outside doctor | P3 | AC4: empty data root on either auto-start channel ⇒ exit 22 (`NoBank`) with creation remedy, directory untouched (watched-red ×2) |
| F39 mistyped `--data-root` never mints a bank outside doctor | P5 | AC3: E2E `settings access show` against an empty root exits 22 and creates nothing |