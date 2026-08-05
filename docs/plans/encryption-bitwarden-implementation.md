# Encryption — Bitwarden key source via bws CLI — Implementation Plan

**Task:** `encryption-bitwarden`
**Source design:** `docs/features/encryption-bitwarden/spec.json` (manifest, 9 ruled cards D1–D9, 1 deferred D10) + `docs/features/encryption-bitwarden/encryption-bitwarden.feature` (behavioral contract — **8 rules / 15 scenarios**; the task brief said 14/9, the file on disk carries 15/8 — this plan covers every scenario in the file)
**Project:** AiRaccoon — C# .NET 10 MCP server over sqlite-memory
**Worktree:** `.ai-badger/worktrees/encryption-bitwarden` (branch `task/encryption-bitwarden`) — all work happens here
**Date:** 2026-08-05

---

## 1. Overview

Add a second encryption key source to the memory bank: Bitwarden Secrets Manager via the **bws CLI** (shell-out to `bws secret get <id>`), on top of the kept default `AIRACCOON_DB_PASSPHRASE` env path. The secret's VALUE is an unencrypted **ed25519 SSH private key**; the raw SQLCipher key is derived as `SHA-256("ai-raccoon-db-key/v1" ‖ seed)` → `x'<64hex>'` (measured in `docs/work/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md` F2). RSA and passphrase-protected keys are rejected. Server startup refuses loudly when the configured source cannot produce a key (no cached copy). CLI surface: `ai-raccoon encryption bitwarden` (interactive config + validation + rekey + rotation warning), `encryption show`, `encryption unset`.

**Pass condition:** every scenario of `encryption-bitwarden.feature` passes — in a Reqnroll suite wired like the file-watcher one (S5) plus the unit/integration tests the spec's nonFunctional section explicitly keeps (derivation determinism, wrong-key rejection, provider errors, config flow).

### Deliverables map

| # | Deliverable | Lands in |
|---|---|---|
| 1 | Core crypto: ed25519 OpenSSH-key parser (format decode) + labelled SHA-256 derivation + typed errors + settings-key constants | `src/AiRaccoon.Core/Encryption/` (new) |
| 2 | Pre-open source selection: `memory.db.source` sidecar + bws process runner (fake-able seam) | `src/AiRaccoon.Infrastructure/Sqlite/` (new) |
| 3 | Provider family: `BitwardenEncryptionKeyProvider`, `SourceSelectingEncryptionKeyProvider`, factory rekey/verify helpers, DI + Program wiring incl. eager startup open (refuse-to-start) | `src/AiRaccoon.Infrastructure/Sqlite/`, `src/AiRaccoon/Setup/`, `src/AiRaccoon/Program.cs` |
| 4 | CLI: `encryption bitwarden` / `show` / `unset` (interactive, `-t` per-run, rotation warning, rekey) | `src/AiRaccoon/Setup/` |
| 5 | Integration: fake-`bws` executable end-to-end (PATH-free via injected executable path) | `tests/AiRaccoon.Tests/Integration/` |
| 6 | Reqnroll suite for the feature file (the 15-scenario acceptance contract) | `tests/AiRaccoon.Tests/BDD/` + csproj link |
| 7 | Full-suite gate + README/SECURITY doc updates | — |

---

## 2. Design facts already ruled (cards — do NOT re-decide)

