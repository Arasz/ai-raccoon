# 0111 — A key-check sidecar distinguishes a wrong key from a corrupt bank

Date: 2026-09-24

Status: Accepted

## Context

SQLCipher answers `SQLITE_NOTADB` identically for a wrong encryption key and for a file that
genuinely is not a database (ADR-0107 PC.0, "Not yet distinguished" item 1). Since #700/#721 the
message names both causes and both remedies, but the *exit code* still depends only on which call
path hit the failure — `doctor` always reports `Bank.Corrupted` (32); a command that resolves the
key and finds no legacy derivation to try always reports `Key.WrongKey` (21) — never the case the
failure actually is. Splitting the code needs a signal that lives outside the encrypted pages
themselves, since everything inside them is exactly what a wrong key also fails to produce.

## Decision

A sidecar, `memory.db.keycheck`, sits next to the bank (`BankPaths.DirectoryFor`) and proves which
key last opened it successfully, independent of the bank's own (possibly corrupt) contents.

### D1 — Content: a bank-id and a keyed tag, never the key

The sidecar holds two fixed-length fields, concatenated: a random 16-byte bank-id (minted once,
alongside the tag) and a 32-byte tag, `HKDF-SHA256(the raw bank key, info: domain ‖ bank-id)`,
where `domain = "ai-raccoon-keycheck/v1"`. One platform-KDF call does the whole job — HKDF's own
construction is already HMAC-based internally, so folding the bank-id into `info` alongside the
domain binds the tag to (key, domain, bank-id) directly, with no separate hand-assembled
derive-then-HMAC step on top (the repo's no-hand-rolled-crypto gate,
`NoHandRolledCryptoTests.RawHashPrimitives_AppearOnlyOnDocumentedSites`, reserves that two-step
shape for a site that *applies* a key a distinct earlier HKDF call already produced, such as
`SyncBlobAuthenticator`, not for a fresh derivation like this one). The `info` string is a domain
distinct from the ones ADR-0012's `SshKeyDerivation` and `SyncBlobAuthenticator` already use for
their own purposes — the same platform-primitive pattern, never a hand-rolled scheme
(`KeyCheckSidecar.cs`). Verification recomputes the tag from a candidate key and compares with
`CryptographicOperations.FixedTimeEquals`. The sidecar can prove a key wrong or right without ever
being able to reveal or reconstruct that key.

### D2 — File handling: 0600, refuse-not-chmod, atomic writes

The sidecar is written owner-only (0600) and follows the same refuse-never-chmod stance the
state-directory secrets take (`IdentityKeyFile`, `McpTokenFile`, `OwnerOnlyFile`): an existing file
another principal can read or write is refused with a `chmod 600` remedy, not silently tightened and
trusted. `SqliteConnectionFactory` and the top-level `AiRaccoon` project sit on opposite sides of the
project reference graph (Infrastructure cannot depend on the host project without inverting it — the
clean-layering invariant), so `KeyCheckSidecar` (`AiRaccoon.Infrastructure.Sqlite.Encryption`)
re-implements the same small ownership check rather than sharing `OwnerOnlyFile` — it does not need
that type's cross-process mint/heal protocol, only the file-mode refusal. A fresh sidecar is created
with `FileMode.CreateNew` (never overwrites); a rewrite goes through a temp file (0600) plus
`File.Move(overwrite: true)`, matching `EncryptionSourceSidecar`'s existing atomic-write idiom.

### D3 — When it is minted or rewritten: every successful keyed open

