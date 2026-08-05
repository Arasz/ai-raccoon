# Research: Removing the AIRACCOON_DB_PASSPHRASE environment variable

**Date:** 2026-08-05
**Question:** How can the `AIRACCOON_DB_PASSPHRASE` environment variable be removed from the AiRaccoon MCP server — can the passphrase be stored inside the encrypted DB, should `SecureString` be used to handle it, and which replacement channel is safest for a local-first macOS MCP server?

```chart:matrix
title: option resistance by threat (1 = weak, 3 = strong)
, at-rest theft, same-user snoop, backup leak, unattended start, setup friction
env var (status quo), 1, 1, 1, 3, 1
key file 0600, 2, 1, 1, 3, 1
macOS Keychain, 3, 2, 3, 3, 2
interactive prompt, 3, 3, 3, 1, 3
```

## Findings

### F1 — SQLCipher v4 has no DEK envelope: the passphrase-derived key *is* the page key; the database file stores only the 16-byte KDF salt, never key material [READ]

Key derivation is PBKDF2-HMAC-SHA512 with 256,000 iterations (v4 defaults) over the passphrase plus a unique random salt held in **the first 16 bytes of the database file**; the output is used directly as the AES-256-CBC page-encryption key, and a *separate* HMAC key is derived from the encryption key with 2 PBKDF2 iterations and a salt variation. The design doc explicitly frames the alternative: "If use of a passphrase is undesirable, an application may provide raw binary key data (for instance to support vaulted keys, or the use of PKI based key exchange)." In `sqlcipher.c` the KDF path is the fallback in `sqlcipher_cipher_ctx_key_derive`: key material is either unpacked from a raw `x'hex'` spec (options 1–3) or derived via the provider KDF; the HMAC key is then derived from the encryption key ("generate a separate key for HMAC… the output of the previous KDF as the input to this KDF run").

**Evidence:** https://www.zetetic.net/sqlcipher/design/ (retrieved 2026-08-05: salt in first 16 bytes; PBKDF2-HMAC-SHA512, 256,000 iterations; raw-key note); `src/sqlcipher.c:1303-1321,1807-1896` in https://github.com/sqlcipher/sqlcipher (master, retrieved 2026-08-05). The bank file itself: `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:31` (`memory.db`) and passphrase → `Password` → e_sqlite3mc at `SqliteConnectionFactory.cs:37-46` + `SqliteEncryptionInit.cs:25-29` (repo, READ).

### F2 — SQLCipher and e_sqlite3mc both support raw 256-bit keys with no KDF: `PRAGMA key = "x'<64 hex>'"`, plus key+salt and key+hmac+salt variants; `PRAGMA rekey` rotates [READ]

The SQLCipher API doc gives three key forms: passphrase (KDF), raw key only (`x'` + 64 hex chars, "converted directly to 32 bytes (256 bits) of key data"), and raw key + explicit salt (`x'` + 64 hex key + 32 hex salt). Raw-key detection in source requires the string to start with `x'`, end with `'`, have even-length hex content (case-insensitive prefix match). SQLite3 Multiple Ciphers (e_sqlite3mc, the provider this repo forces at `SqliteEncryptionInit.cs:27`) documents the identical mechanism: `PRAGMA key = "x'…'"` raw key for the SQLCipher scheme, and a dedicated `PRAGMA hexkey = '<hex>'` for binary keys. `PRAGMA rekey` changes the key of an existing database. The salt stays random per database when a raw key (without explicit salt) is used, so per-file uniqueness is preserved.

**Evidence:** https://www.zetetic.net/sqlcipher/sqlcipher-api/ (retrieved 2026-08-05: "Setting the Key", "Changing the Key", examples 2 and 3); https://utelle.github.io/SQLite3MultipleCiphers/docs/configuration/config_sql_pragmas/ (retrieved 2026-08-05: `PRAGMA key`/`PRAGMA hexkey` syntax, "Raw key data (without key derivation)", "Currently only the cipher schemes sqleet: ChaCha20 and SQLCipher: AES 256 Bit support this method"); https://utelle.github.io/SQLite3MultipleCiphers/docs/ciphers/cipher_sqlcipher/ (retrieved 2026-08-05: v4 parameter table, kdf_iter 256000).

### F3 — Raw keys work end-to-end through Microsoft.Data.Sqlite's `Password` channel against e_sqlite3mc: create, reopen, wrong-key rejection, rekey, and no plaintext key/row material in the file — measured [MEASURED]

A throwaway console app (net10.0, `Microsoft.Data.Sqlite` 10.0.x, `SQLitePCLRaw.bundle_e_sqlite3mc` 2.1.11, macOS 26.5.2, dotnet SDK 10.0.302) set `SqliteConnectionStringBuilder.Password` to the string `x'2DD29C…6D99'` and ran six probes: (1) create+open with raw-key Password — OK; (2) reopen with same key, row read — OK; (3) reopen with a different raw key — throws `SqliteException` (Microsoft.Data.Sqlite forces decryption with `SELECT COUNT(*) FROM sqlite_master;`); (4) a passphrase-created DB does **not** open with a raw key and vice versa — throws; (5) `PRAGMA rekey = "x'…'"` (double-quoted string form) rotates raw key A → C and the DB reopens under C; (6) the DB header is 16 random bytes (salt) and neither the key hex nor the row text appears anywhere in the file. The reason the `Password` channel carries raw keys: Microsoft.Data.Sqlite quotes the password with SQLite's `quote()` and emits `PRAGMA key = '<quoted>';` — the round-tripped string value is exactly `x'…'`, which the codec parses as a raw key. This is the enabler for a key-file provider: **no provider changes are needed, only what string `IEncryptionKeyProvider.GetPassphrase()` returns.**

**Evidence:** Probe project `/tmp/rawkey-test/RawKeyProbe` (Program.cs, `dotnet run` output "ALL PROBES PASSED", 2026-08-05, machine: macOS 26.5.2, dotnet 10.0.302). Provider path read from source: `src/Microsoft.Data.Sqlite.Core/SqliteConnectionInternal.cs:104-125` in https://github.com/dotnet/efcore (main, retrieved 2026-08-05: `PRAGMA key = <quote(password)>` then `SELECT COUNT(*) FROM sqlite_master;`, quote via `QuotePassword`/`SELECT quote($password)`).

### F4 — The passphrase cannot be stored inside the encrypted DB in any scheme that removes the need for an external secret; a "wrapped copy in the DB" is strictly worse than a wrapped copy in the OS keychain [INFERRED]

