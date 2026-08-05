# Implementation Plan — Azure Blob Sync (second `ICloudStore` implementation)

> **Based on:** code reads 2026-08-05 (all files below re-read, not inferred); Azure SDK source
> verification against `azure-sdk-for-net` main (`sdk/core/Azure.Core/src/ETag.cs`,
> `sdk/core/Azure.Core/src/Pipeline/HttpClientTransport.cs`,
> `sdk/storage/Azure.Storage.Blobs/src/BlobBaseClient.cs`); NuGet flat container for the version.
> **Date:** 2026-08-05 · **Branch:** `task/sync-with-azure-blob` · **PR scope:** ONE PR for the whole task
> (repo invariant "One task maps to one PR"), opened as a draft from the first commit; packages land as a
> commit sequence P1 → P2 → P3 → P4 and are squashed on merge. Per-package gates stay (full `dotnet build`
> && `dotnet test` green per commit); per-package *merges* do not exist.
> **Workflow:** TDD mandatory — every production change below is preceded by a failing, behavior-focused
> xunit test (RED → GREEN). Gate: `dotnet build` + `dotnet test` from repo root; package-local fast loop
> with `dotnet test --filter "FullyQualifiedName~<class>"`.

---

## 0. Goal

Add Azure Blob Storage as a second cloud-sync backend behind the existing
`ICloudStore` interface, selected by a new `sync.provider` setting row (default `s3` —
zero migration for existing installs). No changes to `SyncService` merge logic, no new MCP
tools, no S3 behavior changes, no provider auto-detection.

## 1. Validated design (and corrections to the direction given)

Verified against the code; the direction is sound. Corrections are marked **CORRECTION**;
confirmations with new detail are marked **VERIFIED**.

1. **Settings keys** — **VERIFIED.** New `sync.provider` ("s3" when absent), `sync.connectionString`
   (secret, interactive prompt only), `sync.container`; reuse `sync.objectKey`. Precedent exists:
   `embedding.provider` + `ModelShowAsync`'s "provider: (none …)" pattern.
2. **`SyncOptions`** — **VERIFIED.** Add `Provider` (`SyncProvider { S3, Azure }`, default `S3`),
   `ConnectionString`, `Container`; `IsConfigured` becomes provider-aware (azure: `ConnectionString`
   && `Container` non-blank; s3: unchanged). **CORRECTION (small):** unknown `sync.provider` values
   parse to `S3` (lenient — a typo'd row must not break an existing S3 install). **CORRECTION (review
   round):** `SyncProvider.Parse` MUST be case-insensitive (`Enum.TryParse(value, ignoreCase: true)` or
   an explicit lowercase mapping) — the CLI writes `provider=azure` lowercase, and a case-sensitive
   parse would route it to S3 → the exact trap R1/R2 exist to prevent. Pin with
   `ReadOptionsAsync_MapsAzureRows` seeding the literal lowercase `"azure"` and
   `ReadOptionsAsync_UnknownProviderValue_DefaultsToS3`. Only construction site of `SyncOptions` is
   `SyncCloudStoreFactory.ReadOptionsAsync` (verified by search) — no ripple.