Every open that succeeds through `SqliteConnectionFactory`'s single connection chokepoint
(`OpenConnectionAsync`) checks the sidecar for the key that just worked: absent → minted silently;
present and already matching → untouched; present and no longer matching → rewritten, logged once
(`SqliteConnectionFactory.Log.KeyCheckRewritten`, `EventId` 905). This one rule, applied uniformly,
covers rekey and legacy migration for free — `RekeyBankAsync` and `MigrateLegacyKeyAsync` both
reopen with the new key through the same chokepoint at the end of the operation, so the rewrite
happens as a side effect of "the new key just opened the bank successfully," not as a special case
either method has to know about. A crash between the SQLCipher `PRAGMA rekey` and that reopen heals
itself the same way: the next successful open under the new key rewrites the stale sidecar. A
sidecar that cannot be minted or rewritten (permissions, disk) never fails the open itself — it is a
diagnostic aid layered on an already-hot path (nearly every store operation opens a bank connection),
not a new availability dependency; the failure is logged (`KeyCheckUnavailable`, `EventId` 906) and
the open proceeds exactly as it would have before this ADR.

### D4 — When it decides the diagnosis: after the legacy-derivation check, never before

On a `SQLITE_NOTADB` open failure, a present sidecar gives a confident verdict only once the
existing pre-ADR-0012 legacy-derivation check (ADR-0012, the 2026-08-07 rekey migration) has already
run and found nothing: a bank still under the legacy derivation is not "wrong key," and that
detection must not be short-circuited by a sidecar minted under either the legacy or the current key
at some earlier point. With that check exhausted, a present sidecar settles the remaining ambiguity:
tag mismatches the resolved key → `BankKeyMismatchException` (wrong key, exit 21 — `Key.WrongKey`),
message naming the key, never corruption; tag matches → the new `BankCorruptedException` (exit 32 —
`Bank.Corrupted`), message naming corruption only, never the key. A missing or untrustworthy sidecar
(absent, unreadable, not owner-only) falls back to each call path's own pre-existing ambiguous,
both-causes message and code — unchanged, including for the many pre-ADR-0111 banks that were never
opened again after upgrading and so never get a sidecar minted retroactively.

`doctor` (`DoctorCommands.cs`) applies the same three-way read but never mints or rewrites — it is
read-only by construction, and inspecting a bank must not have the side effect of changing what a
later confident diagnosis depends on.

## Consequences

- A wrong key and a genuine corruption are told apart with certainty once a bank has opened
  successfully at least once since this ADR, on every call path that opens the bank (not only
  `doctor` or `encryption migrate`) — `SqliteConnectionFactory.OpenBankWithKeyAsync`, used by
  `serve`'s startup probe, now raises the same typed exceptions `OpenBankWithResolvedKeyAsync` does,
  where it previously let a raw `SqliteException` propagate unexamined.
- A bank with no sidecar (never opened since upgrading, or the sidecar was deleted) keeps today's
  ambiguity exactly as before — this ADR narrows the gap over time as banks are opened, rather than
  requiring a migration step.
- Two new `[LoggerMessage]` `EventId`s (905, 906) extend `SqliteConnectionFactory`'s existing
  901-904 block; `docs/reference/logging-event-ids.md`'s count and table are updated with them.

## Alternatives rejected

- **A plaintext or keyed header field inside the bank file.** Still needs a keyed check to resist
  tampering, so it buys nothing over a sidecar, and adds pragma/format churn to a file whose header
  bytes SQLCipher already claims.
- **An attached companion database.** Heavier than a small fixed-length sidecar for no extra
  guarantee — it is still a second file next to the bank, just a SQLite one.
- **Verifying on the bank's structural contents (schema shape, `quick_check`).** This is exactly the
  signal that is unavailable when the key is wrong — a wrong key and genuine corruption both make
  every structural read fail the same way, which is the problem this ADR exists to solve.

## Evidence

`tests/AiRaccoon.Tests/Unit/Encryption/KeyCheckSidecarTests.cs`,
`tests/AiRaccoon.Tests/Integration/Storage/SqliteConnectionFactoryEncryptionTests.cs`,
`tests/AiRaccoon.Tests/Integration/EncryptionBitwardenIntegrationTests.cs`,
`tests/AiRaccoon.Tests/Unit/Setup/DoctorCommandsTests.cs`,
`tests/AiRaccoon.Tests/Unit/Setup/CommandFailureExitCodeTests.cs`.
