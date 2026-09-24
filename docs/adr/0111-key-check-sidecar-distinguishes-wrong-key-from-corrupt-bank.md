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

### D1 — Content: a bank-id, a per-bank salt and work factor, and a keyed tag — never the key

The sidecar holds four fields, concatenated: a random 16-byte bank-id, a random 16-byte salt, a
4-byte big-endian iteration count, and a 32-byte tag, `HKDF-SHA256(key material, info: domain ‖
bank-id)`, where `domain = "ai-raccoon-keycheck/v1"`. HKDF's own construction is already
HMAC-based internally, so folding the bank-id into `info` alongside the domain binds the tag to
(key material, domain, bank-id) directly, with no separate hand-assembled derive-then-HMAC step on
top (the repo's no-hand-rolled-crypto gate,
`NoHandRolledCryptoTests.RawHashPrimitives_AppearOnlyOnDocumentedSites`, reserves that two-step
shape for a site that *applies* a key a distinct earlier HKDF call already produced, such as
`SyncBlobAuthenticator`, not for a fresh derivation like this one). The `info` string is a domain
distinct from the ones ADR-0012's `SshKeyDerivation` and `SyncBlobAuthenticator` already use for
their own purposes — the same platform-primitive pattern, never a hand-rolled scheme
(`KeyCheckSidecar.cs`). Verification recomputes the tag from a candidate key and compares with
`CryptographicOperations.FixedTimeEquals`. The sidecar can prove a key wrong or right without ever
being able to reveal or reconstruct that key.

### D1a — A leaked sidecar must not be an HMAC-speed dictionary oracle on a human passphrase

