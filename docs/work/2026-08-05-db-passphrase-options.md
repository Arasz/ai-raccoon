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
