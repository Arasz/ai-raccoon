---
name: dotnet-sqlcipher-encryption
description: "Use when working with SQLCipher-encrypted SQLite in .NET (e_sqlite3mc / SQLitePCLRaw bundle): raw 256-bit keys via Password='x'<hex>'', deriving keys from ed25519 SSH keys, PRAGMA rekey constraints (WAL unsupported), pluggable key-source providers (env/keychain/vault) with the pre-open sidecar pattern, and Dapper-over-SQLite3MC mapping traps."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, sqlcipher, sqlite, encryption, keys]
    related_skills: [database-encryption-key-management, dotnet-domain-modeling]
---

# SQLCipher encryption in .NET

Encrypted-at-rest SQLite via SQLCipher-compatible `e_sqlite3mc` (SQLitePCLRaw bundle). The passphrase flows through `SqliteConnectionStringBuilder.Password`. All claims below were MEASURED on macOS + net10 in the reference project
(2026-08-05).

## The raw-key channel (strongest option)

`Password = "x'<64-hex>'"` keys the bank with a RAW 256-bit key — **no KDF**, no passphrase brute-force surface. Verified end-to-end: create → reopen → wrong-key rejection (SqliteException code 26). Prefer raw keys over passphrases whenever
the key material can be a random/derived 32 bytes (file, vault, derivation). Passphrase mode is fine but weaker:
weak passphrases are brute-forceable offline (the header salt + HMAC are on disk).

## Deriving a key from an ed25519 SSH private key

- Parse the OpenSSH private key (`openssh-key-v1` magic, RFC 8709): ciphername must be
  `none`; the 32-byte seed is the first 32 bytes of the private half.
- Derive: `SHA-256("<stable-label>" || seed)` → raw 32 bytes → `x'<hex>'`. The label is a STABILITY CONTRACT — changing it silently breaks every existing bank.
- ed25519 only. RSA: private-key encodings are non-canonical (PKCS#1/PKCS#8/OpenSSH) → unstable derivation. Passphrase-protected keys: seed is bcrypt_pbkdf-encrypted → reject (detect the `bcrypt` cipher field).
- Test vectors must be pinned: synthetic seed `00..1f` → `x'277bf7…281b'` for label
  `<app>-db-key/v1`; generate a real `ssh-keygen -t ed25519` fixture for the second.

## Rekey constraints

- `PRAGMA rekey` with the target `x'…'` literal (build via `SELECT quote(?)` to avoid quoting bugs). Rekey legs verified: raw↔raw, passphrase→raw, plaintext→raw.
- **WAL is unsupported for rekey** — switch the journal to DELETE for the rekey, then back.
- Persist order for source switches: rekey the bank FIRST, then write the source marker, then mirror to settings — a crash mid-sequence must be recoverable (see retry legs below).

## Key-source architecture (pluggable providers)

Pattern for any encrypted store with multiple key sources (env / keychain / vault):

1. `IEncryptionKeyProvider` family: one provider per source, a resolver that picks by a marker, an eager open at startup so refusal-to-start is LOUD before serving.
2. **The pre-open selection problem**: the source selection cannot live inside the encrypted store (unreadable before the key). Use a small UNENCRYPTED sidecar next to the store (e.g. `memory.db.source`, JSON `{source, …}`): absence =
   default source; corrupt sidecar = loud error naming the file; atomic write (temp + rename); 0600. Settings rows inside the store are only a post-open mirror.
3. **Bootstrap problem for remote vaults**: the vault's own credential must be readable pre-open — existing CLI login state (`~/.azure`, `~/.aws`), OS keychain, or env. Never inside the encrypted store (circular). Interactive-at-start is
   rejected when the server is MCP-spawned (no terminal).
4. Crash-window retry legs: if the open with the new source fails but the old source's key still works (e.g. env passphrase), retry with it, report "bank is <old>-keyed; source not switched", and keep the marker consistent.

## bws (Bitwarden Secrets Manager CLI) integration

- `bws secret get <secret-id>` — use the secret ID (stable), not the name. Token:
  `BWS_ACCESS_TOKEN` env at server start; `-t <token>` on argv ONLY at config time, never persisted. 15 s timeout; non-zero exit → surface stderr; empty stdout → error.
- Presence check first: missing bws → actionable install-guidance error, nothing changed.
- Test with a fake-bws executable at an ABSOLUTE path (no PATH mutation): a script that validates a known token and prints a fixed ed25519 key.
- **Rotation trap**: rotating the secret in the Bitwarden web UI without `PRAGMA rekey`
  bricks the bank — the config command must warn.
- The C# SDK (`Bitwarden.Secrets.Sdk` 1.0.0) is beta, synchronous-only, custom license,
  ~7 MB native binary — prefer the CLI when available.

## Gotchas
- SecureString: deprecated (DE0001), no in-memory encryption on .NET Core — NOT a solution for key handling or storage.
- Env vars: visible via `/proc/<pid>/environ` on Linux (macOS hides them from `ps`); the bigger exposure is plaintext client configs and backups.
- Offline behavior for network sources: refuse to start loudly; an offline cache is an explicit opt-in, never a default.

## Dapper mapping pitfalls over SQLite3MC (MEASURED 2026-08-06)

Dapper's typed deserializer is built EAGERLY from the reader schema — it fails at materialization time even when zero rows would be returned:

- **`count(*)` columns are Int64.** A mapping record with `int Count` fails with
  "matching signature (String, Int64) is required". Use `long` in the Dapper mapping record and cast at the call site, or `ExecuteScalarAsync<int>` for plain counts.
- **Aggregates over an empty set come back NULL-typed → `byte[]`.** `avg(...)` with no rows, and `GROUP BY` with zero rows, make Dapper demand a ctor with
  `System.Byte[]` — the classic "parameterless default constructor or one matching signature (…, System.Byte[]) is required" error, thrown even when nothing is materialized. Fixes that work:
    - aggregates: `ExecuteScalarAsync<double?>` (NULL maps cleanly to null) — wrap the aggregate in `CAST(avg(…) AS REAL)` when the value is real;
    - empty-GROUP-BY tables: use the dynamic `QueryAsync()` (no type argument) and build the dictionary row by row — the dynamic path materializes per row and skips the eager typed deserializer entirely.
- Records with `double?` positional params don't match Dapper's ctor lookup for a
  `double` column — keep nullable aggregates on the scalar path, not in mapping records.

## References

- `references/bitwarden-bws-integration.md` — provisioned IDs, SDK facts, bootstrap ranking, and the full owner-decision record from an encryption-key-source work package; read when choosing an encryption-key source.