Reasoned from F1/F2 and the opening requirement: the bank cannot be opened without the key, so anything stored inside it is unreadable until the key is supplied — the key must already exist outside the DB. Concretely: (a) the DB's only plaintext region is the 16-byte header salt (extendable only via `PRAGMA cipher_plaintext_header_size`, still plaintext by definition — not a secret container); (b) SQLCipher has no DEK-envelope: there is no separately generated data-encryption key wrapped by a key-encryption key; the derived/raw key IS the page key, so "store the wrapped DEK in the DB" has no built-in slot; (c) a hand-rolled wrapped copy (keychain-key-encrypted passphrase stashed in the plaintext header) would need an external wrapping key anyway — that key is then the effective secret, and co-locating the wrapped blob with the DB gives nothing over a sibling file while creating a bootstrap dependency in the wrong direction (the keychain read must happen *before* the DB opens, not after). The real exposure the DB *does* contain: salt + HMAC means a stolen DB supports **offline brute force of weak passphrases** — a 256-bit random raw key removes that attack entirely. The only legitimate "inside the DB" content is metadata (key id/version), not key material.

**Evidence:** Reasoning from F1 (salt-only header, KDF-is-page-key), F2 (raw-key forms), F3 (open requires key before any read). `PRAGMA cipher_plaintext_header_size` exists per https://www.zetetic.net/sqlcipher/sqlcipher-api/ (retrieved 2026-08-05).

### F5 — `PRAGMA rekey` is the rotation primitive: it changes the key of an existing bank in place, but the new key must still come from outside the file [READ]

`PRAGMA rekey` / `sqlite3_rekey()` change the encryption key of an existing database; Microsoft.Data.Sqlite needs the value as a quoted text literal (the double-quoted string form, verified in F3 probe 5 — a bare `x'…'` blob literal is a syntax error in the PRAGMA position). It is the migration tool for moving an existing passphrase-encrypted bank onto a raw key, but it is an operation on the file, not a storage mechanism. (That rekey re-encrypts pages, and therefore costs a full-file rewrite, is INFERRED from the cipher design in F1 — the API page does not state it.)

**Evidence:** https://www.zetetic.net/sqlcipher/sqlcipher-api/ "Changing the Key" (retrieved 2026-08-05); syntax failure + double-quoted fix measured in probe 5 of F3.

### F6 — SecureString is officially not recommended for new development on .NET (Core); it is not a storage mechanism and the docs say so [READ]

The Microsoft Learn API page carries an "Important" notice: "We recommend that you don't use the SecureString class for new development on .NET (Core) or when you migrate from .NET Framework." The page's own "How secure is SecureString?" section lists platform and duration limitations: the internal buffer is **not encrypted on non-Windows platforms**, and even on Windows the plaintext is exposed whenever the value is modified or converted for interop.

**Evidence:** https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring (retrieved 2026-08-05; sections "Important", "String versus SecureString", "How secure is SecureString?").

### F7 — On macOS and Linux a SecureString is a pinned managed buffer with no encryption; the only effect is a shorter plaintext window in process memory [READ]

"On the Windows operating system, the contents of a SecureString instance's internal character array are encrypted. However, whether because of missing APIs or key management issues, encryption is not available on all platforms… SecureString does not encrypt the internal storage on non-Windows platform. Other techniques are used on those platforms to provide additional protection." The dotnet/platform-compat analyzer rule (DE0001) is blunter: "Don't use SecureString for new code. When porting code to .NET Core, consider that the contents of the array are not encrypted in memory." Community consensus matches (PowerShell discussion: "considered deprecated for secure purposes in .NET Core/PowerShell 7+").

**Evidence:** https://learn.microsoft.com/en-us/dotnet/api/system.security.securestring (retrieved 2026-08-05); https://github.com/dotnet/platform-compat/blob/master/docs/DE0001.md (retrieved 2026-08-05); https://github.com/PowerShell/PowerShell/discussions/24772 (retrieved 2026-08-05).

### F8 — Verdict on SecureString: do not use it — it does not address the storage/startup problem at all [INFERRED]

Reasoned from F6/F7: the question is *where the passphrase comes from at startup* (env var, file, keychain, prompt). SecureString only affects how a value is held in managed memory once it exists; the source channel is unchanged, so adopting it would keep the env var (or move it to a file) while adding pinning ceremony to `IEncryptionKeyProvider` for zero storage-security gain on macOS. The actual mitigation for in-memory exposure is to keep the secret's lifetime short and prefer OS-owned secrets (keychain), not a .NET wrapper type.

**Evidence:** Reasoning from F6, F7, and the repo interface `src/AiRaccoon.Infrastructure/Sqlite/IEncryptionKeyProvider.cs:6-10` (returns `string?`; READ).

### F9 — Status quo (env var): the last env-only secret in a repo that has ruled secrets env-only, but the passphrase is duplicated into client `.mcp.json` env blocks — plaintext files that are the real at-rest exposure [READ]

`EnvEncryptionKeyProvider` is the sole reader (`src/AiRaccoon.Infrastructure/Sqlite/EnvEncryptionKeyProvider.cs:9-15`), and the documented setup is to place `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped `.mcp.json` `env` block (`README.md:193-207`) — a plaintext file every same-user process can read, included in backups, and one that the repo's own ruling classifies as a tracked/shared-file risk for `args` (`docs/plans/cli-args-parsing.md:53-58`). The prior CLI findings classified it as one of the 4 env-only secrets that keep the env channel (`docs/work/2026-08-04-cli-config-findings.md`, F12). On Linux the environment of any same-user process is readable via `/proc/<pid>/environ` — the man page: "Permission to access this file is governed by a ptrace access mode PTRACE_MODE_READ_FSCREDS check", i.e. same-UID processes pass.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/EnvEncryptionKeyProvider.cs:9-15`, `README.md:193-207`, `docs/plans/cli-args-parsing.md:48-58`, `docs/work/2026-08-04-cli-config-findings.md` F12 (repo, READ); https://man7.org/linux/man-pages/man5/proc_pid_environ.5.html (retrieved 2026-08-05).

### F10 — Measured on macOS 26.5.2: same-user processes cannot read another process's environment via `ps -E`, `ps e`, or `sysctl KERN_PROCARGS2` — the env-var threat on macOS is materially weaker than on Linux, but the `.mcp.json` copy and backups remain [MEASURED]

Three probes against a background `sleep` launched with `SECRET_PROBE=topsecret-abc123` (pid 96318): `ps -E -p` prints no environment; `ps e -p` prints none; a Swift `sysctl(KERN_PROCARGS2)` reader (buffer walk with `argc` + exec-path + argv skip) returns the exec path and argv but **no environment block at all** — the buffer ends with an empty string immediately after the last argv string. Self-read of the probe's own process returns the same shape (argc=3, env count 0). So on this OS version the classic env-dump channel is closed; the remaining macOS exposures are the `.mcp.json` plaintext copy, shell history/rc if the user exports the var, `launchctl` contexts, child-process inheritance, and memory of the running server. Linux still exposes `/proc/<pid>/environ` (F9).

