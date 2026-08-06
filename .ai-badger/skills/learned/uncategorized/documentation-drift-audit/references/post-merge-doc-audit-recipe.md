# Post-merge doc audit — worked example (Azure Blob sync, 2026-08-05)

Concrete Phase-5 audit after squash-merge `feat(sync): Azure Blob sync backend
behind ICloudStore` (PR #7). Use as a pattern template, not as facts about any
other repo — re-verify everything against the code you're auditing.

## Setup

- Worktree created: `git worktree add /path/to/<repo>-doc-audit origin/main`
  (detached HEAD), then `git merge-base --is-ancestor <merge-sha> HEAD` to prove
  the feature is present before auditing.
- Audited only: `.md` files + instruction files. No code, no tests, no push.

## Ground truth extracted from merged code (before judging docs)

- `SyncSettingsKeys.cs`: keys `sync.provider` (absent/unknown → s3, ruling R2),
  `sync.endpoint/bucket/region/objectKey/accessKey/secretKey/connectionString/container`.
- `CliArgs.cs` sync subtree: `sync add s3 {url} --bucket {name} [--region {name}]
  [--object-key {key}]`; `sync add azure {container} [--object-key {key}]`;
  `sync remove` ("Back to default: sync off"); `sync show` ("keys redacted").
- `ConfigCommands.cs`: prompts on **stderr**, reads **stdin**, empty answer →
  exit 1 with no settings writes; `sync add <provider>` deletes the other
  provider's rows then writes `provider=<name>`; `sync remove` prefix-deletes
  ALL `sync.*` rows; `sync show` prints `provider: <raw row>` first, then
  provider-specific fields, secrets as `set`/`unset`.
- `MemoryTools.cs`: `memory_sync` description is provider-neutral ("sync add s3 …
  or `sync add azure …`"); error prefixes `sync-not-configured`, `sync-auth-failed`,
  `sync-conflict`, `sync-network:`, `sync-corrupt-file:`; object-key default
  `memory-<projectId>.db` applied per call.

## Grep gates

- `grep -rn "S3-compatible" --include="*.md" --include="*.cs" .` → 24 hits, all
  intentional: S3CloudStore.cs XML doc, CliArgs.cs s3 subcommand description,
  docs/plans + docs/work (historical), and current-surface hits already paired
  ("S3-compatible or Azure Blob"). Gate passed with zero edits.
- `grep -rn "sync add s3" --include="*.md" .` → 28 hits; every current-surface
  hit (README, docs/reference) sat on a line paired with `sync add azure`;
  remainder in dated plans/work docs. Gate passed.
- Key-name sweep: `grep -rn "sync\." --include="*.md"` — current-surface docs
  only use the real key names; historical docs keep old `--sync-*` flag names
  (correct, they record the pre-refactor surface).

## Generated instruction copies

- `diff CLAUDE.md .ai-badger/CLAUDE.md` → only the "Managed by ai-badger… do not
  edit this copy" header differs; same for `.hermes.md` vs `.ai-badger/HERMES.md`.
  So the sources are current — no edit needed, and the compaction policy means
  the provider-neutral "optional cloud sync" wording is correct as-is.

## The one fix

- `docs/explanation/README.md:13` — "why sync goes through one cloud **database**"
  → "one cloud **object**" (section title in agent-memory-architecture.md says
  "one cloud object"; sync target is object storage, not a database).
- Commit: `docs(sync): fix stale 'cloud database' wording after azure backend merge`.
- Lesson: directory index/README files often carry the OLD name of a section —
  diff the index's description against the actual section heading, not just
  against the code.

## Decision gaps reported (not fixed)

1. Azure auth is connection-string only (no managed identity / Entra ID /
   DefaultAzureCredential) — undocumented limitation.
2. `sync add azure` does not create the container; missing container surfaces as
   `sync-network: Azure push/pull failed: …` — decide auto-create vs document.
3. No user-facing Azurite/dev-testing note (plan doc only).
4. Unknown `sync.provider` values fall back to s3 (R2) — raw row printed by
   `sync show`, but the fallback itself is undocumented.
5. Error-shapes table lists only `sync-not-configured`; code emits four more
   sync prefixes (pre-existing subset, not merge drift).
6. Evidence block for the sync cycle cites only `S3CloudStore.cs`; could add
   `AzureBlobCloudStore.cs` (minor).
