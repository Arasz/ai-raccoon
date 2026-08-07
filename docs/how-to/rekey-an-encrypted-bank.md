# Rekey an encrypted bank

[ADR 0012](../adr/0012-ssh-key-derivation-hkdf-replacement.md) replaced the bank's
SSH-key-derived SQLCipher key with platform HKDF-SHA-256. Existing banks that were
keyed via `ai-raccoon encryption bitwarden` before that change still carry the old
derivation and need a one-time rekey to open under the new one.

## Who is affected

Only banks using the **Bitwarden/SSH key source**. If your bank is keyed by the
`AIRACCOON_DB_PASSPHRASE` environment variable, this does not apply — that path never
went through `SshKeyDerivation` and is unaffected by ADR 0012.

If you are not sure which source your bank uses:

```bash
ai-raccoon encryption show
```

## Before you start: stop the server

The rekey needs exclusive access to the bank file — `PRAGMA rekey` drains the
connection pool and rewrites every page. The MCP server must not be holding the bank
open while you run it. Stop any running `ai-raccoon serve` process (or disconnect the
stdio client) first.

This is why the migration is a separate, explicit command rather than something that
runs automatically on open: opening the bank while it is still on the old derivation
already fails loudly and safely (see below) — the rekey itself only happens when you
ask for it.

## Run the migration

```bash
ai-raccoon encryption migrate
```

## What happens

The command probes the bank and produces exactly one of three outcomes:

1. **Bank rekeyed.**

   ```
   bank rekeyed to the current key derivation
   ```

   The bank opened under the old derivation, and only the old derivation — proof it was
   safe to rekey. It is now rekeyed to the current HKDF key and reopens under it; the
   old derivation no longer opens it.

2. **Already on the current derivation — nothing to do.**

   ```
   bank is already on the current key derivation; nothing to do
   ```

   The bank already opened under the current HKDF key. Nothing was touched. Safe to
   run this command speculatively, including on a bank you're not sure about.

3. **Refusal.**

   ```
   ai-raccoon: the bank at '<path>' opens under neither the current nor the pre-ADR-0012
   bitwarden key derivation — it is corrupt, or keyed to a different secret. It has not
   been modified; restore it from a backup or check that the encryption source is right.
   ```

   Neither key opened the bank (wrong Bitwarden secret, or a damaged file), or the
   legacy key opened it but `PRAGMA quick_check` found it unhealthy. **The file is left
   byte-identical in every refusal case** — a rekey is only ever attempted after
   positive proof that the legacy key opens a healthy bank, so refusing is always safe.
   It is safe to retry the command after fixing the underlying problem (correcting the
   configured secret, restoring from backup, re-running `ai-raccoon encryption
   bitwarden` if the secret was rotated).

If you don't run the migration, the server keeps working normally as long as it can
open the bank — an unmigrated bank surfaces the same refusal the day-to-day open path
would hit, naming this command as the fix.

## See also

- [ADR 0012 — SSH-key derivation → HKDF](../adr/0012-ssh-key-derivation-hkdf-replacement.md)
- [`encryption` command reference](../reference/agent-memory-server.md#command-line-options)
