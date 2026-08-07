# 0012 — Replace the hand-rolled SSH-key derivation with platform HKDF

Date: 2026-08-07

Status: Accepted (decision to replace; implementation + rekey migration is a separate work item)

## Context

`SshKeyDerivation.DeriveRawKey` (`src/AiRaccoon.Core/Encryption/SshKeyDerivation.cs`)
composes the SQLCipher raw key for the Bitwarden-backed encryption source as:

```
raw = SHA-256(Label ‖ seed)
```

where `Label = "ai-raccoon-db-key/v1"` and `seed` is the 32-byte ed25519 private-key seed
extracted by `OpenSshPrivateKeyParser`. This is a hand-assembled domain-separation
construction, not a call into an audited KDF: it concatenates a label and a secret and
runs a single hash pass, rather than using a construction designed and reviewed for this
purpose. `docs/work/archive/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md:126` flagged
this exact gap at research time: whether "the labelled derivation composition... violates
the repo's 'no hand-rolled crypto or security orchestration' invariant... deserves an ADR
or an explicit carve-out" — and no ADR was written before the code shipped
(`BitwardenEncryptionKeyProvider` calls it in production today).

The repo's invariant is explicit: "Never implement security/cryptographic orchestration
yourself — key derivation, token signing, session/cookie protection, encryption-at-rest
schemes. Delegate to an audited, platform-provided library rather than composing audited
primitives into your own protocol, even when the primitives themselves are sound." A
label-prefixed single-hash construction is exactly that kind of composition: SHA-256
itself is sound, but concatenate-and-hash as a domain-separation scheme is a protocol
decision this codebase invented rather than adopted.

## Decision

**Replace** `SHA-256(Label ‖ seed)` with the platform's `HKDF-SHA-256`
(`System.Security.Cryptography.HKDF`), using the seed as input keying material (IKM), no
salt, and the existing label (`"ai-raccoon-db-key/v1"`) as the HKDF `info` parameter —
the same construction the research doc's F1 already identified as the sound alternative,
following the pattern `filippo.io/age`'s SSH-key support uses. HKDF is the audited,
purpose-built primitive for exactly this "derive a key from secret material plus a
domain-separation label" problem; the hand-rolled hash-and-concatenate composition is not
kept as a supported path, carve-out, or fallback.

This is a decision to replace, not to bless the status quo: the owner has ruled the
hand-rolled composition must go, in favor of platform HKDF. Implementing the change and
the accompanying rekey migration is explicitly **out of scope for this ADR** — a separate
work item owns it.

**Consequence for existing banks:** because the derived raw key changes bytes (a
different KDF construction over the same seed produces a different key), every bank
already encrypted via the SSH/Bitwarden key source needs a `PRAGMA rekey` pass to the
newly-derived key before it can open under the new derivation. The implementing work item
must ship this as a migration, not a silent break — an existing encrypted bank opened
with the old derivation's key must fail loudly (not silently produce garbage) if the
rekey has not run.

## Consequences

- Closes the carve-out gap flagged in the 2026-08-05 research doc: the derivation is now
  backed by an audited KDF, consistent with the "no hand-rolled crypto" invariant, instead
  of living as an unreviewed exception to it.
- `SshKeyDerivation.Label` and its value stay unchanged — only the construction changes
  (HKDF `info` instead of raw concatenation-then-hash) — so the domain-separation intent
  is preserved.
- Every existing bank using the Bitwarden/SSH key source requires a `PRAGMA rekey` before
  it opens under the new derivation; this is a breaking change for those banks until the
  migration ships, tracked as a separate implementation work item.
- The env-var passphrase path (`AIRACCOON_DB_PASSPHRASE`) is unaffected — it never went
  through `SshKeyDerivation`.

**Evidence:** `src/AiRaccoon.Core/Encryption/SshKeyDerivation.cs:9-27` (current
construction); `src/AiRaccoon.Infrastructure/Sqlite/Encryption/Providers/BitwardenEncryptionKeyProvider.cs`
(production call site); `docs/work/archive/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md:22,126`
(F1's HKDF alternative and the flagged-but-unwritten ADR gap).