- **D1** `AIRACCOON_DB_PASSPHRASE` STAYS as the default source (single-channel ruling's documented exception).
- **D2** bws CLI, not the SDK: provider shells out to `bws secret get <id>` at bank open.
- **D3** Secret value = unencrypted ed25519 SSH private key; derived `SHA-256("ai-raccoon-db-key/v1" ‖ seed)` → `x'<64hex>'` raw SQLCipher key (no KDF, measured). RSA and passphrase-protected keys rejected.
- **D4** Offline behavior: refuse to start, loudly; no cached key copy.
- **D5** Rotation trap: web-UI rotation without `PRAGMA rekey` bricks the bank — config command warns.
- **D6** Config flow: (a) bws presence check with actionable install error; (b) collect project id + secret id (owner defaults `613165e6-7947-49e0-889b-b49d007c5b85` / `f1d3c8e5-5391-4aef-8611-b49d007c8702`); (c) optional `-t <token>` for runs without `BWS_ACCESS_TOKEN` — that run only, never persisted.
- **D7** Raw-key-file option REMOVED. Provider family = env + bitwarden; keychain/cloud stay documented future sources (D10).
- **D8** `encryption show` prints the current source (+ secret id when bitwarden); `encryption unset` returns to the env default.
- **D9** `IEncryptionKeyProvider` becomes a source-selectable family; the source SELECTION must be resolvable pre-open (settings table is inside the encrypted bank). **This plan pins the D9 implementation shape — see §4.** Everything else in D9 (settings keys `encryption.source`, `encryption.bitwarden.projectId`, `encryption.bitwarden.secretId`) is fixed.

---

## 3. Codebase review findings the plan is grounded in

Read (not guessed): `IEncryptionKeyProvider.cs`, `EnvEncryptionKeyProvider.cs`, `SqliteConnectionFactory.cs`, `SqliteEncryptionInit.cs`, `SqliteMemoryStore.cs` (connection lifetime), `Setup/{CliArgs,ConfigCommands,Dependencies,ServerConfig}.cs`, `Program.cs`, `tests/Unit/Setup/{FakeConfigStore,ConfigCommandsRetrievalSweepSyncTests}.cs`, `tests/Unit/storage/SqliteConnectionFactoryEncryptionTests.cs`, `tests/BDD/{Hooks,MemoryFeatureContext,FileWatcherFeatureContext}.cs`, `AiRaccoon.Tests.csproj`, plus the two design records (`docs/work/2026-08-05-db-passphrase-options.md` incl. F3/F5/F14/F27, and `docs/work/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md` incl. F2/F4).

1. **The `Password` channel already carries raw keys — zero factory changes for keying.** `SqliteConnectionFactory.OpenBankAsync` (L37–46) puts whatever `IEncryptionKeyProvider.GetPassphrase()` returns into `csb.Password`. Measured (options report F3, ssh report F2): `Password = "x'<64hex>'"` keys the bank with NO KDF through e_sqlite3mc; create/reopen/wrong-key-rejection/rekey all verified. **The provider family only changes what string the interface returns.**
2. **Providers are hardwired in two places.** `Setup/Dependencies.cs:27-28` registers `EnvEncryptionKeyProvider`; `Program.cs:20` hardcodes `new EnvEncryptionKeyProvider()` for the verb path. Both must swap to the source-selecting resolver. Note the verb path shares the server's bank resolution (`config.Options`), so `--data-root`/`--install-scope` apply to config commands — the sidecar must resolve from the same `InfrastructureOptions`.
3. **The store opens short-lived connections per operation** (`await using var connection = await factory.OpenBankAsync(...)` everywhere in `SqliteMemoryStore`) — no long-lived connection blocks a rekey. BUT every open applies `PRAGMA journal_mode=WAL` (`OpenWithPragmasAsync` L67–69), and **SQLCipher rekey is not supported in WAL mode** (known SQLCipher constraint; e_sqlite3mc mirrors it). The rekey helper must open in DELETE journal mode, rekey, verify, close — the next normal open re-enables WAL. Pinned in S2b; TDD-first because the e_sqlite3mc-specific behavior must be measured, not assumed.
4. **Config-command conventions to mirror** (`ConfigCommands.cs`): switch on `commandPath` (L25–50); errors → stderr prefixed `ai-raccoon: …`, exit 1; results → stdout, exit 0; secrets prompted on stderr with empty-input abort (`SyncAddS3Async` L284–299 — the interactive precedent); warnings on stderr (`WatchSetEnabledAsync` L362). `CliArgs.cs`: `Verbs` array (L39) must gain `"encryption"` or `ContainsVerb` won't route it; tree added in `BuildFullRootCommand` (L42–53); all CLI text → the caller-supplied writer (stderr in `Program.cs`; `CliOutputRoutingTests` guards stdout).
5. **Config-command tests drive the real dispatch** (`ConfigCommandsRetrievalSweepSyncTests.Run` L15–27): `CliArgs.Parse(args)` → `ConfigCommands.RunAsync(path, parseResult, store, stdout, stderr, stdin, ct)` with `FakeConfigStore` and `StringWriter`s; stdin fed via `StringReader`. The new encryption commands need two more injectables (factory + bws runner) — added as **optional trailing parameters with `null` defaults** so every existing call site and test compiles unchanged.
6. **Wrong-key semantics already pinned by test:** `SqliteConnectionFactoryEncryptionTests.OpenBankAsync_WithWrongPassphrase_FailsToOpen` asserts `SqliteException` code 26 — the resolver-based wrong-key path reuses this exact behavior (feature scenario "A wrong key is rejected").
7. **Reqnroll precedent:** feature files are linked from `docs/features/...` via `<ReqnrollFeatureFiles>` in `AiRaccoon.Tests.csproj` (3 links already; add a 4th), steps bind through a per-scenario context registered in `BDD/Hooks.cs` (extend it, don't fork it). Tags `integration`/`slow`/`e2e` are non-parallelizable per `reqnroll.json`.
8. **Startup today is lazy:** nothing opens the bank until the first tool call. Refuse-to-start (D4) therefore requires an **eager open in `Program.cs`** before `app.RunAsync()` — new behavior, safe for the env path (open succeeds trivially), and it is what makes "server exits with an actionable error" real.
9. **TreatWarningsAsErrors** is on (`Directory.Build.props`); bare `dotnet build` / `dotnet test` from the worktree root are the canonical gates. Pitfall (ai-raccoon-pitfalls): `dotnet test` piped through an agent harness can stall — redirect to a file (`dotnet test > /tmp/x.log 2>&1`). Worktree test runs need `Models/{model_qint8_arm64.onnx,vocab.txt}` copied from the main checkout only if embedding tests run — the encryption sections' filters never touch them, but the S6 full suite does (provision first, gitignored).
10. **E2E harness exists** (`tests/E2E/McpServerFactory.cs`, WebApplicationFactory boots the entry point) but startup-exception surfacing through it is unverified; refusal-to-start is asserted at resolver/factory level (deterministic) in S2b/S4, with a process-level check as manual verification (§8). The E2E-level assertion is an optional stretch, not a gate.

---

## 4. THE PRE-OPEN SOURCE-SELECTION PROBLEM (card D9) — pinned decision

**Problem.** At every start (server or config verb) the factory asks `IEncryptionKeyProvider.GetPassphrase()` *before* the bank opens. With two sources, it must know which provider to ask. The natural home for configuration — the settings table — is *inside* the encrypted bank and unreadable until the key exists. Circular (research F4/F23: same circularity that rules out storing the passphrase in the DB).

**Candidates evaluated.**

- **(a) Small unencrypted sidecar next to `memory.db`** (e.g. `memory.db.source`), written by the encryption commands, read pre-open. Explicit, single-writer, zero new client-side config, clean error attribution ("sidecar says bitwarden → bws failed" vs "no sidecar → env"). Holds only non-secret metadata (source name + ids — no token, no key).
- **(b) Env override** (e.g. `AIRACCOON_DB_KEY_SOURCE=bitwarden`). No new file, but: re-introduces per-client config — the exact friction the Bitwarden feature exists to remove (users put env in every `.mcp.json`); two sources of truth (env vs settings) that can disagree; silent breakage when a client forgets the var (bank opens with the env passphrase → confusing mismatch).
- **(c) Derive-from-bank-presence heuristics** (try env key, fall back to bws on failure). No state at all, but: probing opens mask real state (a stale env passphrase silently wins; a bank never rekeyed silently ignores the bitwarden config), muddles error attribution ("wrong key" vs "bws down" after a double-open), and doubles startup latency on the failure path. The classic "simpler-looking" shape that is actually more complex.

**Decision: (a) — an unencrypted sidecar `memory.db.source` next to the bank.** Absence = env (the existing zero-setup path is byte-for-byte unchanged). Rationale: it is the only candidate that is explicit (no probing, no ambiguity), single-writer (written only by `encryption bitwarden` / removed by `encryption unset`), requires **zero** changes to client configs (the feature's whole point), and gives D4 its loud, attributable failures. "Ask if a simpler shape would do": (b) and (c) were each considered against the invariants — both fail "explicit state, clean failure" for marginal file savings. The sidecar holds no secret material (ids are not secrets; the token comes from the user's own `BWS_ACCESS_TOKEN` or the per-run `-t`; the key is fetched, not stored).

### What lives where (pinned)

| Data | Where | Read when | Written by |
|---|---|---|---|
| Source selection (env \| bitwarden) + projectId + secretId | **Sidecar** `<bankDir>/memory.db.source` (plaintext JSON, non-secret) | **pre-open** (resolver) | `encryption bitwarden` (write) / `encryption unset` (delete) |
| Same three fields (mirror) | Settings table: `encryption.source`, `encryption.bitwarden.projectId`, `encryption.bitwarden.secretId` | **post-open** (`encryption show`, future tools) | same commands, same transaction-ish moment |
| Passphrase (env default) | `AIRACCOON_DB_PASSPHRASE` | pre-open (resolver fallback) | user |
| Token | `BWS_ACCESS_TOKEN` (user's env, read by bws itself) or per-run `-t` on the config command | pre-open (bws child process) | user; **never persisted by ai-raccoon** |
| Raw key | Bitwarden only; in server memory after fetch; never on disk | pre-open (bws fetch → derive) | — |

The provider needs **secretId pre-open**, so the sidecar must carry it (it cannot live only in settings — that was the trap). Settings mirror the sidecar for post-open display; `encryption show` reads settings first and falls back to the sidecar when the rows are missing (crash-window self-description).

### Crash-window semantics (pinned)

Persist order inside `encryption bitwarden`: **rekey → sidecar write → settings write**. If the process dies between rekey and sidecar, the bank is derived-keyed while the sidecar still says env; the command is **self-healing**: it opens with the current source, and on an encryption mismatch retries opening with the already-fetched derived key — if that succeeds the bank is already derived-keyed and rekey is skipped, then persistence completes. Corrupt/unreadable sidecar → **loud startup error** (never silent env fallback — silent fallback would open with the wrong key and surface as a confusing mismatch).

---

## 5. Fixed contracts (do NOT deviate — tests and BDD assert these exact strings/keys)

### 5.1 Derivation (stability contract — changing it silently breaks existing banks)

```
label   = "ai-raccoon-db-key/v1"          (UTF-8)
raw     = SHA-256(label ‖ seed)            (seed = the ed25519 32-byte seed)
key     = "x'" + raw.ToLowerInvariant-hex + "'"
```

Pinned test vectors (computed 2026-08-05, macOS; implementer hard-codes these in tests):

| Seed | Derived `Password` string |
|---|---|
| `00 01 02 … 1e 1f` (synthetic fixture) | `x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'` |
| `6868227276d58fd3a3c67be90bad5cb2cc53ee5f46ec3e03ba483b1eaff2d7e0` (real `ssh-keygen -t ed25519` key) | `x'5944055840d8941e4cb6d5bb0dedfb3e1808bb7b33727107de0e9399054ee83d'` |

Parser validation rules (RFC 8709 / OpenSSH `PROTOCOL.key`, all **format decoding — no hand-rolled crypto**; the seed-vs-pub check is a byte comparison, not a point multiply):
- PEM frame `-----BEGIN OPENSSH PRIVATE KEY-----` … base64 … `-----END…-----`; decode; magic `openssh-key-v1\0`.
- `ciphername` must be `"none"` → else **passphrase-protected**: error `passphrase-protected keys are not supported` (measured: protected keys carry `ciphername=aes256-ctr kdfname=bcrypt`).
- key type must be `ssh-ed25519` → else **RSA/unsupported**: error `only ed25519 keys are supported` (RSA = `ssh-rsa`).
- checkint pair equal; private field = 64 bytes; embedded pub (bytes 32–63 of the private field) **byte-equals** the public-key blob's key — guards malformed values.
- Any structural failure → `malformed OpenSSH private key: <detail>`.

### 5.2 Settings keys + sidecar format

| Key | Value |
|---|---|
| `encryption.source` | `"env"` \| `"bitwarden"` (mirror of sidecar) |
| `encryption.bitwarden.projectId` | UUID |
| `encryption.bitwarden.secretId` | UUID |
| Sidecar `<bankDir>/memory.db.source` | `{"source":"bitwarden","projectId":"<uuid>","secretId":"<uuid>"}` — JSON, atomic write (temp + `File.Move` overwrite); **absence = env**; corrupt → loud error naming the path |

### 5.3 bws invocation forms (pinned)

| Context | Command | Timeout | Notes |
|---|---|---|---|
| Presence check (`encryption bitwarden` step a) | `bws --version` | 5 s | `FileNotFoundException` → "bws not found…" |
| Reachability validation (`encryption bitwarden` step c) | `bws secret get <secretId> -t <token>` (only when `-t` given) | 15 s | `-t` on argv is explicitly allowed by D6 ("beyond the user's own -t flag"); never persisted |
| Server start / config-verb bank open (provider) | `bws secret get <secretId>` | 15 s | no `-t`; token comes from `BWS_ACCESS_TOKEN` inherited by the child |

Process seam: `IBwsProcessRunner` (executable path injectable for tests — default `"bws"`, so no PATH mutation in tests) → `BwsProcessRunner` (Process.Start, redirected output, `WaitForExitAsync` + timeout). Non-zero exit → `bws failed (exit <n>): <stderr-first-line>`; timeout → `bws timed out after 15s`.

### 5.4 Error / output message shapes (BDD asserts substrings)

- Config, bws missing: `ai-raccoon: bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)` → exit 1, no state change.
- Config, unreachable/bad secret: `ai-raccoon: bws failed (exit <n>): <stderr>` (or timeout text) → exit 1, no state change.
- Server start, bws missing / network / bad secret: same texts from the provider, printed by the `Program.cs` startup wrapper → exit 1.
- Server start, wrong key (source=bitwarden): `encryption mismatch: the bank cannot be opened with the bitwarden key — if the secret was rotated, the bank must be rekeyed (run 'ai-raccoon encryption bitwarden')`.
- Rotation warning (stderr, config success path): `warning: rotating the secret in the Bitwarden UI without PRAGMA rekey bricks the bank — rotate the secret and rekey the bank together`.
- `encryption show`: `source: env` / `source: bitwarden` + `projectId: <id>` + `secretId: <id>`.
- `encryption unset` success: `encryption source reset to env`; when the bank stays on the derived key (no env passphrase to rekey back to): loud stderr warning.

---

## 6. Section decomposition & parallelism

### Parallelism waves

```
Wave 1 (parallel):  S1  ∥  S2a          — no shared files (both are new-file sections)
Wave 2 (serial):    S2b                  — needs S1 (parser/derivation) + S2a (sidecar/runner)
Wave 3 (parallel):  S3  ∥  S4            — S3 needs S1+S2 public surface; S4 needs S1+S2a (real fake-bws executable); no shared files
Wave 4 (serial):    S5                   — needs everything merged
Wave 5 (serial):    S6                   — full gate + docs
```

### File-collision matrix (sections sharing a file MUST NOT run concurrently)

| File | Owned by |
|---|---|
| `src/AiRaccoon.Core/Encryption/*` (new) | **S1 only** |
| `tests/AiRaccoon.Tests/Unit/Encryption/{SshKeyDerivationTests,OpenSshPrivateKeyParserTests}.cs` (new) | **S1 only** |
| `src/AiRaccoon.Infrastructure/Sqlite/{EncryptionSourceFile,IBwsProcessRunner,BwsProcessRunner}.cs` (new) | **S2a only** |
| `tests/AiRaccoon.Tests/Unit/Encryption/{EncryptionSourceFileTests,BwsProcessRunnerTests}.cs` (new) | **S2a only** |
| `src/AiRaccoon.Infrastructure/Sqlite/{BitwardenEncryptionKeyProvider,SourceSelectingEncryptionKeyProvider}.cs` (new), `SqliteConnectionFactory.cs` (add `BankPathFor` static + `RekeyBankAsync` + `OpenBankWithKeyAsync`) | **S2b only** |
| `src/AiRaccoon/Setup/Dependencies.cs` | **S2b only** |
| `tests/AiRaccoon.Tests/Unit/Encryption/{BitwardenEncryptionKeyProviderTests,SourceSelectingEncryptionKeyProviderTests}.cs`, `tests/AiRaccoon.Tests/Unit/storage/SqliteConnectionFactoryEncryptionTests.cs` (extend) | **S2b only** |
| `src/AiRaccoon/Program.cs` | **S2b edit 1** (resolver for verb path + eager startup open + error mapping) → **S3 edit 2** (pass encryption deps to `RunAsync`). Sequential waves — never concurrent. |
| `src/AiRaccoon/Setup/{CliArgs.cs,ConfigCommands.cs}` + new `EncryptionCommands.cs`, `tests/AiRaccoon.Tests/Unit/Setup/{ConfigCommandsEncryptionTests.cs (new),CliArgsTests.cs (extend)}` | **S3 only** |
| `tests/AiRaccoon.Tests/Integration/EncryptionBitwardenIntegrationTests.cs` (new) | **S4 only** |
| `tests/AiRaccoon.Tests/BDD/{EncryptionBitwardenFeatureContext.cs,EncryptionBitwardenSteps.cs}` (new), `BDD/Hooks.cs` (extend), `AiRaccoon.Tests.csproj` (feature link) | **S5 only** |
| `README.md`, `src/AiRaccoon/README.md`, `SECURITY.md` | **S6 only** |

No two waves touch the same file concurrently. `Program.cs` has two sequential edits (S2b, then S3). `ConfigCommands.RunAsync` gains optional parameters in S3 (S2b does NOT touch its signature — Program's existing call compiles unchanged until S3 updates it).

### Worktree etiquette (single shared worktree — from the file-watcher plan, binding here)

- Code edits may be parallel; **`dotnet build` / `dotnet test` never run concurrently** — the orchestrator runs every gate serially (shared `obj/`/`bin/`). Agents report "code done, gate pending" rather than racing the build.
- Commits are per-path: `git add <specific files>` — **never `git add -A` / `git add .`**. Failing tests committed before production code, per section (TDD).
- Gates run from the worktree root with the targeted filters below; the full build+test runs in S6 (and once after each wave merge, by the orchestrator).

---

## Section S1 — Core crypto: OpenSSH parser + derivation

**Wave 1. Parallel with S2a. New files only.**

### Scope
- `src/AiRaccoon.Core/Encryption/SshKeyDerivation.cs` — `const string Label = "ai-raccoon-db-key/v1"`; `static string DeriveRawKey(ReadOnlySpan<byte> seed)` → `"x'<64 lowercase hex>'"` (SHA-256 via `System.Security.Cryptography`). Pure, no I/O.
- `src/AiRaccoon.Core/Encryption/OpenSshPrivateKeyParser.cs` — `static byte[] ParseSeed(string pem)` implementing §5.1's validation; **format decoding only** (no crypto beyond the byte comparisons; derivation stays in `SshKeyDerivation`).
- `src/AiRaccoon.Core/Encryption/EncryptionKeyException.cs` — base + `PassphraseProtectedKeyException`, `UnsupportedKeyTypeException`, `MalformedPrivateKeyException` (messages per §5.4).
- `src/AiRaccoon.Core/Encryption/EncryptionSourceConfig.cs` — record `(string Source, string? ProjectId, string? SecretId)`.
- `src/AiRaccoon.Core/Encryption/EncryptionSettingsKeys.cs` — `Source = "encryption.source"`, `ProjectId = "encryption.bitwarden.projectId"`, `SecretId = "encryption.bitwarden.secretId"`, `SourceEnv = "env"`, `SourceBitwarden = "bitwarden"`. **Contract with S2b/S3/S5 — changing these breaks integration.**

### TDD order (failing tests first, each committed before its production code)
1. `Unit/Encryption/SshKeyDerivationTests.cs`: assert the two §5.1 vectors byte-exactly (`x'277bf7…'` for seed `00..1f`, `x'594405…'` for the real-key seed); assert lowercase; assert a wrong label (`ai-raccoon-db-key/v2`) produces a different key (pins the stability contract).
2. `Unit/Encryption/OpenSshPrivateKeyParserTests.cs`, with an in-test **synthetic PEM builder** (C# helper that assembles the openssh-key-v1 binary from seed `00..1f` + synthetic pub bytes `01..20` — deterministic, no real key material):
   - parses the synthetic key → seed `00..1f`;
   - passphrase-protected PEM (builder variant with `ciphername=aes256-ctr`/`kdfname=bcrypt`) → `PassphraseProtectedKeyException` with the §5.4 text;
   - RSA PEM (builder variant with `ssh-rsa` keytype) → `UnsupportedKeyTypeException`;
   - malformed: bad magic, truncated body, checkint mismatch, embedded-pub ≠ pubkey field, bad base64 → `MalformedPrivateKeyException` each;
   - real-key integration of the parser: a fixture generated once with `ssh-keygen -t ed25519 -N ''` (recipe in §5.1; hard-code the derived constant from the plan — do NOT recompute it in the test).

### Acceptance criteria
- Derivation is deterministic, label-pinned, and returns the exact `x'<64hex>'` form the `Password` channel needs (research F2/F3 measured).
- Every §5.1 rejection rule has a unit test with the exact §5.4 message.
- Core/Encryption is infrastructure-free (no `Process`, no `Microsoft.Data.Sqlite`).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~AiRaccoon.Tests.Unit.Encryption"
```

---

## Section S2a — Sidecar + bws process runner

**Wave 1. Parallel with S1. New files only.**

### Scope
- `src/AiRaccoon.Infrastructure/Sqlite/EncryptionSourceFile.cs` — `EncryptionSourceFile(string bankPath)`; `static string PathFor(string bankPath)` → `bankPath + ".source"`; `EncryptionSourceConfig? Read()` (null = absent; corrupt JSON → `EncryptionSourceException` naming the path); `void Write(EncryptionSourceConfig)` (atomic: temp file + `File.Move(overwrite)`); `void Delete()`.
- `src/AiRaccoon.Infrastructure/Sqlite/IBwsProcessRunner.cs` — `BwsResult Run(string executable, IReadOnlyList<string> args, TimeSpan timeout)` → `(int ExitCode, string Stdout, string Stderr)`; throws `BwsInvocationException` on `FileNotFoundException` ("bws not found …" per §5.4) and on timeout ("bws timed out after 15s").
- `src/AiRaccoon.Infrastructure/Sqlite/BwsProcessRunner.cs` — Process.Start with redirected output, `WaitForExitAsync(timeout)`; executable path constructor-injectable (default `"bws"`) so tests pass an absolute fake path — **no PATH mutation in tests**.

### TDD order
1. `Unit/Encryption/EncryptionSourceFileTests.cs` (temp dirs): absent → null; write/read round-trip; overwrite; delete; corrupt JSON → loud exception with path; atomicity (no partial file after a simulated write).
2. `Unit/Encryption/BwsProcessRunnerTests.cs`: real process on a benign executable (e.g. `/bin/echo`) → exit 0 + stdout; non-zero exit + stderr surfaced; nonexistent executable → `BwsInvocationException` with the §5.4 "bws not found" text; timeout path (runner with a tiny timeout against a sleeping script) → timeout text.

### Acceptance criteria
- Sidecar + runner are pure file/process mechanics, fully unit-tested, no SQLite involvement.
- The runner seam is the fake point for every bws failure branch in S2b/S3/S5.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~AiRaccoon.Tests.Unit.Encryption"
```

---

## Section S2b — Provider family, resolver, factory rekey, startup wiring (refuse-to-start)

**Wave 2. Depends on S1 + S2a. Owns the wiring files (see matrix).**

### Scope
- `BitwardenEncryptionKeyProvider(IBwsProcessRunner runner, string secretId) : IEncryptionKeyProvider` — `GetPassphrase()`: run `bws secret get <secretId>` (15 s, no `-t`); exit≠0 → §5.4 bws-failed error; stdout → `OpenSshPrivateKeyParser.ParseSeed` (typed errors pass through) → `SshKeyDerivation.DeriveRawKey` → return the `x'…'` string.
- `SourceSelectingEncryptionKeyProvider(InfrastructureOptions options, IEncryptionKeyProvider envProvider, IBwsProcessRunner runner) : IEncryptionKeyProvider` — reads the sidecar **fresh on every call** (the config command changes it between calls); `string? SourceName` (sidecar source or `"env"`); absent sidecar or `env` → `envProvider.GetPassphrase()`; `bitwarden` → the bitwarden provider (secretId from the sidecar). `SqliteConnectionFactory` gains `static string BankPathFor(InfrastructureOptions)` (extract L22–31) used by both the resolver and the factory.
- `SqliteConnectionFactory` additions: `OpenBankWithKeyAsync(string key, ct)` (raw connection with `Password=key` + the existing pragmas — the command's fallback probe and rekey verification); `RekeyBankAsync(string newKey, ct)` — open with the **current provider key** via a DELETE-journal connection (rekey is unsupported in WAL mode — pinned §3.3), build the literal via `SELECT quote($newKey)` (the exact Microsoft.Data.Sqlite quoting mechanism, measured F3 — handles raw `x'…'` strings AND passphrases), `PRAGMA rekey = <quoted>`, close, verify by reopening with `newKey` (throws `SqliteException` code 26 on mismatch, matching the existing wrong-key test).
- `Setup/Dependencies.cs:27-28` → register `SourceSelectingEncryptionKeyProvider` (env provider + runner injected); register after the factory (`BankPathFor` needs only options, so ordering is free).
- `Program.cs` **edit 1**: verb path builds the store with the resolver (not `EnvEncryptionKeyProvider`); server path adds the **eager startup open** — after `ConfigureMcpEndpoints`, before `RunAsync`:
  ```csharp
  try { await using var probe = await app.Services.GetRequiredService<SqliteConnectionFactory>().OpenBankAsync(); }
  catch (SqliteException) when (resolver.SourceName == "bitwarden")
  { Console.Error.WriteLine("ai-raccoon: encryption mismatch: …" /* §5.4 */); return 1; }
  catch (Exception ex) { Console.Error.WriteLine($"ai-raccoon: {ex.Message}"); return 1; }
  ```
  (all provider exceptions already carry §5.4 text; the SqliteException branch adds the rotation hint).

### TDD order
1. `Unit/Encryption/BitwardenEncryptionKeyProviderTests.cs` (fake runner): canned valid PEM → returns the exact derived `x'…'`; canned RSA PEM → RSA error; canned encrypted PEM → passphrase error; garbage stdout → malformed error; exit 1 + stderr → bws-failed text; timeout → timeout text; **asserts the runner was invoked with exactly `["secret","get",<secretId>]` and no `-t`**.
2. `Unit/Encryption/SourceSelectingEncryptionKeyProviderTests.cs` (temp root + stub env provider + fake runner): no sidecar → env stub's value (incl. null → unencrypted); sidecar `env` → env stub; sidecar `bitwarden` → fake-runner fetch + derived string; corrupt sidecar → loud error; `SourceName` correctness.
3. `Unit/storage/SqliteConnectionFactoryEncryptionTests.cs` additions (real temp banks, real e_sqlite3mc):
   - `RekeyBankAsync` env-passphrase bank → derived raw key → **reopens with the derived key** (feature scenario "reopens with the derived key after rekey"; also closes the research "Still open" item);
   - rekey from unencrypted (no passphrase) → derived raw key → reopens (fresh-bank path of the config command);
   - `OpenBankWithKeyAsync` with a wrong key → `SqliteException` code 26;
   - resolver-wired wrong-key rejection: bank keyed with key A, provider returns key B → open fails code 26 (feature scenario "A wrong key is rejected").
4. Startup smoke (existing harness style): Dependencies registers the resolver; eager-open path exercised by the S4 integration suite.

### Acceptance criteria
- Feature scenarios 1 (env default unchanged), 9 (bws missing at start), 10 (network failure at start), 11 (reopens after rekey), 12 (wrong key) all pinned at factory/resolver level with exact §5.4 messages.
- `SqliteConnectionFactory` keying path unchanged for the env provider (existing tests untouched and green).
- No cached/offline key copy exists anywhere (D4) — assert nothing writes key material next to the bank in the rekey tests.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~AiRaccoon.Tests.Unit.Encryption|FullyQualifiedName~SqliteConnectionFactoryEncryptionTests"
```
(plus orchestrator `dotnet build` after merge)

---

## Section S3 — CLI: `encryption bitwarden` / `show` / `unset`

**Wave 3. Parallel with S4. Depends on S1 + S2. Owns the CLI files (see matrix).**

### Scope
- `Setup/CliArgs.cs`: add `"encryption"` to `Verbs`; `EncryptionCommand()` — `bitwarden` (option `-t` `HelpName="token"`, description "access token for this run only — never persisted; defaults to BWS_ACCESS_TOKEN"), `show`, `unset`; register in `BuildFullRootCommand`.
- `Setup/ConfigCommands.cs`: `RunAsync` gains optional trailing params `SqliteConnectionFactory? bank = null, IBwsProcessRunner? bws = null` (existing call sites compile unchanged); dispatch line `["encryption", "bitwarden"] => EncryptionBitwardenAsync(...)`, `["encryption", "show"]`, `["encryption", "unset"]`.
- `Setup/EncryptionCommands.cs` (new): the three handlers, following the verb conventions (§3.4):
  - **bitwarden** (pinned order): (a) presence check `bws --version` 5 s → missing = §5.4 install error, exit 1, no change; (b) prompts on stderr — `project id [613165e6-…]` then `secret id [f1d3c8e5-…]`, empty input = owner default (ids are not secrets — the sync empty-abort rule applies to secrets only); (c) validation fetch `bws secret get <secretId> [-t <token>]` 15 s + parse + derive → any failure = §5.4 error, exit 1, **no state change** (feature: unreachable-secret scenario); (d) rotation warning to stderr (§5.4); (e) bank: `File.Exists(BankPath)` → try `bank.OpenBankAsync()`; on `SqliteException` retry `bank.OpenBankWithKeyAsync(derived)` (crash-window self-heal); if the current-source open succeeded → `bank.RekeyBankAsync(derived)` (internal verify-reopen); both opens fail → error, no change; (f) persist: write sidecar, then settings rows (`encryption.source/projectId/secretId` via the store — which now opens with the bitwarden key because the sidecar is already written); (g) stdout success line. Fresh bank (no file): skip rekey, persist (first server start creates the bank with the derived key).
  - **show**: open bank (resolver) → read `encryption.source` (fallback: sidecar) → print §5.4 lines (feature: "prints bitwarden with the secret id").
  - **unset**: open bank with current source → delete the three settings rows → rekey back: if `AIRACCOON_DB_PASSPHRASE` is set → `RekeyBankAsync(envPassphrase)`; if unset → leave the bank on the derived key and print the loud §5.4 warning; delete the sidecar LAST → stdout `encryption source reset to env`.
- `Program.cs` **edit 2** (S3): verb path passes `bank`/`bws` into `RunAsync`.

### TDD order (`FakeConfigStore` + fake runner + real factory on temp roots; `Run` helper mirroring `ConfigCommandsRetrievalSweepSyncTests` L15–27)
1. `CliArgsTests` additions: `encryption bitwarden|show|unset` parse with empty command paths; `-t` accepted; `-t` absent OK; unknown subcommand → parse error; help text renders.
2. `Unit/Setup/ConfigCommandsEncryptionTests.cs`:
   - bws missing → exit 1, §5.4 install text on stderr, **settings and sidecar unchanged**;
   - interactive flow with owner defaults (stdin = `\n\n`) → source persisted (sidecar + settings rows), stdout success, rotation warning on stderr;
   - non-default ids via stdin → persisted;
   - `-t <token>` → fake runner captured the token in the validation call only, **nothing persisted** (assert sidecar/settings contain no token);
   - unreachable secret (fake runner exit 1) → exit 1, bws stderr text, no state change;
   - malformed secret value at config → exit 1, malformed text, no change;
   - show: env (no rows) → `source: env`; bitwarden rows → `source: bitwarden` + secret id line;
   - unset: bitwarden rows + sidecar → rows gone, sidecar gone, stdout reset line; env-passphrase-set variant asserts rekey-back (real temp bank);
   - rotation warning asserted in the success path.
3. Real-bank flow (fake runner, temp root): env-keyed bank → `encryption bitwarden` → bank reopens with the derived key (via resolver); the re-run self-heal path (sidecar stale) covered here or in S4.

### Acceptance criteria
- Feature scenarios 2–5, 13, 14, 15 pass at command level with exact streams/exit codes.
- Secrets never on argv except the user's own `-t`; token never persisted; ids default to the owner's.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~ConfigCommandsEncryptionTests|FullyQualifiedName~CliArgsTests"
```

---

## Section S4 — Integration: fake-bws executable end-to-end

**Wave 3. Parallel with S3. Depends on S1 + S2a (+ S2b's wiring where merged). New file only.**

### Scope
`tests/AiRaccoon.Tests/Integration/EncryptionBitwardenIntegrationTests.cs` (traits `Integration`/`Slow`):
- Fixture: a temp dir with a fake `bws` shell script (recipe below) + `key.pem` (the synthetic §5.1 key; generate once via the test's own builder or a checked-in fixture). Tests construct `new BwsProcessRunner(Path.Combine(tempDir, "bws"))` — absolute path, **no PATH mutation**.
- Fake script (deterministic):
  ```sh
  #!/bin/sh
  # fake bws: `secret get <id>` prints the synthetic key; `--version` succeeds; else fails.
  if [ "$1" = "--version" ]; then echo "bws 1.0.0 (fake)"; exit 0; fi
  if [ "$1" = "secret" ] && [ "$2" = "get" ]; then cat "$(dirname "$0")/key.pem"; exit 0; fi
  echo "bws: not implemented: $*" >&2; exit 1
  ```

### TDD order (each starts failing)
1. Real temp bank keyed with the env passphrase → resolver with fake-bws runner → bank opens with the derived key (feature scenario 11, end-to-end through the real codec).
2. Wrong-key: bank keyed with key A, fake bws returns a different key → `SqliteException` code 26 (scenario 12).
3. Refusal: fake executable absent (runner pointed at a nonexistent path) → §5.4 "bws not found"; fake script exits 1 → bws-failed text; fake script sleeps past a short timeout → timeout text (scenario 9/10).
4. Config-command drive: `ConfigCommands.RunAsync` with the real factory + fake-bws runner on a temp env-keyed bank → sidecar + settings written, bank reopens with the derived key; `encryption unset` with `AIRACCOON_DB_PASSPHRASE` set → bank reopens with the env passphrase, sidecar gone.
5. Malformed secret (fake script emits garbage) → malformed error, bank unchanged.

### Acceptance criteria
- The fake-bws executable is the only "network"; every feature scenario touching bws is exercised against real SQLCipher banks.
- No test mutates the global environment (PATH, env vars are set/restored within the test body only where the scenario demands — unset's rekey-back reads the env passphrase; use try/finally).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~EncryptionBitwardenIntegrationTests"
```
(serialized by the orchestrator; `@slow`-class runtime expected)

---

## Section S5 — BDD Reqnroll suite (the feature file itself)

**Wave 4. Depends on S1–S4 merged. Owns `BDD/*` + csproj link.**

### Scope
- `AiRaccoon.Tests.csproj`: add `<ReqnrollFeatureFiles Include="..\..\docs\features\encryption-bitwarden\encryption-bitwarden.feature">` with a `BDD\features-encryption-bitwarden\` link (mirror the file-watcher entry, L45–47).
- `BDD/EncryptionBitwardenFeatureContext.cs` (new): temp data root; real `SqliteConnectionFactory` + `EncryptionSourceFile`; a switchable fake runner (`Installed`/`Missing`/`FailWith(stderr)`/`ReturnKey(pem)`/`Hang`); `FakeConfigStore` for the config scenarios; writers; `StringReader` stdin. Registered in `BDD/Hooks.cs` (extend — do not fork; the native-memory context registration stays).
- `BDD/EncryptionBitwardenSteps.cs` (new): bind the 15 scenarios:
  - "server opens the bank" steps drive `factory.OpenBankAsync()` (or `ConfigCommands.RunAsync` for verb scenarios) and assert §5.4 messages / exit codes;
  - derivation scenarios call `SshKeyDerivation`/parser directly with the §5.1 vectors;
  - `-t` scenario asserts the runner's captured args contain the token and that sidecar/settings contain none;
  - rotation-warning scenario asserts the stderr substring;
  - show/unset scenarios assert the §5.4 output shapes and store/sidecar state.

### TDD order
1. csproj link + context + steps skeleton → suite runs and fails (unbound/red steps are the failing contract).
2. Bind scenario-by-scenario; each scenario green only when its production path behaves per §5.

### Acceptance criteria
- **All 15 scenarios of the feature file pass** in the Reqnroll suite — the acceptance contract itself, not a re-implementation.
- The `.feature` and `spec.json` are never edited (they are the contract).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~EncryptionBitwarden"
```

---

## Section S6 — Full gate + documentation

**Wave 5. Serial.**

### Scope
1. Full gate from the worktree root: `dotnet build` (TreatWarningsAsErrors) then `dotnet test` (bare; provision `Models/` per §3.9 first). All existing suites must stay green — the encryption sections touch no MCP tools, no schema, no store internals, so the only expected ripple is `CliArgsTests` verb-surface additions (S3).
2. Docs (mirror the F18 audit list, narrowed to this feature): `README.md` encryption section (env default + `encryption bitwarden` flow + rotation trap + sidecar note), `src/AiRaccoon/README.md`, `SECURITY.md` — mention `memory.db.source` (non-secret), the `-t` per-run token, and that `BWS_ACCESS_TOKEN` is the server-start token channel.
3. Manual verification (documented in the plan's completion report): run the built binary against a temp data root with a bad sidecar → exits 1 with the §5.4 mismatch message (the process-level refusal check).

### Acceptance criteria
- Bare build + bare test green; docs describe the new surface and the D4/D5 traps; no leftover references to removed options (raw-key file stays absent).

### Quality gate
```
dotnet build
dotnet test
```
(both bare, from the worktree root, run serially by the orchestrator)

---

## 7. Risk coverage map & plan-blocking unknowns

### Feature scenarios → sections

| Scenario | Covered by |
|---|---|
| env default opens the bank | S2b (resolver test) + S5 |
| config fails with install guidance | S3 + S5 |
| interactive config persists ids | S3 + S5 |
| `-t` per-run, never persisted | S3 (arg capture) + S5 |
| unreachable secret id | S3 + S4 |
| ed25519 derives deterministically | S1 (vectors) + S5 |
| passphrase-protected rejected | S1 (parser) + S2b (provider) + S5 |
| RSA rejected | S1 + S2b + S5 |
| bws missing at start fails loudly | S2b + S4 + S5 |
| network failure at start, no cache | S2b (timeout/non-zero) + S4 + S5 |
| env-keyed bank reopens after rekey | S2b (factory) + S4 + S5 |
| wrong key rejected | S2b (code 26) + S4 + S5 |
| rotation warning printed | S3 + S5 |
| show prints source + secret id | S3 + S5 |
| unset returns to env | S3 + S4 + S5 |

### Unknowns (honest — each is closed by a TDD-first test, not assumed)
1. **`PRAGMA rekey` from a passphrase-keyed or plaintext bank onto a raw key through Microsoft.Data.Sqlite / e_sqlite3mc** — research F3 verified raw↔raw rekey only; the passphrase→raw and plaintext→raw legs are the S2b rekey tests (TDD-first). If e_sqlite3mc rejects a leg, the implementer adapts the rekey helper (journal-mode pin is already specified) — do NOT change the contract.
2. **Rekey in WAL mode** — pinned to DELETE-journal rekey (§3.3); the S2b tests prove it. 
3. **`encryption unset` rekey-back with no env passphrase** — pinned to "warn loudly, leave the bank on the derived key". If the owner later wants auto-decrypt (`PRAGMA rekey = ''`), it needs its own verification pass (e_sqlite3mc empty-rekey behavior unmeasured) — deferred, not silently attempted.
4. **Real bws behaviors** (message text, exit codes, `--version` flag shape, token auth) — no Bitwarden org access; every branch is driven through the fake runner/fake script with §5.4 messages passing bws stderr through verbatim, so real-bws text remains actionable. The `--version` presence check: if a real bws exits non-zero for `--version`, the presence check still passes (only FileNotFound means missing) — safe both ways.
5. **E2E harness startup-failure surfacing** — refusal-to-start is asserted at resolver/factory level (deterministic); the process-level check is manual (S6.3). An E2E assertion via `McpServerFactory` is optional and must be verified against the harness before being added.
6. **PATH visibility of bws for GUI-spawned MCP clients** — operational, documented in S6 (clients must have bws on their PATH); not a code fix.
7. **`CliArgsTests` hard-coded verb inventories** — if the file asserts the verb list, S3 extends it in the same commit as the tree (TDD).

---

## 8. Final gate

After S6: bare `dotnet build` + `dotnet test` green from the worktree root; `git status` shows only per-path-addressed commits (never `-A`); the completion report records: wave-by-wave merge order (S1+S2a → S2b → S3+S4 → S5 → S6), the §5 contracts as merged, and the manual process-level refusal check result.