**Evidence:** Probe binaries and transcripts in `/tmp/procar/` (main.swift, trace4.swift, run outputs, 2026-08-05; macOS 26.5.2, `sw_vers` ProductVersion 26.5.2). `ps -E -p 96318` output shows PID/TTY/TIME/CMD only.

### F11 — macOS Keychain via the `security` CLI works headlessly for round-trip store/read, keeps the secret out of the keychain file in plaintext, and fails cleanly when the keychain is locked — measured [MEASURED]

In a throwaway keychain at `/tmp/probe.keychain`: `security create-keychain -p probe123` → `security add-generic-password -a ai-raccoon -s ai-raccoon-db -w 'probe-secret-42'` → `security find-generic-password -s ai-raccoon-db -a ai-raccoon -w` returns the secret; `grep` for the secret bytes in the keychain file finds **zero** occurrences (the item is encrypted at rest in the keychain container); after `security lock-keychain`, the same `find` exits 128 with no output — the failure mode an unattended server would hit against a *locked* keychain. The local `man security` (macOS 26.5.2) documents the full CLI surface including `add-generic-password`, `find-generic-password`, `lock-keychain`/`unlock-keychain`, and partition-list ACL commands.

**Evidence:** Transcripts in the terminal session of 2026-08-05 (commands above; macOS 26.5.2); `man security` (local, READ). The repo's earlier research already sketched the `Process.Start("/usr/bin/security")` wrapper and the first-access prompt / `-T` preauthorization caveat: `docs/research/encryption-at-rest.md:37-75` (READ).

### F12 — Keychain threat model: strongest at-rest story of all options (keychain-file encryption plus FileVault), no plaintext copy in client configs, but same-user code can still query the keychain, and first-access prompting/ACLs need a one-time setup verb [INFERRED]

Reasoned from F11 and repo docs: the secret is encrypted at rest *inside* the keychain container (F11 measured), and on this machine FileVault is on (`fdesetup status` → "FileVault is On", measured 2026-08-05), so the keychain item has two layers at rest; the item never appears in `.mcp.json` or backups of the repo/config tree, and MCP clients respawning the server need no environment at all. Residual risks: any process running as the user *can* call `security find-generic-password` (same-user trust boundary is the same as for a 0600 file — the keychain's edge is that passive readers get nothing, no prompt-free read of a file), iCloud Keychain sync can replicate items across devices if enabled (flagged in `docs/research/encryption-at-rest.md:72`), and losing the keychain item loses the bank unless the key is exported — which argues for the key file doubling as a recovery copy. The login keychain is unlocked at login (standard macOS behavior), so unattended spawns succeed in the normal session; a locked-keychain startup fails loudly (F11), which is the correct failure mode. First-access GUI-prompt behavior for a CLI-created login-keychain item was **not** measured (would require writing to the user's login keychain); `-T` preauthorization at add time is the documented escape hatch.

**Evidence:** F11 measurements; `fdesetup status` output (2026-08-05); `docs/research/encryption-at-rest.md:37-75` (repo, READ).

### F13 — Cross-platform equivalents exist for the other two OSes: Windows DPAPI/Credential Manager and Linux Secret Service (D-Bus, gnome-keyring/KWallet, libsecret) [READ]

Windows: `System.Security.Cryptography.ProtectedData` (DPAPI) protects blobs under `CurrentUser` scope — Windows-only, throws `PlatformNotSupportedException` elsewhere (verified pattern already documented in this repo: `docs/research/encryption-at-rest.md:11-35`). Linux: the freedesktop Secret Service API (org.freedesktop.Secret.Service over D-Bus) is the standard secret store with collections, locking/unlocking, and prompting; accessed from .NET via a CLI wrapper (e.g. `secret-tool`) or libsecret bindings. Neither is needed for a macOS-primary server, but a key-file provider is the portable common denominator across all three.

**Evidence:** https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata (retrieved 2026-08-05); https://specifications.freedesktop.org/secret-service/latest/ (Secret Service API Draft 0.2, retrieved 2026-08-05); `docs/research/encryption-at-rest.md:11-35` (repo, READ).

### F14 — Interactive prompt has a first-class precedent in this repo (`sync add s3` reads secrets from stdin), but a startup prompt breaks the MCP spawn model [READ]

`ConfigCommands.cs:284-299` shows the established pattern: secrets are read interactively from stdin with an empty-input abort, never from argv. The MCP server, however, is spawned by clients (`.mcp.json` config, `README.md:185-207`) at arbitrary times — typically without a TTY — so a *startup* prompt would hang or fail exactly when the client needs the server (that failure mode is INFERRED from the stdio-spawn model). The prompt belongs at *setup time* (a `config` verb that writes the key into the keychain/key file), not at server start.

**Evidence:** `src/AiRaccoon/Setup/ConfigCommands.cs:284-299` (repo, READ); spawn model from `README.md:185-207` (repo, READ).

### F15 — Supply-chain note: the pinned encryption stack itself is flagged — `SQLitePCLRaw.bundle_e_sqlite3mc` 2.1.11 pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 with a high-severity advisory (CVE-2025-6965, SQLite < 3.50.2 aggregate memory corruption) [READ]

The repo pins `SQLitePCLRaw.bundle_e_sqlite3mc` 2.1.11 (`Directory.Packages.props:12-16`, with a comment documenting the deliberate 2.1.11-train choice pending e_sqlite3mc in Microsoft.Data.Sqlite 11). The probe build of F3 emitted `NU1903` for exactly this package (measured 2026-08-05): GHSA-2m69-gcr7-jv3q / CVE-2025-6965 affects SQLite before 3.50.2. Any change to the encryption key path should double-check the current advisory status of this package.

**Evidence:** `Directory.Packages.props:12-16` (repo, READ); NU1903 warning in `dotnet run` output of the F3 probe (2026-08-05, measured); https://github.com/advisories/GHSA-2m69-gcr7-jv3q (retrieved 2026-08-05).

### F16 — Recommendation: (1) macOS Keychain via the `security` CLI as the primary store, (2) a 0600 raw-key file as the portable fallback and recovery copy, (3) interactive prompt at setup time only, (4) delete the env var entirely [INFERRED]

Reasoned from F9-F14. For a local-first macOS MCP server the ranking is: **Keychain** (best at-rest protection of the secret itself — keychain encryption + FileVault; zero `.mcp.json` plaintext; no env needed for respawns; loud locked-keychain failure instead of silent plaintext) → **key file** (trivially portable, works identically on macOS/Linux/Windows, survives keychain loss as a recovery copy; weakest point is plaintext-at-rest in backups — mitigated by FileVault on the primary machine and by treating the file as a recovery/portability copy) → **interactive prompt at setup, never at startup** (the `sync add s3` pattern, repurposed as `config db-key set`) → **env var: removed** (its only real remaining advantages — zero setup, unattended start — are fully covered by keychain and key file, and it is the only option that forces the secret into plaintext client config files and, on Linux, `/proc`).

