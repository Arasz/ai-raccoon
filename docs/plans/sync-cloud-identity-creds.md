# Implementation Plan — Sync via cloud-identity credentials (az CLI / AWS chain), bws-style

> **Based on:** merged `sync-with-azure-blob` (PR #7) — ICloudStore, provider row, SyncOptions/IsConfigured,
> factory routing, CLI verbs, row-clearing R1/R2 rulings. Follow-up requested by owner (f: 2026-08-05):
> add `--cli` credential modes using the azure CLI (az) and the AWS CLI credential chain — NO token options,
> NO secret prompts for these modes; the user is expected to be logged in already. Same approach as the
> bws key-source work: config rows hold non-secret identifiers only, secrets come from the machine's
> CLI/identity state, failure is loud and fixable. Back-compat: existing connection-string / access-key
> modes remain unchanged and remain the fallback (headless/CI).
> **Date:** 2026-08-05 · **Branch:** `task/sync-cloud-identity-creds` · **PR:** one PR, draft early, squash-merge.

## 0. Goal

`sync add azure <container> --cli --account <name>` and `sync add s3 <url> --bucket <name> --cli` configure
sync WITHOUT persisting any secret: azure uses `DefaultAzureCredential` (az CLI login / env / managed
identity), s3 uses the AWS default credential chain (env / ~/.aws / SSO / IMDS). At sync time an
auth failure maps to `SyncAuthFailedException` with a "run `az login`" / "run `aws configure` | `aws sso
login`" hint. Docs gain the least-privilege setup instructions (az role assignment / IAM policy) and the
container-must-exist note.

## 1. Design

### P1 — Options + stores (files: SyncOptions.cs, SyncSettingsKeys.cs, SyncCloudStoreFactory.cs, AzureBlobCloudStore.cs, S3CloudStore.cs, SyncCloudStoreFactoryTests.cs, AzureBlobCloudStoreTests.cs, MemoryTools.cs copy)

0. **REVIEW ROUND (silent-dead bug): `SyncCloudStoreFactory.ReadOptionsAsync` MUST read the two new rows
   (`sync.azureAccount`, `sync.s3Chain`) into `SyncOptions` — this file was missing from the original P1
   list, and without the mapping every account/chain config would resolve `IsConfigured=false` →
   `NullCloudStore` → silently dead sync.** Pin with the `ReadOptionsAsync_MapsAzureAccountRow` /
   `ReadOptionsAsync_MapsS3ChainRow` facts.
1. **Settings keys:** `sync.azureAccount` (azure-only, non-secret), `sync.s3Chain = "true"` (s3-only marker).
   Row inventories updated: azure-only rows = connectionString, container, azureAccount; s3-only rows =
   endpoint, bucket, region, accessKey, secretKey, s3Chain. `objectKey` stays shared; `provider` overwritten.
2. **SyncOptions:** add `Account` (azure), `S3Chain` (bool — parse with `bool.TryParse`, case-insensitive,
   review round). `IsConfigured`:
   - azure: `(ConnectionString || Account) && Container`
   - s3: `Endpoint && Bucket && ((AccessKey && SecretKey) || S3Chain)`
   Deterministic tie-break when both modes present (manual settings edits): connection string wins for
   azure, keys win for s3 — documented, not an error.
3. **AzureBlobCloudStore:** public ctor guard: `Container` required, and at least one of
   `ConnectionString`/`Account` (`ArgumentException` otherwise — the guard replaces the current
   `ThrowIfNullOrWhiteSpace(ConnectionString)`). Extract `internal static BlobServiceClient CreateClient(SyncOptions)`
   (public ctor calls it; tests assert `client.Uri` == `https://<account>.blob.core.windows.net/` for
   account mode — `BlobServiceClient.Uri` is public, probe-verified): ConnectionString present → `new BlobServiceClient(connStr)`;
   else → `new BlobServiceClient(new Uri($"https://{Account}.blob.core.windows.net"), new DefaultAzureCredential())`.
   Malformed connection string → `SyncNotConfiguredException` (existing wrap, unchanged).
   **Auth error mapping (new, probe-verified):** with no az login state, the first call throws
   `Azure.Identity.CredentialUnavailableException` (NOT AuthenticationFailedException — probe on a
   credential-less machine, 2026-08-05); bad credentials later surface as `AuthenticationFailedException`.
   Map BOTH → `SyncAuthFailedException` ("Azure auth failed — run 'az login' …"). `RequestFailedException`
   with Status 401/403 → `SyncAuthFailedException` (applies to ALL azure modes, incl. connection-string —
   acknowledged behavior change); other `RequestFailedException` → `SyncNetworkException` (unchanged).
   404 → null (unchanged).