3. **`AzureBlobCloudStore` semantics** — **VERIFIED with a critical quoting detail.** Azure SDK source
   (checked today):
   - `new ETag(string)` stores the string **verbatim — no normalization**; `ToString()` ("G" format)
     returns it verbatim; `ToString("H")` would add quotes if missing.
   - The Blobs client writes the If-Match header as `conditions.IfMatch?.ToString()` — plain "G"
     (`BlobBaseClient.cs:6197`, verified). Therefore **`IfMatch` must be constructed from the QUOTED
     string: `new ETag($"\"{etag}\"")`** — constructing `new ETag(etag)` unquoted would send an
     invalid `If-Match: 0x8D…` header (Azure requires a quoted etag). This is the one place the
     naive direction would have produced a live bug.
   - Pull ETag (`BlobDownloadDetails.ETag`) comes back as the raw header value, i.e. **quoted**
     (`ETag: "0x8D…"`); store the unquoted form via `.Trim('"')` — idempotent, matches the S3
     storage format (S3CloudStore already strips quotes the same way).
   - `PullAsync` → `BlobClient.DownloadContentAsync`; 404 (`RequestFailedException.Status == 404`)
     → `null`; other `RequestFailedException` → `SyncNetworkException`. `PushAsync` →
     `UploadAsync(BinaryData, BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = … } })`;
     no conditions when `etag` is null = unconditional overwrite (Put Blob semantics — matches S3);
     412 → `SyncConflictException`; other → `SyncNetworkException`. Key on `Status`, not `ErrorCode`
     (mirrors S3CloudStore keying on `StatusCode`).
   - **CORRECTION (new risk found):** `BlobServiceClient` ctor throws `ArgumentException` /
     `FormatException` on a malformed connection string — inside `CreateAsync` that would surface as
     an untyped tool crash. Wrap the ctor and rethrow as `SyncNotConfiguredException` (typed; the MCP
     layer already maps it to `sync-not-configured`). The exception message becomes provider-neutral
     AND covers the malformed case (review round): "Memory sync is not configured or its connection
     string is invalid. Run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add
     azure <container>' and enter the credentials when prompted." Test-safe: `SyncServiceTests`
     asserts the exception type only, never the message (verified).
   - **Accepted edge (review round):** only the `BlobServiceClient` ctor is wrapped — container-client
     construction is lazy (verified), so the wrap scope is correct; but a pathological container name
     (space, `?`, `/`) can still throw `ArgumentException` from URI building at request time — S3
     surfaces request-time errors as typed `AmazonS3Exception`; azure cannot. Accepted and documented
     here; surfaces as an untyped tool error only for invalid container names, which Azure rejects at
     request time anyway (noted in risk table).
4. **Factory** — **VERIFIED.** `SyncCloudStoreFactory.CreateAsync` switches on
   `options.Provider`: S3 → `S3CloudStore` (existing path), Azure → `AzureBlobCloudStore`,
   `!IsConfigured` → `NullCloudStore` regardless of provider. `Dependencies.cs:43` needs no change
   (per-call resolution already wired).
