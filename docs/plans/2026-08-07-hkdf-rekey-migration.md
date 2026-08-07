# HKDF rekey migration

Date: 2026-08-07
Status: Planned
Implements: [ADR-0012](../adr/0012-ssh-key-derivation-hkdf-replacement.md)
Branch: `task/hkdf-key-derivation` (draft PR #99)

## What this is for

ADR-0012 replaced `SHA-256(Label ‖ seed)` with platform HKDF-SHA-256. The derived
SQLCipher raw key changes bytes, so every bank encrypted through the Bitwarden/SSH key
source stops opening. The ADR left the migration to a separate work item: this one.

The ADR's binding constraint is one sentence — an existing bank opened with the old
derivation's key "must fail loudly (not silently produce garbage) if the rekey has not
run." Everything below is chosen to serve that.

Scope: the Bitwarden/SSH source only. The env passphrase path
(`AIRACCOON_DB_PASSPHRASE`) never went through `SshKeyDerivation` and must be observably
unaffected — that is an acceptance criterion, not an assumption.

## Where the seams actually are

Verified by reading the code on `task/hkdf-key-derivation` at `98195f6`, not from the
task description. Two things in the brief were wrong and are corrected here.

| Seam | State |
| --- | --- |
| `SshKeyDerivation.DeriveRawKey` / `DeriveLegacyRawKey` | Both present. HKDF and legacy constructions, same `Label`. |
| `Passphrase` (`Providers/IEncryptionKeyProvider.cs`) | `sealed record Passphrase(string Source)` with an init-only `Value`. **Not** a single-member record — it already carries the source name. |
| `BitwardenEncryptionKeyProvider.GetPassphrase` | Holds the parsed seed; can produce both keys. Sole call site of `DeriveRawKey` in production. |
| `EncryptionKeyResolver.Resolve` | Flattens `Passphrase` into `ResolvedKey(string? Passphrase, string SourceName)`. Any second key must be threaded through here too. |
| `SqliteConnectionFactory.OpenBankAsync` | `OpenBankWithKeyAsync(keyResolver.Resolve().Passphrase, …)`. |
| `SqliteConnectionFactory.RekeyBankAsync` | **Takes the current key from `keyResolver.Resolve().Passphrase`, not from a parameter** (`SqliteConnectionFactory.cs:68`). Post-ADR-0012 that resolves to the *HKDF* key, which is exactly the key a legacy bank will not open under. As written it cannot perform this migration. |

That last row is the one correction that changes the work. The brief says
`RekeyBankAsync` "is the primitive to build on. Do not write a second rekey path" — the
intent is right and the drain/DELETE-journal/verify body is reused unchanged, but the
method must gain an explicit *current* key parameter first. That is a parameterisation of
the existing path, not a second path.

`EncryptionCommands.BitwardenAsync` already performs a try-open / fall-back / rekey dance
for the secret-rotation case (`EncryptionCommands.cs:109-146`). The migration is the same
shape and belongs next to it, not somewhere new.

## Decision 1 — where the fallback lives

**Chosen: split by what each layer knows.** The provider emits both keys; the factory
does the probing and the rekey.

Only `BitwardenEncryptionKeyProvider` has the seed, so only it can produce the legacy key.
Only `SqliteConnectionFactory` opens connections, so only it can find out which key works.
Neither can do the other's half.

- `Passphrase` gains `LegacyValue` (init-only, null for env/none).
- `ResolvedKey` gains `LegacyPassphrase` (null for env/none).
- `SqliteConnectionFactory` gains the ladder in Decision 2 and a current-key parameter on
  `RekeyBankAsync`.

Rejected — **all of it in the resolver**: the resolver cannot open a connection, so it
cannot know which key is right. It would have to take a dependency on the factory that
constructs it, which is a cycle.

Rejected — **all of it in the provider**: same objection, plus it would put SQLite
connection handling inside a key provider, breaking the layering the providers exist to
maintain.

Rejected — **factory derives the legacy key itself**: the factory would need the seed, the
Bitwarden CLI and `SshKeyDerivation`, which drags `AiRaccoon.Core.Encryption` and the bws
runner into the connection factory. It also hard-codes "the fallback is an SSH key",
whereas carrying an opaque second key on `ResolvedKey` stays true for any future source.

Cost of the chosen shape: `Passphrase` and `ResolvedKey` grow a field that is null for two
of the three sources, which is mild dead weight on the env path. Accepted — the
alternative is a layering violation, and the field is one nullable string.

## Decision 2 — wrong key versus corrupt bank

This is the correctness question. Measured behaviour first.

A wrong-key open surfaces as `SqliteException: SQLite Error 26: 'file is not a database'`
thrown from `connection.OpenAsync()` inside `OpenWithPragmasAsync` — observed in the
current failing run of `EncryptionBitwardenIntegrationTests.EnvKeyedBank_RekeyedToDerivedKey_ReopensThroughFakeBwsWithDerivedKey`,
whose stack ends at `SqliteConnectionFactory.cs:131`. SQLITE_NOTADB (26) is also what a
truncated or garbage file produces, and a page-level corruption can produce either 26 or
SQLITE_CORRUPT (11).

**So the exception cannot distinguish the two cases, and the plan does not try to.**
Classifying `SqliteException` by error code or message would be guessing, and guessing
wrong here rekeys over a corrupt bank.

The discriminator is positive proof instead:

> A rekey is authorised only by a **successful open under the legacy key**. A successful
> open proves the file is a valid SQLCipher database *and* that the legacy key is its key.
> Nothing else authorises a rekey.

The ladder:

1. Open with the HKDF key. Success → return. The happy path is untouched and pays nothing.
2. `SqliteException` → the bank is legacy-keyed, or corrupt, or keyed to something else.
   Unknown. Nothing has been written.
3. No legacy key available (env source, none source, sidecar not `bitwarden`) → **rethrow
   the original exception unchanged**. Env banks keep their exact current behaviour.
4. Probe-open with the legacy key, minimally — open plus `PRAGMA quick_check`, *not*
   `OpenBankWithKeyAsync` (which runs `MemorySchema.EnsureAsync` and would create tables
   as a side effect of a diagnostic).
   - Probe fails → throw a dedicated `BankKeyMismatchException` naming both attempted
     derivations and the bank path. **No rekey.** Wrong-secret and corrupt-bank land in
     the same branch on purpose: the safe action is identical in both, and refusing is
     always safe where rekeying is not.
   - `quick_check` returns anything but `ok` → same refusal, different message. A bank that
     is legacy-keyed *and* damaged must not be rekeyed; rekeying it would add "you also
     need the new key" to an existing recovery problem.
   - Probe succeeds and `quick_check` is `ok` → proof obtained. Go to 5.
5. Rekey legacy → HKDF via `RekeyBankAsync(newKey: hkdf, currentKey: legacy)`, which
   drains the pool, rekeys on a DELETE-journal connection, and verifies by reopening.
6. Reopen normally with the HKDF key and return that connection.

Why this ordering matters beyond tidiness: in SQLCipher-family engines `PRAGMA rekey` on a
connection opened with the *wrong* key re-encrypts pages it never decrypted, which is
precisely the "silently produce garbage" outcome ADR-0012 forbids. Step 4 gating step 5 is
the whole safety argument, and it is why the rekey must never be attempted speculatively.

`quick_check` is O(bank size), but it runs only on the one open that already failed, and
it is strictly cheaper than the rekey it gates.

## Decision 3 — automatic on open, or an explicit CLI verb

**Chosen: explicit CLI verb (`ai-raccoon encryption migrate`). The automatic open path
detects and fails loudly; it does not rekey.**

Steps 1–4 above run automatically on every open — that is the detection, and it is what
turns a bare `SQLite Error 26` into an actionable `BankKeyMismatchException`. Step 5, the
rekey, runs only from the CLI verb.

Why not automatic:

- A rekey needs exclusive access. `RekeyBankAsync`'s own contract says "callers must not
  hold an open bank connection", and it calls `SqliteConnection.ClearPool`. The MCP server
  is long-lived and concurrent; firing a whole-file re-encryption from an arbitrary tool
  call, possibly from two processes at once, is a data-loss shape.
- Rewriting every page of an encrypted store is not something to do as a side effect of a
  read the user did not know would rewrite anything.
- Explicit is auditable and lets the user back the bank up first — for encryption-at-rest,
  a user who can choose the moment is worth more than a user who is spared a command.

What it costs: an agent whose MCP call hits an unmigrated bank gets a hard failure. The
mitigation is entirely in the message — `BankKeyMismatchException` names the exact command
to run. That is the deal being made, and it is only acceptable because the message is good.

**What would change this decision:** proof that the rekey can hold exclusive access
safely — an OS-level file lock over the bank that the open path can acquire and that any
other live process would fail to take. With that in hand, automatic-on-open becomes
friendlier at no correctness cost and I would switch. Absent that lock, automatic is a
race waiting for a user with two terminals open. A second thing that would change it:
if the owner judges a hard failure on upgrade unacceptable for a single-user local tool
and accepts the race — that is the owner's call to make, not the implementer's.

## Decision 4 — recording the migration, and crash-mid-rekey

**Chosen: record nothing. The bank is its own record.**

The ladder is self-describing and idempotent: a bank either opens under the HKDF key
(migrated) or under the legacy key (not). That state is authoritative, always current, and
cannot disagree with itself.

Rejected — **a `settings` row**: it lives *inside* the encrypted bank, so it cannot be read
until the key problem it describes is already solved. Useless exactly when needed.

Rejected — **a `keyVersion` field in the sidecar**: readable pre-open, but it introduces a
second source of truth that can drift from the bank. The dangerous drift is concrete — if
the sidecar says "migrated" and the bank is not (crash between rekey and sidecar write, or
a restored backup), the ladder would skip the legacy fallback and lock the user out of a
bank that was fine. A marker whose failure mode is "lock the user out" is worse than no
marker, and this is the exact outcome the whole work item exists to prevent.

Audit trail comes from a `[LoggerMessage]` at Information on the migrate path instead
(source, bank path, outcome), alongside the existing `RekeyingBank` / `BankRekeyed` events.

**Crash mid-rekey.** `PRAGMA rekey` runs on a DELETE-journal connection
(`OpenRekeyConnectionAsync` forces `journal_mode=DELETE`), so a crash leaves a rollback
journal and SQLite's recovery on next open rolls the file back to the legacy key. The
outcome is old-key-or-new-key, never a half-encrypted file.

Stated honestly: that atomicity argument is reasoned from SQLite's journal semantics and
**is not verified by a crash test here** — killing a process at the right instant inside
`PRAGMA rekey` is not something this test suite can do reliably, and building that harness
costs more than the risk it retires.

The real mitigation is not the journal, it is idempotent retry: because the ladder
re-probes on every open, a bank that rolled back to the legacy key is simply detected as
unmigrated again and the user re-runs the verb. Nothing needs to know a previous attempt
happened. This is also the strongest argument for Decision 4 — a migration that records
nothing has nothing to leave inconsistent.

One residual: `RekeyBankAsync` verifies by reopening, and if that verification throws, the
bank's state is genuinely unknown (rekey may have landed and the reopen failed for another
reason). The exception propagates as-is; the next open re-probes both keys and resolves it.
Documented rather than handled, because there is no action to take that re-probing does not
already take.

