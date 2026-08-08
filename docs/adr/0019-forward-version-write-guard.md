# 0019 — Forward-version write guard

Date: 2026-08-09

Status: Accepted

## Context

One bank (`memory.db`) serves every project on the machine, and every process that opens it
read-write runs `MemorySchema.EnsureAsync` (`SqliteConnectionFactory.InitializeAsync`) — the
schema-version ladder ADR-0011 established. Before this decision, `EnsureAsync` only ever moved
the stored version forward: it had no case for a stored version *ahead of* the binary's own
`CurrentVersion`. During any rollout where more than one `ai-raccoon` binary version is in use
against the same bank — a background service upgraded before a CLI shim, two machines syncing
against one bank, a tool client pinned to an older install — an older binary would open a bank a
newer binary had already migrated, read a stored version it didn't recognize as "ahead," and
silently no-op past the version check. It then wrote through write paths that skip that newer
schema's maintenance.

That gap is exactly what caused issue #200: 193 rows across roughly 10 `source_file` groups were
left with `chunk_index`/`total_chunks` at their `0`/`0` defaults after a stale binary wrote into a
bank a newer binary had already stamped at v2. The corruption was silent — no error, no log line
pointing at the cause — and was only found by directly auditing the stored values.

## Decision

`MemorySchema.EnsureAsync` refuses the open when the stored version is newer than the binary's
`CurrentVersion`, instead of proceeding:

```csharp
if (storedVersion > CurrentVersion)
{
    throw new UnsupportedSchemaVersionException(
        $"bank schema v{storedVersion} is newer than this binary supports (v{CurrentVersion}); update ai-raccoon");
}
```

- This is the only surface that needs the check: the codebase has no separate read-only bank
  open, and `EnsureAsync` runs on every read-write open and nowhere else — closing this one path
  closes it for every writer (CLI verbs, MCP tools, the file watcher, sync merges).
- `UnsupportedSchemaVersionException` (`AiRaccoon.Core.Memory`) is a plain `InvalidOperationException`
  subtype, not mapped in `ToolRefusals.RefusalPrefixes`
  (`src/AiRaccoon/Tools/ToolRefusals.cs`). It is therefore a genuine failure, not a typed refusal:
  it escapes as a normal MCP tool-call error logged at `Error`, per the existing "anything
  `ToolRefusals` does not recognize" contract (`docs/reference/agent-memory-server.md`).
- The message names both versions and tells the operator the fix in one sentence: update
  `ai-raccoon`. No self-healing or downgrade path is attempted — the schema ladder only ever
  moves forward (ADR-0011), so an older binary cannot safely interpret a newer shape.
- Shipped alongside `CurrentVersion` 2 → 3, a new hard ladder step (`MigrateToV3Async`) that
  reuses `MemorySql.RecomputeChunkColumnsBankWide` — the same bank-wide recompute
  `SyncService.MergeRemoteAsync` already calls — to self-heal any bank written during the
  pre-guard mixed-version window, including the rows #200 found.

## Consequences

- **Positive.** The exact silent-corruption mechanism behind #200 can no longer recur: a stale
  binary now hard-fails loudly on its first write against a newer bank instead of writing through
  paths that skip that schema's maintenance.
- **Negative — the new failure mode.** During any rollout where binaries of different versions
  touch the same bank, the older one now hard-fails on every write once the newer one has stamped
  the bank, until it is updated. For an MCP server whose bank is shared across every project on
  the machine, this can surface as every write from an un-updated client failing, not just one.
- **Recovery is a version update, not a repair.** The fix is `dotnet tool update -g ai-raccoon` (or
  the equivalent for however that binary was installed) so its `CurrentVersion` is at or above the
  bank's stamped version. There is no downgrade path and no override flag — this is deliberate,
  matching ADR-0011's "the ladder only ever moves forward" model.
- **Reads are unaffected.** The guard only gates the read-write open path; nothing in the
  codebase opens the bank read-only, so there is no degraded-but-working read mode during the
  version mismatch — the process fails to open the bank for any operation.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:220-240` (`CurrentVersion`,
the guard in `EnsureAsync`), `:700-720` (`MigrateToV3Async`); `src/AiRaccoon.Core/Memory/UnsupportedSchemaVersionException.cs`;
`src/AiRaccoon/Tools/ToolRefusals.cs:22-35` (`RefusalPrefixes`, confirming the exception is
unmapped); PR #204 ("fix(schema): forward-version write guard + v3 chunk recompute (closes
#200)"); [ADR 0011](0011-schema-versioning.md) (the ladder this guard closes a gap in).