4. **S3CloudStore:** public ctor guard: `Bucket` required; either `(AccessKey && SecretKey)` or `S3Chain`
   (`ArgumentException` otherwise). Extract `internal static IAmazonS3 CreateClient(SyncOptions)`:
   keys → `BasicAWSCredentials` (unchanged); chain → plain `new AmazonS3Client(config)` — **v4 resolves the
   credential chain LAZILY (probe-verified: ctor succeeds on a credential-less machine; the first call
   throws `AmazonClientException` "Failed to resolve AWS credentials")**, so NO ctor seam is needed.
   **Auth error mapping (new):** `AmazonClientException` → `SyncAuthFailedException` ("AWS auth failed —
   run 'aws configure' or 'aws sso login' …") caught in Pull/Push; `AmazonServiceException` Status 403 →
   `SyncAuthFailedException` (probe-verified: `AmazonServiceException` and `AmazonClientException` are
   SIBLINGS — separate catches, no shadowing). **Network gap (pre-existing, fix in this round):** raw
   `System.Net.Http.HttpRequestException` escapes both SDKs' typed exceptions (probe-verified) and surfaces
   as an untyped MCP 500 — map to `SyncNetworkException` in both stores. Other `AmazonS3Exception` →
   `SyncNetworkException` (unchanged); 412 → `SyncConflictException` (unchanged).
5. **MemoryTools.cs copy:** the `sync-auth-failed` McpException hint (~line 614, currently "verify the keys
   with 'ai-raccoon sync show'") becomes mode-aware: "…run 'az login' (azure --cli) or 'aws configure' /
   'aws sso login' (s3 --cli), or verify the keys with 'ai-raccoon sync show'". The "verify the keys"
   clause covers the 403-in-key-mode behavior change (review round). Test-safe (E2E asserts IsError only).
6. **SyncNotConfiguredException message:** unchanged (already covers both providers).

### P2 — CLI (files: CliArgs.cs, ConfigCommands.cs, CliArgsTests.cs, ConfigCommandsRetrievalSweepSyncTests.cs)

1. `sync add azure <container> --cli --account <name> [--object-key <key>]`:
   - `--cli` + `--account` given → NO connection-string prompt. Writes provider=azure, azureAccount,
     container; upsert/delete objectKey; deletes the 5 s3-only rows (endpoint, bucket, region, accessKey,
     secretKey, s3Chain — six now). Stdout: "sync configured: azure container {container} (az CLI)".
   - `--cli` without `--account` → parse error ("--account is required with --cli" via Required=true on
     --account when... System.CommandLine can't do conditional required — validate in the handler: stderr
     + exit 1). Without `--cli` → existing connection-string prompt path, unchanged; additionally deletes
     `sync.azureAccount` (stale mode row) and the s3 rows.
2. `sync add s3 <url> --bucket <name> --cli [--region] [--object-key]`:
   - `--cli` → NO access/secret key prompts. Writes provider=s3, s3Chain="true", endpoint, bucket, region,
     objectKey; deletes the 3 azure-only rows (connectionString, container, azureAccount). Stdout:
     "sync configured: {url} bucket {bucket} (AWS credential chain)".
   - Without `--cli` → existing key prompts, unchanged; additionally deletes `sync.s3Chain` + azure rows.
3. Delete-before-write ordering (R1 crash-safety, from the review round of the previous task) applies to
   both new modes: other-provider rows + stale mode rows deleted BEFORE the writes.
4. `sync show`: azure mode prints `account:` (set/unset) alongside connectionString/container/objectKey;
   s3 mode prints `chain: true/false` alongside keys states.
5. CliArgs: `--cli` and `--account` declared as options on the verbs (non-secret — fine); secrets still
   never declared (connection-string/keys remain prompt-only). Add `--sync-account`/`--sync-chain` to the
   secret-flags-never-declared test? No — they ARE declared now (`--account`, `--cli`); the never-declared
   list keeps `--sync-connection-string` etc. (not added). Parse tests: azure `--cli` without `--account`
   → handler error.

### P3 — Docs (files: README.md, src/AiRaccoon/README.md, docs/reference/agent-memory-server.md, docs/explanation/architecture.md + agent-memory-architecture.md if the sync section lists modes)

1. **Container must exist first** (owner item 1): `sync add azure` does not create the container — create
   it before configuring (az storage container create …), or the first sync fails with `sync-network:`.
2. **Azure CLI instructions** (owner item 2, bws-style — exact commands, least privilege):
   ```
   az login                                        # sign in once (Azure CLI)
   az storage account show -g <rg> -n <account> --query id   # find the storage account resource id
   az role assignment create --assignee "you@domain.com" --role "Storage Blob Data Contributor" \
     --scope "<storage-account-resource-id>"       # least privilege: scope to account or container
   ```
   Note: `--cli` mode uses DefaultAzureCredential — az CLI login state, or env vars
   (AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET) for headless. Nothing long-lived is stored in
   the settings table; the token is short-lived and revocable. Residual risks (bws-style, brief):
   network dependency at sync time, cloud-account compromise = memory compromise.
3. **AWS instructions** (owner item 2):
   ```
   aws configure   # or: aws sso login (short-lived SSO tokens)
   ```
   IAM policy (least privilege — the sync only GETs and PUTs one object):
   ```json
   { "Version": "2012-10-17", "Statement": [ { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"],
     "Resource": "arn:aws:s3:::<bucket>/<object-key-prefix>*" } ] }
   ```
   Note: `--cli` mode uses the default credential chain (env, ~/.aws/credentials, SSO, IMDS); prefer
   SSO/short-lived over static keys in ~/.aws/credentials.
4. CLI table rows gain the `--cli` variants; sync section gains a small mode table (connection string /
   az CLI / keys / AWS chain — what is stored where).

## 2. TDD order (each cluster: failing test → RED → implement → GREEN → commit)

1. Options/factory: `ReadOptionsAsync_MapsAzureAccountRow` / `ReadOptionsAsync_MapsS3ChainRow` /
   `IsConfigured_AzureAccountMode` / `IsConfigured_S3ChainMode` / `IsConfigured_MissingBothAzureModes_False` /
   `IsConfigured_MissingBothS3Modes_False`.
2. Azure store: `Ctor_AccountMode_BuildsClientWithAccountUri` (internal CreateClient seam — assert
   `client.Uri`), `Ctor_NeitherMode_Throws`, `Pull_NoAzureLogin_ThrowsSyncAuthFailed`
   (CredentialUnavailableException seam via a fake — see below), `Pull_Unauthorized_ThrowsSyncAuthFailed`
   (canned 401), `Pull_Forbidden_ThrowsSyncAuthFailed` (canned 403), `Pull_ServerError_StillSyncNetwork`
   (500), `Pull_HttpRequestException_ThrowsSyncNetwork` (canned transport throwing HttpRequestException),
   existing canned tests unchanged.
   Note: CredentialUnavailableException is thrown by DefaultAzureCredential's first token acquisition — it
   is NOT reachable through the canned-transport internal ctor. Test seam: the public ctor's
   `CreateClient` returns a BlobServiceClient; the auth-mapping tests use the internal ctor with a client
   whose transport throws `CredentialUnavailableException` (construct `DefaultAzureCredential`-backed
   behavior by wrapping: an internal `Action`/delegate seam on the store is NOT wanted — instead throw
   from the canned handler's `SendAsync` by wrapping the exception in the response path the SDK surfaces
   it through; if the SDK wraps it as RequestFailedException, assert that path and add the
   CredentialUnavailableException catch defensively (probe showed it IS the thrown type on first call).
   Implementer: verify at RED time what the SDK surfaces through HttpClientTransport and pin THAT.
3. S3 store: `Ctor_ChainMode_BuildsClientWithServiceUrl` (CreateClient seam — `client.Config.ServiceURL`),
   `Ctor_NeitherMode_Throws`, `Pull_NoCredentials_ThrowsSyncAuthFailed` (AmazonClientException — throw it
   from the canned handler? NO: the chain resolves OUTSIDE the HTTP path. Seam: chain-mode store + no
   env/creds → the first real call throws AmazonClientException. For hermetic tests, an internal seam is
   needed: make `CreateClient` return `IAmazonS3` and allow tests to substitute a stub `IAmazonS3` that
   throws AmazonClientException — the internal ctor seam. Implementer: add `internal S3CloudStore(IAmazonS3 s3, string bucket, ILogger?)` test ctor mirroring AzureBlobCloudStore's), `Pull_Forbidden_ThrowsSyncAuthFailed`
   (canned 403 AmazonS3Exception), `Push_HttpRequestException_ThrowsSyncNetwork`, existing tests unchanged
   (none exist today — factory tests cover construction).
4. CLI: `SyncAddAzure_CliMode_WritesAccountRow_NoPrompt` (stdin TextReader.Null — must NOT prompt),
   `SyncAddAzure_CliMode_MissingAccount_ReturnsError`, `SyncAddS3_CliMode_WritesChainRow_NoPrompt`,
   `SyncAddS3_CliMode_ClearsAzureRows`, `SyncAddAzure_ConnStringMode_ClearsStaleAccountRow`,
   `SyncAddS3_KeyMode_ClearsStaleChainRow`, show facts for account/chain fields, CliArgs parse facts
   (--cli/--account accepted; handler rejects --cli without --account).
5. Copy: MemoryTools sync-auth-failed hint — no behavior test needed (string), E2E IsError-only (verified).
6. Docs per P3 (no tests).

## 3. Risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | `DefaultAzureCredential` no-login failure throws `CredentialUnavailableException` (probe-verified), not the type the plan originally named | Both types mapped → SyncAuthFailedException; pinned by the RED-phase discovery test |
| 2 | AWS v4 resolves the chain lazily — first call throws `AmazonClientException` | Mapped in Pull/Push → SyncAuthFailedException; pinned via the internal IAmazonS3 seam (stub throwing AmazonClientException) |
| 3 | Both-mode rows present (manual edits) — deterministic tie-break | Connection string wins (azure), keys win (s3); documented in SyncOptions XML |
| 4 | Row-clearing inventory drift (new azureAccount/s3Chain rows must be cleared when switching providers/modes) | Updated inventories in all four add paths + delete-before-write; pinned by CLI tests |
| 5 | `--cli` without `--account` (azure) | Handler validation → stderr + exit 1, pinned by test |
| 6 | Back-compat: existing installs (no --cli) unchanged; existing tests must stay green | No changes to non-CLI paths beyond stale-mode-row deletion + 403/401-now-auth-failed mapping (acknowledged); full suite at join |
| 7 | 403/401 now map to SyncAuthFailedException in key/connection-string modes too (was SyncNetwork) | Acknowledged behavior improvement; MemoryTools hint keeps the "verify the keys" clause so the error stays actionable |

## 4. Out of scope

Token options (explicitly excluded by owner — "no token for them"); setup-time `az account show` /
`aws sts get-caller-identity` presence checks (runtime auth errors are the loud failure); managed-identity
config beyond what DefaultAzureCredential resolves automatically; changing the connection-string / key modes.

## 5. Gates

Per-commit: targeted `dotnet test --filter` (sync + setup classes) + `dotnet build` 0 warnings. At join:
full `dotnet test` (expected 892+ passed; known pre-existing embedding/E2E flakes on this machine,
proven on origin/main baseline). Final: code-reviewer gate on the diff, doc sweep for the new options.