5. **CLI** — **VERIFIED with two corrections.**
   - `sync add azure <container> [--object-key <key>]`; connection string prompted interactively on
     stderr, read from stdin, empty aborts with exit 1 persisting nothing (mirror `SyncAddS3Async`).
   - `sync add s3` must ALSO write `sync.provider = "s3"` (direction said this; it is not optional):
     after an azure install runs `sync add s3`, the azure rows are cleared but the `sync.provider`
     row would otherwise still say `azure` → factory sees `azure` + no azure credentials →
     `NullCloudStore` → sync silently dead. Writing `provider=s3` is what makes the switch real.
     Existing test `SyncAddS3_WritesAllRows_IncludingSecrets_PromptedInteractively` must be updated.
   - **CORRECTION:** `SyncShowAsync` currently decides "not configured" by the presence of the
     `sync.endpoint` row (`ConfigCommands.cs:332`) — an azure install has no endpoint row and would
     print "sync not configured" even when fully configured. Re-key `show` off the resolved
     provider: print `provider:` (row or default `s3`), then provider-specific fields
     (s3: endpoint/bucket/region/objectKey + redacted accessKey/secretKey states; azure:
     container/objectKey + redacted connectionString state). "Not configured" = no `sync.*` rows at all.
   - `sync remove`: replace the 6-key array with prefix-delete (`GetSettingsByPrefixAsync("sync.")`
     → `DeleteSettingAsync` each) so "remove deletes ALL sync.* keys" holds by construction and the
     key list can't drift when rows are added later. Simpler than maintaining the array.
   - Adding one provider clears the other provider's rows (direction) — required, with a stronger
     reason than hygiene: **the settings table is itself merged across installs by `SyncService`
     (`INSERT … ON CONFLICT DO UPDATE` on `settings`), so stale S3 secrets would propagate to every
     synced install.** Clearing on add is the only cheap total fix. **Explicit row inventories
     (review round):** s3-only rows = `endpoint`, `bucket`, `region`, `accessKey`, `secretKey` (5);
     azure-only rows = `connectionString`, `container` (2); `objectKey` is SHARED (upsert/delete,
     never blanket-deleted — blanket-deleting it would silently reset the key on every provider
     switch). `provider` is overwritten, not deleted.
   - **Write-order guarantee (review round):** `SyncAddAzureAsync` prompts for the connection string
     and validates it BEFORE any settings write (mirror `SyncAddS3Async`'s prompt-then-write order).
     Writing `provider=azure` before the user aborts would leave a working s3 install in the trap
     state `provider=azure` + no azure rows → `NullCloudStore` → silently dead sync; settings-merge
     has no tombstones, so that partial state would even propagate to other installs.
6. **Tests without a live Azure account** — **VERIFIED.** `HttpClientTransport(HttpClient)` and
   `HttpClientTransport(HttpMessageHandler)` are public (`HttpClientTransport.cs:112,133`), and
   `BlobClientOptions.Transport` is the standard public setter. `new BlobServiceClient(fakeConnStr,
   new BlobClientOptions { Transport = new HttpClientTransport(handler) })` never touches the
   network — **no Azurite needed.** Two details:
   - Set `BlobClientOptions.Retry.MaxRetries = 0` in tests so a canned 500 doesn't trigger the SDK's
     default 3-retry loop (deterministic, fast).
   - Fake connection string must parse (account key is base64-decoded eagerly):
     `DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=<Azurite's published dev key>;EndpointSuffix=core.windows.net`
     — obviously fake, public, valid base64.
   - Test seam: public ctor `(SyncOptions, ILogger<AzureBlobCloudStore>?)` for DI + internal ctor
     `(BlobServiceClient, ILogger<AzureBlobCloudStore>?)` for tests (`InternalsVisibleTo
     AiRaccoon.Tests` already exists in the Infrastructure csproj).
7. **Docs** — **VERIFIED.** All "S3-compatible" sync wording listed in §5 P4 with exact anchors.
   Plus two MCP-layer strings that mention `sync add s3` explicitly (`MemoryTools.cs:586,606` and the
   `memory_sync` tool `[Description]` at `MemoryTools.cs:567`); E2E only asserts `IsError` (verified
   `McpServerE2ETests.cs:158-169`) so rewording is test-safe.
8. **No new ADR.** This is a second implementation behind an existing interface with no architecture
   change. Two small rulings get recorded as inline comments (repo precedent: "single-channel ruling",
   F13): **R1** — exactly one active provider; `sync add <provider>` clears the other provider's rows.
   **R2** — unknown `sync.provider` value behaves as `s3`.

**Simpler-shape check (invariant):** no provider abstraction beyond the enum + factory switch (the
two stores already share `ICloudStore`); no container-name validation up front (Azure rejects at
request time; surfaces as `SyncNetworkException`); no etag watermark changes (`last_etag` is written
but never read by `SyncService` — verified — so switching providers can't contaminate etags);
`Azure.Core` is not pinned (flows transitively from `Azure.Storage.Blobs`; we only use `Azure.ETag`
and `Azure.RequestFailedException`, both public). Everything below is the minimum that works.

---

## 2. Work packages

### P1 — Provider model, settings keys, package ref, `AzureBlobCloudStore` (serial root)

**Objective:** the azure backend exists, is configurable, and is exercised end-to-end at the store level.

**Files**
- Modify: `src/AiRaccoon.Infrastructure/Options/SyncOptions.cs` — add `SyncProvider` enum
  (`S3`, `Azure`, `Parse(string?)` helper defaulting to `S3`), `Provider`, `ConnectionString`,
  `Container`; provider-aware `IsConfigured`.
- Modify: `src/AiRaccoon.Infrastructure/Sync/SyncSettingsKeys.cs` — add `Provider = "sync.provider"`,
  `ConnectionString = "sync.connectionString"`, `Container = "sync.container"` (secrets noted as
  single-channel, matching the existing access/secret key comments).
- Modify: `Directory.Packages.props` — add
  `<PackageVersion Include="Azure.Storage.Blobs" Version="12.29.1" />` (latest stable on
  nuget.org, checked 2026-08-05; **never** a `Version` on the `PackageReference`).
- Modify: `src/AiRaccoon.Infrastructure/AiRaccoon.Infrastructure.csproj` — add
  `<PackageReference Include="Azure.Storage.Blobs"/>`.
- Create: `src/AiRaccoon.Infrastructure/Sync/AzureBlobCloudStore.cs` — sealed partial class,
  nested static partial `Log` with `[LoggerMessage]` (EventIds 202/203), mirroring `S3CloudStore`
  shape. Ctor: guards on `ConnectionString`/`Container` (`ArgumentException.ThrowIfNullOrWhiteSpace`);
  `BlobServiceClient` construction wrapped → `SyncNotConfiguredException` (see §1.3).
  Pull/push as §1.3.
- Modify: `src/AiRaccoon.Infrastructure/Sync/ICloudStore.cs:3` and
  `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:6` — drop "S3-compatible" from the XML docs
  (they describe the interface/cycle, not S3).
- Modify: `src/AiRaccoon.Infrastructure/Sync/SyncNotConfiguredException.cs` — provider-neutral
  message covering the malformed-string case: "Memory sync is not configured or its connection
  string is invalid. Run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add
  azure <container>' and enter the credentials when prompted."
- Modify: `src/AiRaccoon.Infrastructure/Sync/SyncSettingsKeys.cs` — class-level doc comment updated
  (review round): no longer "written by `ai-raccoon sync add s3`" only — both providers.
- Modify: `tests/AiRaccoon.Tests/Unit/sync/SyncCloudStoreFactoryTests.cs` — extend `ReadOptionsAsync`
  mapping coverage (this file is shared with P2; P2 edits it after P1 lands — sequential, see §3).
- Create: `tests/AiRaccoon.Tests/Unit/sync/AzureBlobCloudStoreTests.cs` — `[Trait(Unit)]` +
  `[Trait(Fast)]`; local `CannedBlobHandler : HttpMessageHandler` returning configured
  (status, headers, body) responses and recording requests — method, path, AND `If-Match` header
  (review round: asserting the blob URL is hit catches wrong `GetBlobClient` wiring).

**TDD order (each: failing test → run to confirm RED → implement → GREEN → commit)**
1. `ReadOptionsAsync_MapsAzureRows` (seeds the literal lowercase `"azure"` the CLI writes) /
   `ReadOptionsAsync_ProviderDefaultsToS3_WhenRowAbsent` /
   `ReadOptionsAsync_UnknownProviderValue_DefaultsToS3` (pins R2) /
   `ReadOptionsAsync_AzureIsConfiguredOnlyWhenConnectionStringAndContainerPresent` (4 facts) →
   `SyncOptions` + keys.
2. `Ctor_InvalidConnectionString_ThrowsSyncNotConfigured`,
   `Ctor_MissingContainer_ThrowsArgumentException` → store ctor + exception mapping.
3. `Pull_ExistingBlob_ReturnsDataAndUnquotedETag` (canned 200, `ETag: "0x8Dabc"` → `0x8Dabc`),
   `Pull_MissingBlob_ReturnsNull` (404), `Pull_ServerError_ThrowsSyncNetworkException` (500,
   `MaxRetries = 0`) → `PullAsync`.
4. `Push_WithETag_SendsQuotedIfMatchHeader` (handler asserts request has `If-Match: "0x8Dabc"` —
   **this pins the quoting correction**), `Push_WithoutETag_SendsNoIfMatchHeader`,
   `Push_Conflict_ThrowsSyncConflictException` (412), `Push_ServerError_ThrowsSyncNetworkException`
   (500), `Push_ReturnsUnquotedETag` (canned `ETag: "0x8Dnew"` → `"0x8Dnew"` returned unquoted),
   `Push_WithETagOnMissingBlob_ThrowsSyncConflictException` (review round: documents the SDK
   behavior — If-Match on a nonexistent blob also 412s, matching S3; `SyncService`'s existing
   conflict→re-pull→unconditional-push retry handles it) → `PushAsync`.

**Acceptance criteria:** `dotnet test --filter "FullyQualifiedName~AzureBlobCloudStore|FullyQualifiedName~SyncCloudStoreFactory"` green; store returns unquoted etags in/out; If-Match header quoted when etag given and absent when null; 404→null, 412→`SyncConflictException`, 500→`SyncNetworkException`, malformed connection string→`SyncNotConfiguredException`; no `Version` on the PackageReference; `dotnet build` clean.

---

### P2 — Factory routing (parallel with P3, after P1)

**Objective:** `CreateAsync` resolves the store from the current rows, provider-aware.

**Files**
- Modify: `src/AiRaccoon.Infrastructure/Sync/SyncCloudStoreFactory.cs` — `ReadOptionsAsync` reads
  the 3 new rows; `CreateAsync` switches on `options.Provider` (S3 → `S3CloudStore`, Azure →
  `AzureBlobCloudStore` with `loggerFactory.CreateLogger<AzureBlobCloudStore>()`, else `NullCloudStore`).
- Modify: `tests/AiRaccoon.Tests/Unit/sync/SyncCloudStoreFactoryTests.cs` (sequential after P1).

**TDD order**
1. `Create_WithAzureSettings_ReturnsAzureBlobCloudStore`,
   `Create_WithAzureMissingConnectionString_ReturnsNullCloudStore`,
   `Create_WithProviderAzureAndS3RowsOnly_ReturnsNullCloudStore` (provider row says azure, s3 rows
   present, no azure creds → `NullCloudStore` — the "silently dead" trap from §1.5) → factory switch.
2. `Create_WithFullS3Settings_ReturnsS3CloudStore` stays green; add explicit
   `Create_WithProviderS3AndFullS3Settings_ReturnsS3CloudStore` and
   `Create_WithProviderRowOnly_ReturnsNullCloudStore` (review round: degenerate form of the trap —
   provider row present, no credential rows).

**Acceptance criteria:** the 3 routing facts above green; existing S3 routing tests unchanged-green;
no behavior change when no `sync.provider` row exists (default s3).

---

### P3 — CLI: `sync add azure`, provider-aware `show`, total `remove` (parallel with P2, after P1)

**Objective:** the azure backend is configurable through the single config channel, with the
single-active-provider ruling enforced.

**Files**
- Modify: `src/AiRaccoon/Setup/CliArgs.cs` — `SyncCommand()`: `add` description drops
  "S3-compatible"; new `azure` subcommand with `Argument<string>("container")` + optional
  `--object-key` (same `HelpName` as s3). Secrets stay undeclared (existing defense covers
  `--sync-connection-string` automatically).
- Modify: `src/AiRaccoon/Setup/ConfigCommands.cs` — new `SyncAddAzureAsync` (container arg,
  interactive connection-string prompt mirroring `SyncAddS3Async`; writes `provider=azure`,
  `connectionString`, `container`, upsert/delete `objectKey`; deletes the 5 s3-only rows);
  `SyncAddS3Async` additionally deletes the 2 azure-only rows and writes `provider=s3`;
  `SyncRemoveAsync` → prefix-delete over `sync.*`; `SyncShowAsync` → provider-resolved display
  with `connectionString` redacted ("set"/"unset").
- Modify: `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs` — add
  `Parse_SyncAddAzure_ParsesContainerAndOptions`, `Parse_SyncAddAzure_NoContainer_ReturnsError`;
  add `--sync-connection-string` to `Parse_SecretFlagsNeverDeclared_ReturnError`.
- Modify: `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsRetrievalSweepSyncTests.cs` — extend the
  existing sync section (same `Run` helper, `FakeConfigStore`).

**TDD order**
1. `SyncAddAzure_WritesRows_AndClearsS3Rows` (seed full s3 rows + stdin connection string →
   azure rows present, `provider=azure`, s3 rows gone), `SyncAddAzure_EmptyStdin_AbortsWithoutPersistingAnything`,
   `SyncAddAzure_WithoutObjectKey_ClearsStaleObjectKeyRow` → `SyncAddAzureAsync`.
2. `SyncAddS3_WritesProviderRow_AndClearsAzureRows` (seed azure rows → after add s3: `provider=s3`,
   azure rows gone) — update the existing `SyncAddS3_WritesAllRows_IncludingSecrets_PromptedInteractively`
   expectation to include the provider row → `SyncAddS3Async` changes.
3. `SyncRemove_DeletesAllSyncRows_IncludingAzure` (seed s3 + azure rows + a stray `sync.future` row
   → all gone) — replace/extend `SyncRemove_DeletesAllSyncRows` → prefix-delete.
4. `SyncShow_Azure_PrintsProviderContainerAndRedactedConnectionString`,
   `SyncShow_WithoutProviderRow_DefaultsToS3`, `SyncShow_AzureMissingSecret_ShowsUnset` → re-keyed
   `SyncShowAsync`.
5. CliArgs facts (parse + secret-flag rejection) → `CliArgs.SyncCommand`.

**Acceptance criteria:** the 8+ new/updated facts green; existing s3 CLI tests green after the
provider-row update; full `dotnet test` green.

---

### P4 — Docs + provider-neutral error copy (after P3)

**Objective:** no user-facing surface still says "S3-compatible" where azure is now possible.

**Files**
- Modify: `README.md` — lines ~6, 32-34 (features), 65-66, 91, 99-100, 156-167 (CLI table +
  secrets paragraph + cloud-sync section): add `sync add azure <container> [--object-key]`,
  `sync.provider` default note, connection-string prompt note.
- Modify: `docs/reference/agent-memory-server.md` — lines ~153-154, 181, 189-194, 238 (error table
  row for `sync-not-configured` gains the azure hint).
- Modify: `docs/explanation/architecture.md` — lines ~256-268 (sync paragraph + mermaid
  participant "R as S3 Cloud Store" → "Cloud Store").
- Modify: `docs/explanation/agent-memory-architecture.md` — lines ~39-40, 65-68 ("S3-compatible
  sync" / "Why sync goes through one S3 object" headings + prose → provider-neutral).
- Modify: `src/AiRaccoon/README.md` — lines ~8, 32-34, 66, 74.
- Modify: `src/AiRaccoon/Tools/MemoryTools.cs` — lines 567, 586, 606: provider-neutral copy
  ("Configure the endpoint/bucket/keys with `ai-raccoon sync add s3 …` or `ai-raccoon sync add azure
  <container> …`"). Test-safe (E2E asserts `IsError` only).
- Add inline ruling comments (R1 single active provider, R2 unknown provider → s3) on
  `SyncSettingsKeys.Provider` / `SyncProvider.Parse` — these land with P1/P3; P4 only the prose.
- **Intentional residues** (allowed by the acceptance grep): S3-specific statements inside
  `S3CloudStore` docs/code; the `s3` subcommand descriptions in `CliArgs.cs`; historical
  planning/finding docs under `docs/plans/` and `docs/work/` (e.g. `cli-args-parsing.md`).

**Acceptance criteria:** `grep -rn "S3-compatible" --include="*.md" --include="*.cs" .` returns only
the intentional residues above; `dotnet build` + full `dotnet test` green.

---

## 3. Ordering (serialized — review round)

P1 → P2 → P3 → P4, all strictly sequential. P2 and P3 do NOT run in parallel: they share the same
worktree's `obj/`/`bin/` for the whole solution, so concurrent `dotnet build`/`dotnet test` would
contend on `project.assets.json`, restore artifacts, and test output, and each agent's test run
could compile the other's half-written sources — a "green" run under those conditions is not
trustworthy evidence ("Done means proven"). P2 and P3 share no source files, so serializing costs
nothing; P2 and P3 are both pure consumers of P1.

## 4. Risks and mitigations

| # | Risk | Evidence / mitigation |
|---|---|---|
| 1 | **ETag quoting** — `new ETag(string)` does NOT normalize (stores verbatim); the Blobs client writes `IfMatch.ToString()` ("G", verbatim) straight into the header. Unquoted construction → invalid `If-Match: 0x8D…` → 400s. | Verified from SDK source today (`ETag.cs`, `BlobBaseClient.cs:6197`). Mitigation: construct `new ETag($"\"{etag}\"")`; pin with `Push_WithETag_SendsQuotedIfMatchHeader`; strip pull etags with idempotent `.Trim('"')`; pin with `Pull_ExistingBlob_ReturnsDataAndUnquotedETag`. |
| 2 | **Conditional upload on existing blobs** — If-Match Put Blob returns 412 `ConditionNotMet` on mismatch; upload with **no** conditions unconditionally overwrites (matches S3). | `BlobUploadOptions.Conditions.IfMatch` is the documented conditional path; canned 412 test pins the conflict mapping; no-conditions test pins overwrite semantics. Note: If-Match against a *missing* blob also 412s — identical to S3; `SyncService`'s conflict→re-pull→unconditional-push retry already handles it (untouched). |
| 3 | **Azure default retry policy** (3 retries on 5xx/408/connection errors) could make 500-tests slow/flaky and double-retry on network errors. | Tests set `BlobClientOptions.Retry.MaxRetries = 0`; 412 is not retried by the SDK, so `SyncService`'s own 3-retry loop is the only conflict retry — no interaction. |
| 4 | **Back-compat** — existing installs have no `sync.provider` row; `sync add s3` now writes one. | Default `S3` on absent/unknown row (R2); existing s3 tests updated in the same PR as the CLI change (P3 step 2); `sync show`/factory treat absent row as s3. |
| 5 | **Stale-provider trap** — `sync add s3` after azure without writing `provider=s3` leaves routing dead (provider=azure + no azure rows → Null). | P3 step 2 writes `provider=s3`; pinned by `SyncAddS3_WritesProviderRow_AndClearsAzureRows` and `Create_WithProviderAzureAndS3RowsOnly_ReturnsNullCloudStore` (documents the failure mode). |
| 6 | **Credential sprawl / settings merge** — stale secrets in the settings table propagate to every synced install (settings merge is LWW upsert). | Adding a provider deletes the other provider's rows (R1); `sync remove` prefix-deletes everything. |
| 7 | **Malformed connection string** throws `ArgumentException`/`FormatException` from the SDK ctor inside per-call `CreateAsync` → untyped tool error. | Store ctor wraps and rethrows `SyncNotConfiguredException` (typed, mapped to `sync-not-configured` by the MCP layer); pinned by `Ctor_InvalidConnectionString_ThrowsSyncNotConfigured`. |
| 8 | **Version drift** — `Azure.Storage.Blobs` moves fast; `Azure.Core` flows transitively. | Pin **12.29.1** (latest stable, checked 2026-08-05) centrally in `Directory.Packages.props` only; no direct `Azure.Core` reference unless a build analyzer demands one. |
| 9 | **`sync show` misreports azure configs** — current gate keys off the `sync.endpoint` row. | Re-key on resolved provider (P3 step 4), pinned by `SyncShow_Azure_PrintsProviderContainerAndRedactedConnectionString`. |
| 10 | **Canned-HTTP fidelity** — the SDK parses response headers (ETag, Last-Modified, x-ms-*); a minimal canned response must carry `ETag` at least. | Handler supplies `ETag` + status per scenario; `MaxRetries=0`; no Azurite, no network (transport replaced at the pipeline level — public API verified). |
| 11 | **Partial-write hazard (review round)** — an aborted `sync add azure` (or any write ordering that persists `provider` before the secrets prompt completes) leaves `provider=azure` + no azure rows → `NullCloudStore` → silently dead sync; settings-merge has no tombstones (verified `SyncService.cs:253-262`), so the partial state can spread to other installs. | Prompt + validate the connection string BEFORE any settings write (required change 7); pinned by `SyncAddAzure_EmptyStdin_AbortsWithoutPersistingAnything`. |
| 12 | **Invalid container name (review round)** — pathological names (space, `?`, `/`) throw `ArgumentException` from URI building at request time, which azure cannot map to a typed sync exception (S3 can). | Accepted: only `BlobServiceClient` ctor is wrapped (container construction is lazy — verified); surfaces as an untyped tool error only for container names Azure would reject anyway. Documented in §1.3. |

## 5. Out of scope (unchanged)

`SyncService` merge logic and retry loop; the `memory_sync` MCP tool surface; the S3
implementation; provider auto-detection; migration of existing installs; container-name
validation beyond Azure's own 400s; `last_etag` watermark handling (written, never read —
no cross-provider contamination possible).

## 6. Commit order and gates (one PR, squashed on merge)

1. P1 → P2 → P3 → P4, sequential commits on `task/sync-with-azure-blob`, pushed as they land; the
   draft PR is opened from the first commit (P1) per the GitHub extension.
2. Full gate per commit: `dotnet build` && `dotnet test` (repo root) — Unit/Fast tests cover all new
   behavior; no Integration/E2E additions (canned transport is fully hermetic).
3. Final doc sweep: no "S3-compatible" phrasing on provider-agnostic surfaces (see P4's intentional-residue list).