## Failure modes

| # | Situation | Behaviour |
| --- | --- | --- |
| 1 | Bank already HKDF-keyed | Opens at step 1. No probe, no cost. |
| 2 | Bank legacy-keyed, healthy | Step 4 proves it; verb rekeys; opens under HKDF. Legacy key no longer opens it. |
| 3 | Bank genuinely corrupt | Neither key opens it → `BankKeyMismatchException`. **Never rekeyed.** |
| 4 | Bank legacy-keyed but damaged | Legacy opens, `quick_check` fails → refusal. **Never rekeyed.** |
| 5 | Wrong Bitwarden secret (rotated) | Neither key opens it → same refusal as #3. Correct: the safe action is identical. |
| 6 | Env-keyed bank, env source | No legacy key on `ResolvedKey`; step 3 rethrows the original `SqliteException`. Unchanged behaviour. |
| 7 | Env-keyed bank, sidecar says bitwarden | Both derived keys fail → refusal. Existing `EncryptionCommands.BitwardenAsync` env-recovery path is untouched. |
| 8 | Crash mid-rekey | Journal rolls back to legacy key; next open re-detects; verb re-run completes it. |
| 9 | Two processes migrate at once | Pool drain plus SQLite file locking serialises them; the loser's rekey fails against an already-HKDF bank and surfaces as an error, not corruption. The CLI-verb choice (Decision 3) is what keeps this rare. |
| 10 | Bank file absent | Unchanged — `ReadWriteCreate` creates a fresh HKDF-keyed bank. No probe (nothing failed). |

