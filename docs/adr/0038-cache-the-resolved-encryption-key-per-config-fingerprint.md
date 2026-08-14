# 0038. Cache the Resolved Encryption Key Per Config Fingerprint, Scoped to the Resolver Instance

Date: 2026-08-14

## Status
Accepted

## Context
`SqliteConnectionFactory.OpenBankAsync` calls `IEncryptionKeyResolver.Resolve()` on every bank open,
and every store method opens a bank connection per operation (.NET-F2). `EncryptionKeyResolver`
is documented as reading the `memory.db.source` sidecar fresh on every call — deliberately, because
a config command can change the source between calls. For the `bitwarden` source,
`BitwardenEncryptionKeyProvider.GetPassphrase` shells out to `bws secret get` — a blocking
`Process.Start` round-trip with a 15 s timeout — meaning every memory read and write on a
Bitwarden-keyed install paid a full CLI invocation, and that invocation blocked a thread-pool
thread synchronously inside what should have been an async chain.

## Decision
1. **Cache the resolved key**, not the sidecar read. `EncryptionKeyResolver` still calls
   `IEncryptionSourceSidecar.Read()` on every `ResolveAsync()` — that stays a cheap local file
   read, so a config change (source/project/secret id edited between calls) is always seen. Only
   the expensive step — the provider's `GetPassphraseAsync`, which is what actually shells out —
   is skipped when the sidecar's contents are unchanged since the last resolve.
2. **Fingerprint = `Source, ProjectId, SecretId`** (`EncryptionData`'s full non-secret shape). Any
   change to any of the three invalidates the cache and forces a re-fetch. Nothing else about the
   sidecar (there is nothing else) participates.
3. **Cache lifetime = the resolver instance's lifetime**, not global and not time-based.
   `EncryptionKeyResolver` is registered as a DI singleton (`AppRegistrations.cs`), so in the
   long-running MCP server process this means "once per process, until the config changes" — the
   shape the finding asked for. A one-shot CLI verb (`encryption bitwarden`, `maintenance vacuum`)
   gets its own resolver per process invocation, so it always pays exactly one CLI round trip,
   never zero.
4. **Concurrent resolves serialize through a `SemaphoreSlim(1,1)`**, not just a read-check. A cache
   miss during concurrent bank opens (e.g. the watch digest's concurrency-4 fan-out) must produce
   exactly one CLI invocation, not one per racing caller.
5. **`BitwardenCliSecretManager.RunAsync` is genuinely async**: `Process.WaitForExitAsync` +
   awaited `StandardOutput`/`StandardError` reads, no `.GetAwaiter().GetResult()`. Caching alone
   would still block a thread on the one invocation per config; async alone would still pay a full
   CLI round trip per operation. Both were required — this ADR records that both landed together.

The whole `IEncryptionKeyResolver` / `IEncryptionKeyProvider` / `ICliSecretManager` chain became
async as a consequence (`Resolve()` → `ResolveAsync()`, `GetPassphrase()` → `GetPassphraseAsync()`,
`Run()` → `RunAsync()`), threading a `CancellationToken` through `SqliteConnectionFactory`,
`AppRunner`, `NodeRunner`, `AppRegistrations`' sync-snapshot connection strings, and
`EncryptionCommands`' direct provider/CLI calls.

## Consequences
- **Positive**: a Bitwarden-sourced install pays one `bws` invocation per process (or per config
  change), not one per memory operation — the dominant fix for .NET-F2.
- **Positive**: the one invocation that does happen no longer parks a thread-pool thread for up to
  15 s; it awaits real I/O.
- **Positive**: an already-resolved key survives `bws` becoming unavailable mid-process — the
  derived key material doesn't expire, so re-verifying CLI availability on every operation was
  never buying correctness, only cost.
- **Negative**: `bws` becoming unavailable is only detected on the *next* resolve after a config
  change (or on the process's first resolve) — a long-running server will not notice mid-process.
  This is intentional per the finding's own framing ("cache what the existing resolver already
  returns") and was confirmed against the BDD scenario `bws missing at server start fails loudly`,
  which now simulates a fresh process (fresh resolver, cold cache) rather than reusing the test
  fixture's already-warmed shared resolver instance.
- **Negative**: every call site of the three interfaces changed shape (async, `CancellationToken`),
  touching files outside the immediate encryption stack (`AppRegistrations.cs`, `ProbeExtensions.cs`,
  `NodeRunner.cs`, `EncryptionCommands.cs`) — a wider mechanical blast radius than the caching
  change alone would have needed, traded for closing the blocking-call defect for real rather than
  moving it one level down.