Found in review (#729): the shape above, taken straight into HKDF over the raw key bytes, is a
single fast HMAC-family call. For the `env` source that raw input is a human-typed
`AIRACCOON_DB_PASSPHRASE` — low entropy by construction — so a leaked 48-byte (now larger) sidecar
let an attacker brute-force it at HKDF speed instead of paying SQLCipher's own PBKDF2 cost
(`kdf_iter`, 256,000 by default). `key material` above is therefore not always the raw key bytes:
`ComputeTag` first checks whether the candidate key is SQLCipher's raw-key literal shape
(`x'<hex>'`, case-insensitive on the `x`, even-length valid hex) — already 32 high-entropy bytes
with nothing to dictionary-attack — and only when it is *not* that shape does it pre-stretch the
key with `Rfc2898DeriveBytes.Pbkdf2` (SHA-256, the stored salt, the stored iteration count, ≥
256,000) before folding the result into the same HKDF step. The iteration count is stored per
record rather than assumed from a constant, so raising `WorkFactorIterations` later never
invalidates a sidecar minted under the old floor — it keeps verifying at the count it was minted
with. A candidate key's shape alone decides which path a verify takes, exactly mirroring the mint:
the true key always takes the same path at both ends, so correctness never depends on knowing the
source out of band, and no source metadata needs threading into `SqliteConnectionFactory`'s call
sites. Rejected: stretching every key unconditionally (measured ~43ms per PBKDF2-SHA256 call at
256,000 iterations on osx-arm64/M-series — negligible for a raw 32-byte key that needed none of it,
but every source pays it regardless of whether anything is actually low-entropy); threading the
resolved key's `SourceName` through every call site instead of inspecting the key's own shape (more
plumbing, for a distinction the key string already reveals unambiguously). `KeyCheckSidecarTests`
proves the stored tag cannot be reproduced by the pre-fix (unstretched) computation and that a raw
key literal skips the stretch; `NoHandRolledCryptoTests.ObsoletePasswordKdfs_AreNotUsed` narrows to
ban only the obsolete `Rfc2898DeriveBytes` instance constructor and `PasswordDeriveBytes` — the
modern static `Pbkdf2(...)` one-shot method (explicit hash, explicit iteration count) is not
obsolete and is `KeyCheckSidecar`'s one documented caller.

**Per-open cost, measured**: `SqliteConnectionFactory.EnsureKeyCheck` runs on every bank open (most
store operations open a bank connection per call — D3 below), not once per process, so this ~43ms
lands on every `env`-passphrase open, every time. Accepted as the security/latency trade-off this
finding asks for; a raw-key or Bitwarden-derived source pays nothing extra (the shape check skips
the stretch for those). Revisit with a process-lifetime cache keyed on the resolved key if this
proves user-visible in practice — not built here, since nothing yet shows it is needed.

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

### D4a — Only SQLITE_NOTADB (26) is the ambiguous shape; anything else propagates as itself

Found in review (round 5, #729): `NoLegacyDerivationToTry`, `NotLegacyKeyed`, and
`MigrateLegacyKeyAsync`'s own inline no-legacy-derivation branch each unconditionally wrapped
*any* `SqliteException` reaching them into `BankKeyMismatchException` — not only SQLITE_NOTADB.
`OpenBankWithKeyAsync`'s `?? openFailure` already got this right; these three did not.
Reproduced deterministically (no timing race needed): pointing `BankPath` at an existing directory
makes every open fail with SQLITE_CANTOPEN (14), not 26, and a resolved key with no legacy
derivation (`env`, the common case) turned that into a confidently-wrong "wrong key or corrupt"
verdict. `AmbiguousOrOriginal(openFailure, ambiguous)` centralizes the guard the three call sites
lacked: only SQLITE_NOTADB is entitled to the ambiguous both-causes diagnosis; every other code
(SQLITE_BUSY, SQLITE_CANTOPEN, …) propagates as the original, correctly-coded exception —
`CliFailureErrorCode` already maps those to `Bank.Busy`/`Bank.OpenFailed`, distinct from
`Key.WrongKey`, so this is a strictly more accurate diagnosis, not merely "less wrong."

### D5 — A crash mid-rekey self-heals; a concurrent *opener* mid-rekey does not, on its own

D3's "a crash between `PRAGMA rekey` and the reopen heals itself on the next successful open"
is true for the rekeying caller, but it understates a second actor: between the pragma landing and
`RekeyBankAsync`'s verify-reopen rewriting the sidecar, a *different* opener still holding the old
key hits the newly-rekeyed bytes as `SQLITE_NOTADB`, reads the sidecar — still verifying the old
key, because the rewrite has not run yet — and D4's rule reads that as "tag matches → corrupt".
The verdict is confident-looking and wrong: the bank is fine, mid-rekey, under a different key.
Found in review (#729).

### D6 — A rekey marker makes that window ambiguous instead of confidently wrong

`RekeyMarker` (`RekeyMarker.cs`) is a second sidecar-adjacent file, `memory.db.rekeying`, holding no
content — its only signal is whether it exists. `RekeyBankAsync` marks it before `PRAGMA rekey` and
clears it in a `finally` spanning the whole operation (success or failure), so it is present for
exactly D5's window and never longer. `TryDiagnoseWithKeyCheck` and `DoctorCommands`'s
`ReportNotADatabaseAsync` both check it before returning the confident `BankCorruptedException` (a
sidecar mismatch → wrong-key verdict is untouched, since that is correct regardless of a rekey in
flight): marker present → fall through to the same both-causes ambiguous default a missing sidecar
already gets, never a new exception shape. Rejected: holding a lock across the rekey and every
opener (the callers are not all in the same process — `encryption migrate`/`rekey` is a CLI
one-shot, the bank is typically also open in a running `serve` process — so an in-process lock does
not close the window between processes, while a file-existence check does); rewriting the sidecar
*before* the pragma (a concurrent opener that still succeeds under the old key, because the rekey
has not landed yet, would then re-win the sidecar back to the old key on its own successful open,
reopening the same window from the other direction). Tested by reproducing the exact gap
deterministically — the pragma applied directly and the marker set by hand, not two racing threads
— red before the marker check existed, green after.

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
- A rekey now leaves a third file next to the bank for its duration only: `memory.db.rekeying`
  (D6). It never survives a completed or failed `RekeyBankAsync` call, so it adds no new steady
  state to reason about — only a narrower window in which "corrupt" is downgraded to "ambiguous".

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
`tests/AiRaccoon.Tests/Unit/Encryption/NoHandRolledCryptoTests.cs`,
`tests/AiRaccoon.Tests/Integration/Storage/SqliteConnectionFactoryEncryptionTests.cs`,
`tests/AiRaccoon.Tests/Integration/EncryptionBitwardenIntegrationTests.cs`,
`tests/AiRaccoon.Tests/Unit/Setup/DoctorCommandsTests.cs`,
`tests/AiRaccoon.Tests/Unit/Setup/CommandFailureExitCodeTests.cs`.