## Tests

Written failing first, in this order.

**Migration gate (the acceptance test)** — `EncryptionBitwardenIntegrationTests`:

1. `LegacyKeyedBank_MigrateVerb_RekeysToHkdfKey` — create a bank under
   `DeriveLegacyRawKey`, write a known row, run the migrate path, then assert: opens under
   `DeriveRawKey`, the row is intact, and opening under `DeriveLegacyRawKey` now throws.
   Both halves are required — "opens under the new key" alone would pass against a bank
   that was never encrypted.

**Refusal**:

2. `CorruptBank_Migrate_ThrowsAndDoesNotRekey` — write a file of garbage bytes at the bank
   path; assert `BankKeyMismatchException` and assert the file bytes are **unchanged**
   afterwards. The byte comparison is the real assertion; the exception type alone does not
   prove nothing was written.
3. `LegacyKeyedBankFailingQuickCheck_Migrate_Refuses` — legacy-keyed bank with corrupted
   interior pages; assert refusal and no rekey.
4. `WrongSecret_Migrate_ThrowsBankKeyMismatch` — bank keyed to an unrelated key.

**Open path**:

5. `OpenBankAsync_HkdfKeyedBank_DoesNotProbeLegacy` — the legacy key is never derived on
   the happy path (assert via the fake bws runner's call count).
6. `OpenBankAsync_LegacyKeyedBank_ThrowsActionableMismatch` — the automatic path detects
   and refuses, and the message names the migrate command. This is Decision 3 pinned as a
   test: if someone later makes the open path rekey, this fails.
7. `OpenBankAsync_EnvKeyedBank_WrongPassphrase_RethrowsOriginalSqliteException` — the env
   path is untouched, including the exception type.

**Unit**:

8. `BitwardenEncryptionKeyProviderTests` — `GetPassphrase` returns both values, and
   `LegacyValue` matches the independently derived legacy vector.
9. `EncryptionKeyResolverTests` — `ResolvedKey.LegacyPassphrase` is populated for
   bitwarden, null for env and none.
10. `SqliteConnectionFactoryEncryptionTests` — `RekeyBankAsync` with an explicit current
    key, including that a wrong current key throws before any rekey is attempted.

**Regression**: `NoHandRolledCryptoTests` stays green. No new crypto-primitive call sites
are expected — the migration composes existing derivations and adds none. If that turns out
to be wrong, the new site goes on the documented allowlist with a reason; the test is not
weakened.

## Acceptance criteria

1. `dotnet test --filter "FullyQualifiedName~Encryption"` is green, including the eleven
   repinned vectors and every test above.
2. Test 1 passes and is proven meaningful by having been watched to fail first.
3. Test 2 passes and asserts byte-level non-modification of the corrupt file.
4. `NoHandRolledCryptoTests` green, allowlist unweakened.
5. Env-path behaviour is unchanged, evidenced by test 7 asserting the original exception
   type rather than a new one.
6. No second rekey implementation — `RekeyBankAsync` remains the only one; the diff shows a
   signature change, not a new method with a rekey body.
7. Every pinned key vector in the suite was derived independently, not copied from
   implementation output.

## Out of scope

Cloud sync interaction with rekeyed banks; rekeying workspace sandboxes separately (they
live in the same `memory.db`); rotating the Bitwarden secret itself (already handled by
`encryption bitwarden`); a crash-injection harness for `PRAGMA rekey` (see Decision 4).