**Evidence:** Reasoning from F9-F14; the option matrix chart above.

### F17 — Implementation sketch: an `IEncryptionKeyProvider` chain with a `config db-key` verb, raw keys via the existing `Password` channel, and `PRAGMA rekey` for migration [INFERRED]

Reasoned from F2/F3 (raw keys need zero provider changes — only the returned string), F11 (keychain CLI mechanics), F14 (setup-verb precedent). Shape: (a) `KeychainEncryptionKeyProvider` (macOS): `security find-generic-password -s ai-raccoon-db -a <scope> -w` via `Process.Start`, returns null on exit≠0 so the chain falls through; (b) `KeyFileEncryptionKeyProvider`: reads `<dataRoot>[/.ai-raccoon]/memory.key`, 64 hex chars (raw key), created with `File.SetUnixFileMode(..., UserRead|UserWrite)` (0600); returns the `x'<hex>'` string so the existing `Password` path keys the bank with no KDF; (c) `PromptEncryptionKeyProvider` for the interactive `config db-key set` verb (pattern of `ConfigCommands.cs:284-299`), which *writes* to keychain and/or key file; (d) `EnvEncryptionKeyProvider` deleted; DI wiring at `Dependencies.cs:27-28` swaps to the chain; (e) migration for existing passphrase banks: `PRAGMA rekey = "x'…'"` (F5) executed once by the setup verb, after opening with the old passphrase; (f) `GetPassphrase` returning raw-key strings keeps the `SqliteConnectionFactory` unchanged (`SqliteConnectionFactory.cs:37-46`).

**Evidence:** Reasoning from F2, F3, F5, F11, F14 and the cited repo files (`Dependencies.cs:27-28`, `SqliteConnectionFactory.cs:37-46`, `ConfigCommands.cs:284-299` — all READ).

### F18 — ADR follow-up: this decision deserves a dated ADR covering the channel change, the raw-key format, rotation, and documentation churn [INFERRED]

Reasoned from the repo's documentation surface: removing the env var touches `README.md:37,60,193-207`, `src/AiRaccoon/README.md:83,131`, `SECURITY.md:44`, `docs/reference/agent-memory-server.md:148,248`, and the registry manifest (`src/AiRaccoon/.mcp/server.json`, which currently omits the var per `docs/work/2026-08-04-cli-config-findings.md` F10). A follow-up ADR should pin: primary=keychain / fallback=key file, raw-key (no-KDF) format, per-scope keychain service/account naming (user vs project scope banks, `SqliteConnectionFactory.cs:22-29`), `PRAGMA rekey` migration, and the locked-keychain failure mode.

**Evidence:** Reasoning from the cited repo files (README.md:37,60,193-207; SECURITY.md:44; agent-memory-server.md:148,248; .mcp/server.json:14-27; 2026-08-04 findings F10).

### F19 — Bitwarden.Secrets.Sdk 1.0.0 (published 2025-01-24, the only NuGet release) targets net6.0 with a bundled Rust native core for osx-x64, osx-arm64, linux-x64 and win-x64; it loads and runs under net10.0 on macOS arm64 — measured [MEASURED]

NuGet registration lists a single listed version: `Bitwarden.Secrets.Sdk` 1.0.0, published 2025-01-24, target framework net6.0, dependency `System.Text.Json >= 8.0.5`. The 1.0.0 nupkg contains `lib/net6.0/Bitwarden.Sdk.dll` (46 KB) plus `runtimes/{osx-x64,osx-arm64,linux-x64,win-x64}/native/libbitwarden_c.*` (the Rust core; the arm64 dylib is 7.0 MB), NuGet repository-signed (`.signature.p7s`). A probe console app (net10.0, dotnet 10.0.302, macOS 26.5.2) restored the package and `new BitwardenClient(...)` succeeded in 16–306 ms — the native library loads on this platform with zero extra setup. The C# binding is explicitly beta: the README says "This is a beta release and might be missing some functionality"; the crate docs say the same. License is Bitwarden's custom "BITWARDEN SOFTWARE DEVELOPMENT KIT LICENSE AGREEMENT v1, 17 March 2023" — source-available, not OSI. Maturity note: `languages/csharp` is actively maintained (commits 2026-06-10, 2026-02-12, STJ v10 bump 2026-01-20) but **no package has shipped since 1.0.0** — everything added after 2025-01-24 (including the async API) is main-branch only. Supply-chain note mirroring F15: the shipped secret path is a ~7 MB native binary from a third-party repo, black-box from the app's perspective.

**Evidence:** https://api.nuget.org/v3/registration5-gz-semver2/bitwarden.secrets.sdk/index.json (retrieved 2026-08-05: 1.0.0, 2025-01-24, net6.0, STJ >= 8.0.5); nupkg unzip listing of `/tmp/bw.nupkg` (2026-08-05); `dotnet run` of `/tmp/bwprobe/BwProbe` probe A0 (2026-08-05, macOS 26.5.2, dotnet 10.0.302); https://github.com/bitwarden/sdk-sm/blob/main/languages/csharp/README.md (retrieved 2026-08-05: beta statement); LICENSE at https://github.com/bitwarden/sdk-sm (retrieved 2026-08-05); GitHub commits API for `languages/csharp/Bitwarden.Sdk` (retrieved 2026-08-05).

### F20 — The published 1.0.0 C# API is synchronous and matches the README exactly; async variants (`LoginAccessTokenAsync`, `GetByIdsAsync`, …) exist only on main, unreleased — measured [MEASURED]

Reflection over the published `Bitwarden.Sdk.dll`: `BitwardenClient(BitwardenSettings? settings = null)` with `Auth`, `Projects`, `Secrets` properties and `Dispose()`; `AuthClient.LoginAccessToken(string accessToken, string stateFile = "")`; `SecretsClient` — `Get(Guid)`, `GetByIds(Guid[])`, `List(Guid orgId)`, `Sync(Guid orgId, DateTimeOffset? lastSyncedDate)`, `Create`, `Update`, `Delete`; `ProjectsClient` — `Get/List/Create/Update/Delete`; request/response DTOs (e.g. `SecretResponse { Id, Key, Value, Note, ProjectId, OrganizationId, CreationDate, RevisionDate }`); envelope `ResponseForX { Success, ErrorMessage, Data }`; exceptions `BitwardenException` and `BitwardenAuthException`. `BitwardenSettings` exposes only `ApiUrl` and `IdentityUrl` (defaults to api.bitwarden.com / identity.bitwarden.com — verified by the probe hitting those hosts). The main branch (source read at https://github.com/bitwarden/sdk-sm/tree/main/languages/csharp/Bitwarden.Sdk) adds `*Async` methods over the `run_command_async` FFI (added 2025-04-25, #993), targets net8.0 with System.Text.Json 10.0.2, and makes `stateFile` optional — none of that is in the published package.

**Evidence:** Reflection dump of `~/.nuget/packages/bitwarden.secrets.sdk/1.0.0/lib/net6.0/Bitwarden.Sdk.dll` via `/tmp/bwprobe/dump` (2026-08-05); `AuthClient.cs`, `SecretsClient.cs`, `BitwardenClient.cs`, `BitwardenSettings.cs`, `CommandRunner.cs`, `Bitwarden.Sdk.csproj` from sdk-sm main (fetched 2026-08-05); probe runs A1/B1 hitting api.bitwarden.com and identity.bitwarden.com with default settings (2026-08-05).

### F21 — Measured error behavior: token format is validated client-side before any network call; a format-valid fake token fails with `BitwardenAuthException` carrying the server body (`400 {"error":"invalid_client"}`, ~200–263 ms); unauthenticated secret fetch fails with `BitwardenException` (`401 Unauthorized`); a connection-refused endpoint fails instantly with the reqwest error text and an empty inner exception; `BitwardenSettings` exposes no timeout, retry, or proxy config [MEASURED]

Probes against the real identity endpoint with a format-valid but fake token (`0.<uuid>.<16B b64>:<16B b64>`): malformed tokens are rejected in 0–9 ms with "Access token is not in a valid format" (pure local validation); the format-valid fake token reaches `identity.bitwarden.com/connect/token` and returns `400 Bad Request {"error":"invalid_client"}` wrapped in `BitwardenAuthException` (263 ms / 202 ms). A `Secrets.GetByIds` call without login hits `api.bitwarden.com` and returns `BitwardenException: Received error message from server: [401 Unauthorized]`. Pointing `ApiUrl`/`IdentityUrl` at a closed local port fails with `BitwardenAuthException: error sending request for url (http://127.0.0.1:9/connect/token)` in 0 ms (connection refused surfaces immediately; a blackholed network would hang until the Rust core's own HTTP timeout, which is not configurable from C#).

**Evidence:** Probe runs A1, B1, C1, D1 of `/tmp/bwprobe/BwProbe` (Program.cs, `dotnet run` transcripts, 2026-08-05, macOS 26.5.2, dotnet 10.0.302; fake token `0.00000000-0000-0000-0000-000000000000.MDEyMzQ1Njc4OWFiY2RlZg==:QUJDREVGR0hJSktMTU5PUA==`).

### F22 — The SDK state file: `stateFile` is a caller-supplied path; on successful login the Rust core persists authenticated client state there ("basic state to avoid reauthenticating when creating a new Client", CHANGELOG v0.4.0, #388); the bws CLI precedent stores per-token-id session state at `~/.config/bws/state/<access-token-id>` and warns about "authentication limits" without it — the file is secret-bearing, and its exact content and permissions are UNVERIFIED (needs a real token) [READ]

The C# `LoginAccessToken(accessToken, stateFile)` passes the path straight into the command JSON; the Rust core (in the private bitwarden/sdk-internal monorepo, pinned rev in `Cargo.toml:26`) owns the file write. The CLI in the same repo (`bws`) resolves a default state path of `<home>/.config/bws/state/<access_token_id>` (`get_state_file`, `create_dir_all` on the dir) and passes it to the same `login_access_token`; its startup warning says to set `state_dir` "to avoid authentication limits" — i.e. the state avoids repeated token exchanges. Two measured hygiene facts: on FAILED login the SDK does not create the state file, and a pre-existing garbage state file is left untouched. One open footgun: the C# wrapper serializes `stateFile = ""` (the default) as an empty string — not null — so the Rust `Option<String>` receives `Some("")`; what a successful login does with an empty path is UNVERIFIED and must be tested with a real token before relying on "no state file".

**Evidence:** `crates/bitwarden/CHANGELOG.md` v0.4.0 (2023-12-21) and v1.0.0 (2024-09-26); `crates/bws/src/state.rs:8-32` and `crates/bws/src/main.rs:87-113` in https://github.com/bitwarden/sdk-sm (cloned 2026-08-05); `CommandRunner.cs:14-19` (no null/empty stripping in the C# serializer); probe runs A1/E1 of `/tmp/bwprobe/BwProbe` (state file absent after failed login; garbage file unchanged); sdk-sm `Cargo.toml:26` (sdk-internal pin) — all retrieved 2026-08-05.

### F23 — The bootstrap problem is structurally the same as F4: the access token cannot live in the settings table (it is inside the encrypted DB and unreadable before the passphrase exists — circular); the token must come from an OS-level channel that is readable pre-open, and the SDK state file does not solve this — it is only written AFTER a successful login, and it is itself a secret on disk [INFERRED]

Reasoned from F4 (nothing inside the bank is readable pre-open), F11/F12 (keychain is OS-level and readable pre-open), F21 (login requires network + token), F22 (state file is produced only by a successful login). The same circularity that rules out "passphrase in the DB" rules out "token in the DB": both need the key before the key exists. The SDK state file cannot be the bootstrap store for the same reason — it is an output of authentication, not an input to it — and additionally it would place token material at a path and with permissions chosen by a third-party native core.

**Evidence:** Reasoning from F4, F11, F12, F21, F22.

### F24 — Ranked token-storage options for the `encryption bitwarden` flow: (1) macOS Keychain — the only option that is both readable pre-DB-open and protected at rest (F11/F12), and a leaked token is revocable/expirable and machine-account-scoped; (2) 0600 token file next to the bank — portable fallback for non-macOS, but it recreates the keyfile's backup exposure for a second secret; (3) SDK state file as the store — rejected (F22/F23); (4) interactive token entry at every server start — rejected for the MCP spawn model (F14); (5) the `bws` CLI as an alternative integration — viable and more mature, but adds a binary dependency and its session file is equally a disk secret. What the setup verb persists: token → keychain (service `ai-raccoon-db`, account `bw-<scope>`); non-secret metadata (secret id, org id) → settings table; no SDK state file by default [INFERRED]

The ranking mirrors F16's logic: the token is a credential like the passphrase, so the same at-rest hierarchy applies (OS-encrypted keychain > plaintext 0600 file), and the same setup-time-prompt rule applies (F14). Option (5) is ranked as an implementation alternative, not a storage tier: `bws` stores its session at `~/.config/bws/state/<token-id>` (F22) and would need the binary installed on every host; the SDK keeps everything in-process with the token exactly where the setup verb puts it.

**Evidence:** Reasoning from F11–F14, F22, F23.

### F25 — Threat-model delta vs keychain/keyfile: Bitwarden SM adds remote key custody (the raw DB key never rests on this machine — only in memory after fetch), credential revocation/expiry without local rekey, machine-account scoping, audit/event logs (Teams+ plans), and cross-machine consistency; it does NOT add protection against same-user keychain reads (F12 boundary unchanged), offline start (start now requires network + Bitwarden reachability), or resistance to Bitwarden-org compromise. Trap: the secret VALUE is the DB key — rotating the secret without rekeying the DB bricks the bank [INFERRED]

Access tokens are issued to a machine account and "give any machine they're applied to the ability to access only the secrets associated with that machine account"; they are never stored in Bitwarden databases, cannot be retrieved after creation (one-time display), and can be revoked at any time (revocation breaks retrieval immediately) or given an expiry (default Never). So the practical gains over keychain-direct: (a) the DB key exists only in Bitwarden's encrypted store and in server memory; (b) a compromised/leaked local credential is fixed by issuing a new token in the web UI — no local rekey of the DB, no touching other machines; (c) a dedicated machine account per app scopes what a token can read; (d) event/audit logs exist on Teams and Enterprise plans (Free has none); (e) several machines can share one DB key, each with its own token. The costs: server start now depends on api.bitwarden.com + identity.bitwarden.com; the local trust boundary is unchanged (any same-user process can still read the keychain item, F12); a Bitwarden org admin can read or change the secret (DoS by rotation); and rotation of the secret VALUE is not "change the secret in the web UI" — it is the full F5 rekey flow (update secret, `PRAGMA rekey` to the new value, verify), because the secret value IS the page key (F1/F3).

**Evidence:** Reasoning from F1, F3, F5, F12, F21; https://bitwarden.com/help/access-tokens/ (retrieved 2026-08-05: machine-account scoping, never stored/one-time display, revocation, expiry); https://bitwarden.com/help/secrets-manager-plans/ (retrieved 2026-08-05: Free = 2 users / 3 projects / 3 machine accounts, Teams = 20 machine accounts + event logs, Enterprise = 50 + SCIM).

### F26 — Network failure at server start: with the token in the keychain and no local key copy, an unreachable Bitwarden makes the server refuse to start (SDK throws; connection refused fails in 0 ms, F21) — the loud failure is correct and matches the locked-keychain philosophy, but an OPT-IN offline cache (the fetched passphrase mirrored to the 0600 key file as a degraded startup fallback) is the only way to keep unattended starts through outages [INFERRED]

Reasoned from F21 (no timeout config; instant failure on refused connections, unbounded hang on blackholed networks — the provider must wrap the SDK call in its own timeout), F24 (keychain token), F16 (key file already exists as recovery copy). Default posture: no cache — a startup that cannot reach Bitwarden fails loudly with a message naming the cause (distinguishable from "no key configured" by checking keychain presence first). Opt-in posture: `encryption bitwarden set --offline-cache` writes the fetched key to the existing 0600 key file; every start that succeeds via cache logs "degraded: Bitwarden unreachable, using offline cache". The cache's at-rest exposure equals the F16 key-file option exactly, so the trade is purely availability-vs-at-rest.

**Evidence:** Reasoning from F16, F21, F24.

### F27 — Implementation sketch: `BitwardenEncryptionKeyProvider : IEncryptionKeyProvider` in src/AiRaccoon.Infrastructure/Sqlite/ (SDK dependency stays in Infrastructure — clean-layering invariant; Domain untouched); GetPassphrase(): keychain token → `new BitwardenClient(new BitwardenSettings{...})` → `Auth.LoginAccessToken(token)` (no state file) → `Secrets.Get(secretId)` → return `x'<hex>'` (secret stores the 64-hex raw key; F3) inside a 15 s timeout; DI swap at Dependencies.cs:27-28; interactive `ai-raccoon encryption bitwarden [set|remove|show]` mirroring SyncAddS3Async (token from stdin, never argv; test-fetch BEFORE persisting; token → keychain or 0600 fallback; metadata → settings table; `PRAGMA rekey` the bank to the fetched key in the same session; verify by reopen) [INFERRED]

Shape: (a) `BitwardenEncryptionKeyProvider` takes `InfrastructureOptions`-derived config (scope, secret id, org id, optional api/identity URL) and a token resolver (keychain first, 0600 file fallback, env `AIRACCOON_BW_ACCESS_TOKEN` last — the env channel is acceptable for the token because it is revocable, unlike the DB key); `GetPassphrase()` is synchronous in the published SDK (F20) so it must run inside `Task.Run(...).WaitAsync(TimeSpan.FromSeconds(15))` at startup (F21/F26); returns null on "not configured" so the chain falls through, throws a wrapped `BitwardenException` on auth/network failure — the server then refuses to start, loudly (F26). (b) The interactive verb follows `ConfigCommands.cs:40,276-310`: `["encryption", "bitwarden", "set"]` prompts for the access token and the secret id/key on stderr with empty-input abort, performs a live `Secrets.Get` to validate both, then persists token → keychain (`security add-generic-password -s ai-raccoon-db -a bw-<scope> -w`, F11 pattern, with `-T` preauthorization) and metadata → `store.SetSettingAsync` (`Encryption.BitwardenSecretId`, `Encryption.BitwardenOrganizationId`); the same session then runs `PRAGMA rekey = "x'<fetched-hex>'"` (F5) so an existing passphrase or plaintext bank is sealed under the Bitwarden key, and reopens to verify. (c) DI: `Dependencies.cs:27-28` swaps `EnvEncryptionKeyProvider` for the chain `BitwardenEncryptionKeyProvider → KeyFileEncryptionKeyProvider (offline cache / recovery)`, deleting the env provider (F16); `IEncryptionKeyProvider` and `SqliteConnectionFactory` (lines 37-46) stay unchanged. (d) Layering: `Bitwarden.Secrets.Sdk` PackageReference lands only in AiRaccoon.Infrastructure, exactly like SQLitePCLRaw today.

**Evidence:** Reasoning from F3, F5, F11, F14, F16, F20-F22, F26 and the repo files (IEncryptionKeyProvider.cs:6-10; EnvEncryptionKeyProvider.cs:9-15; SqliteConnectionFactory.cs:37-46; Dependencies.cs:27-31; ConfigCommands.cs:40,276-310 — all READ).

### F28 — Updated recommendation: Bitwarden does NOT displace the F16 ranking for this repo's common case (single local machine, MCP-spawned server, offline-tolerant). Order stays: keychain-direct → Bitwarden SM (token in keychain; opt-in remote-key service for users who already run a Bitwarden org, need cross-machine key custody, revocation, or audit) → 0600 key file (portable fallback + recovery) → env var removed. Bitwarden ships behind an explicit setup verb, never as the default, never with the token in a plaintext file or the SDK state file [INFERRED]

Reasoned from F24-F27: Bitwarden's wins (remote custody, revocation, audit, multi-machine) all target scenarios this local-first single-machine server does not have today, while its costs (startup network dependency, beta SDK with a stale published package, 7 MB native binary supply-chain surface, custom non-OSI license) are paid on every start. The correct integration is therefore an opt-in tier that reuses the already-recommended keychain as the token store — the ranking of the storage channel is unchanged; Bitwarden adds a remote key SERVICE on top of it.

**Evidence:** Reasoning from F16, F24-F27.

## Option table: threat models

| Option | At-rest DB theft | Same-user process snooping | Account compromise | Backups | Unattended server start |
|---|---|---|---|---|---|
| **Env var (status quo)** | Secret in `.mcp.json` plaintext; DB itself protected by FileVault only | Linux: `/proc/<pid>/environ` readable by same user (F9); macOS: not readable via ps/sysctl, measured (F10); `.mcp.json` readable by any same-user process | Attacker with the login reads `.mcp.json` directly; env inherited by child processes | Secret ships with every backup of the config tree | Works (no interaction) |
| **Key file 0600 (raw key)** | Secret at rest = FileVault only; raw 256-bit key defeats offline brute force of the stolen DB (F1/F2) | Any same-user process can read the file (same trust boundary as `.mcp.json`); passive readers get the key — no prompt, no encryption | Same as status quo for file readers; no plaintext in client configs | Key file is copied by every backup — a plaintext secret in backup media (needs exclusion list) | Works (no interaction) |
| **macOS Keychain** | Secret encrypted at rest in keychain container (F11) + FileVault; strongest at-rest story | No plaintext file to read; same-user code can still query the keychain, but reads are observable/ACL-gated (F12); no passive read | Login-keychain access = user session; item does not exist in config files or repo tree | Keychain item not in ordinary file backups (F12, mechanism UNVERIFIED — see Still open); recovery = key-file copy | Works while login keychain is unlocked (normal session); locked keychain fails loudly (F11) |
| **Interactive prompt at start** | Secret never stored — strongest possible at-rest posture | Secret exists only in one process's memory; nothing on disk | Nothing persisted for the attacker to find; only live-memory theft works | Nothing to leak | **Broken**: MCP clients spawn the server without a TTY (F14) |
| **Interactive prompt at setup → keychain/key file** | Inherits the chosen store's posture | Inherits | Inherits | Inherits | Works — the prompt is out of the spawn path (F14) |

## Recommendation

1. **Primary: macOS Keychain** — `security add/find-generic-password` wrapper (`KeychainEncryptionKeyProvider`), service `ai-raccoon-db`, account = install scope; one-time `-T` preauthorization at add time to suppress first-access prompts.
2. **Fallback + recovery: 0600 raw-key file** at `<dataRoot>[/.ai-raccoon]/memory.key` containing 64 hex chars; the provider returns `x'<hex>'` so the *existing* `SqliteConnectionFactory` `Password` path keys the bank with no KDF and no provider/connection-string changes (measured, F3). Same file layout works on Linux/Windows.
3. **Setup-time interactive verb** `ai-raccoon config db-key set|show|remove` mirroring `sync add s3` (F14); the prompt never runs at server start.
4. **Delete `AIRACCOON_DB_PASSPHRASE`** and its documentation; existing encrypted banks migrate via `PRAGMA rekey` to the raw key (F5).
5. Do **not** use `SecureString` (F8). Do not attempt in-DB key storage (F4).

## Still open

- **Login-keychain first-access prompt behavior** for `security find-generic-password` on this machine — not measured because it would require writing an item to the user's login keychain; `-T` ACL pinning at add time is the documented workaround (F11/F12).
- **Whether Time Machine excludes keychains** by default — affects the backup-hygiene score in the matrix (currently UNVERIFIED, scored 3 for keychain on the strength of "not in ordinary file backups").
- **Rekey of an existing *passphrase* bank onto a raw key through the Microsoft.Data.Sqlite `Password` channel** — F3 used freshly created raw-key DBs; the rekey SQL itself is verified (F3 probe 5) but the full migrate flow (open with passphrase → rekey to raw key) was not exercised.
- **e_sqlite3mc ↔ SQLCipher version parity** — raw-key behavior verified empirically (F3) but the exact upstream SQLCipher codec version embedded in SQLitePCLRaw's e_sqlite3mc 2.1.11 was not checked.
- **`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 advisory status** (CVE-2025-6965, F15) — whether a patched 2.1.x exists, and whether upgrading interacts with the pinned 2.1.11 e_sqlite3mc train.
- **A prompt-free way to detect "no key configured" vs "keychain locked"** at startup so the failure message can distinguish "run `config db-key set`" from "unlock your keychain" — the two failure modes produce similar null results today.
- **Real-token success path (Bitwarden, Option E)** — none of the F19–F28 findings exercise a successful `LoginAccessToken` + `Secrets.Get` with a genuine machine-account token: the SDK state-file content and on-disk permissions (F22), the `stateFile = ""` → `Some("")` behavior on success (F22), and end-to-end provider latency are all UNVERIFIED until probed against a real org.
- **First-access keychain prompt for the `bw-<scope>` item** — same unmeasured behavior as the passphrase item (F11/F12); `-T` preauthorization at add time is the documented workaround.
- **Bitwarden startup timeout on a blackholed network** — connection-refused fails in 0 ms (F21), but a packet-dropping network's hang duration is unmeasured; the 15 s provider timeout in F27 is a design choice, not a measurement.
- **Token-revocation behavior at runtime** — what the server does if the keychain token is revoked between starts (expected: 400 invalid_client, loud refusal per F26) was not exercised with a real org.

## Option E — Bitwarden Secrets Manager

**Date:** 2026-08-05 (follow-up pass on the same record)
**Question:** Can Bitwarden Secrets Manager serve as the source of the DB encryption passphrase through the C# SDK (github.com/bitwarden/sdk-sm/languages/csharp), where must the access token live given that the passphrase is needed *before* the DB opens, and does this change the F16 ranking?

```chart:matrix
title: Option E token-storage options by threat (1 = weak, 3 = strong)
, at-rest theft, same-user snoop, backup leak, unattended start, setup friction
keychain token, 3, 2, 3, 3, 2
0600 token file, 2, 1, 1, 3, 1
SDK state file, 1, 1, 1, 3, 1
interactive at start, 3, 3, 3, 1, 3
bws CLI session file, 2, 1, 1, 3, 2
```

### Package and platform facts (F19–F20)

The only NuGet release is **`Bitwarden.Secrets.Sdk` 1.0.0** (2025-01-24): net6.0 assembly, `System.Text.Json >= 8.0.5`, and a bundled Rust native core (`libbitwarden_c`) for osx-x64, **osx-arm64**, linux-x64 and win-x64 — verified by unzipping the nupkg and by running a net10.0 probe on this Mac (client construction 16–306 ms, no extra setup). The published API is **synchronous** (`Auth.LoginAccessToken(token, stateFile = "")`, `Secrets.Get/GetByIds/List/Sync/...`); the async variants exist only on main and have never shipped. The binding is explicitly **beta**, the package lags main by ~18 months of commits (last C# commit 2026-06-10, still no release), the native core is a ~7 MB black-box binary (supply-chain note, cf. F15), and the license is Bitwarden's custom source-available SDK agreement, not OSI. Conclusion: platform support is real and measured, but treat the SDK as a beta dependency with a frozen published API.

### The bootstrap problem (F23–F24)

The access token cannot live in the settings table: that table is *inside* the encrypted bank and is unreadable before the passphrase exists — the same circularity F4 established for the passphrase itself. The token must come from an OS-level channel that is readable pre-open, and the SDK state file does not help: it is written only *after* a successful login (it is an output of authentication, not an input), and it is itself a secret on disk written by a third-party native core. The passphrase flow is: **keychain token (pre-open, OS-protected) → SDK login (network) → `Secrets.Get(secretId)` → DB key in memory only → open the bank.** The token is the bootstrap secret; the DB key never needs to exist on disk at all.

### Ranked token-storage options (F24)

1. **macOS Keychain — the answer.** Readable pre-DB-open, encrypted at rest in the keychain container (+FileVault, F11/F12), no plaintext file, same-user passive readers get nothing. Bonus over storing the passphrase directly: a leaked token is *revocable, expirable, and machine-account-scoped* — fix exposure in the Bitwarden web UI without touching the DB or other machines.
2. **0600 token file next to the bank — portable fallback** (non-macOS hosts). Recreates the keyfile's backup exposure for a second secret; acceptable only where the keychain is unavailable.
3. **SDK state file as the store — rejected.** Uncontrolled path/permissions, secret on disk, and it cannot be created without a successful login, so it cannot bootstrap anything.
4. **Interactive token entry at every start — rejected.** The MCP spawn model has no TTY (F14); the prompt belongs to the setup verb.
5. **`bws` CLI as an alternative integration — viable but inferior here.** More mature surface and `bws secret get`, but adds a per-host binary dependency and its own session file (`~/.config/bws/state/<token-id>`) that is equally a disk secret. The SDK keeps everything in-process.

**What the setup flow persists:** the access token → keychain (service `ai-raccoon-db`, account `bw-<scope>`); non-secret metadata (secret id, organization id, optional API/identity URLs) → settings table; **no SDK state file by default**.

### Security value and threat-model delta (F25–F26)

Bitwarden SM adds, relative to keychain-direct and keyfile: **remote key custody** (the raw DB key rests only in Bitwarden's encrypted store and in server memory), **credential revocation/expiry without local rekey**, **machine-account scoping** (a dedicated machine account limits what a token reads), **audit/event logs** (Teams+ plans; the Free plan — 2 users / 3 projects / 3 machine accounts — has none), and **cross-machine consistency** (one DB key, many machines, per-machine tokens). It does *not* change the local trust boundary (same-user keychain reads still work, F12), it does not help offline (start now **requires** api.bitwarden.com + identity.bitwarden.com; on failure the SDK throws — connection refused fails in 0 ms — and the server should refuse to start loudly, matching the locked-keychain philosophy), and it adds a Bitwarden-org admin as a trust party (can read the secret; rotation by an attacker is a DoS). The one trap to design around: **the secret value IS the DB key** (no KDF when stored as a raw key, F3) — rotating the secret in the web UI without rekeying the bank bricks it; secret rotation is the F5 `PRAGMA rekey` flow, not a web-UI edit. An **opt-in offline cache** (fetched key mirrored to the existing 0600 key file, degraded-start log line) is the only way to keep unattended starts through outages, at exactly the F16 key-file at-rest cost.

### Implementation sketch (F27)

```csharp
// src/AiRaccoon.Infrastructure/Sqlite/BitwardenEncryptionKeyProvider.cs (sketch)
public sealed class BitwardenEncryptionKeyProvider : IEncryptionKeyProvider
{
    // token: keychain (security find-generic-password -s ai-raccoon-db -a bw-<scope> -w)
    //        → 0600 file fallback → AIRACCOON_BW_ACCESS_TOKEN env (revocable, so env is tolerable)
    public string? GetPassphrase()
    {
        if (!IsConfigured(out var token, out var secretId, out var orgId)) return null; // chain falls through
        using var client = new BitwardenClient(new BitwardenSettings { ApiUrl = ..., IdentityUrl = ... });
        return Task.Run(() =>
        {
            client.Auth.LoginAccessToken(token);            // no state file (F22/F24)
            return client.Secrets.Get(secretId).Value;      // 64-hex raw key from the secret
        }).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() switch
        {
            { } hex when hex is { Length: 64 } => $"x'{hex}'", // existing Password channel, no KDF (F3)
            _ => throw new InvalidOperationException("Bitwarden secret is not a 64-hex raw key")
        };
    }
}
```

- **DI:** `Dependencies.cs:27-28` swaps `EnvEncryptionKeyProvider` for the chain `BitwardenEncryptionKeyProvider → KeyFileEncryptionKeyProvider` (offline cache / recovery); `IEncryptionKeyProvider` and `SqliteConnectionFactory` (lines 37–46) stay untouched.
- **Config verb:** `ai-raccoon encryption bitwarden [set|remove|show]`, dispatched like `["sync","add","s3"]` (`ConfigCommands.cs:40`) with the `SyncAddS3Async` interactive pattern (token from stdin, empty-input abort, never argv, `ConfigCommands.cs:284-299`): prompt token + secret id → **live `Secrets.Get` test-fetch before persisting anything** → token → keychain (with `-T` preauthorization) → metadata → settings table → `PRAGMA rekey = "x'<fetched-hex>'"` seals an existing passphrase/plaintext bank under the Bitwarden key in the same session → reopen to verify.
- **Layering invariant:** `Bitwarden.Secrets.Sdk` is referenced only by AiRaccoon.Infrastructure (like SQLitePCLRaw today); Domain and MCP stay clean.
- **Runtime note:** the published SDK is synchronous, so the startup fetch must run inside the 15 s `WaitAsync` wrapper (F21/F26) — the SDK exposes no timeout of its own.

### Updated overall recommendation (F28)

**Bitwarden does not change the F16 ranking.** For this repo's actual shape — a local-first, single-machine, MCP-spawned, offline-tolerant server — keychain-direct stays primary, the 0600 key file stays the portable fallback/recovery copy, and the env var is removed. Bitwarden SM becomes an **opt-in tier** on top of the same keychain: valuable for users who already run a Bitwarden org and want remote key custody, cross-machine consistency, revocation, or audit; not worth its startup network dependency, beta-SDK risk, and supply-chain surface as the default. If adopted: token in the keychain, metadata in the settings table, no SDK state file, no default offline cache, and the secret treated as an immutable DB key rotated only through the rekey flow.
