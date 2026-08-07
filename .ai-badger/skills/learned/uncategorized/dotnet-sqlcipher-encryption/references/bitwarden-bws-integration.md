# Bitwarden Secrets Manager + bws — AiRaccoon integration record (2026-08-05)

Owner-decision facts and measured findings from the AiRaccoon encryption-key-source work. Source of truth documents (repo): docs/work/2026-08-05-db-passphrase-options.md (owner decisions section supersedes report F28 where they differ) and
docs/work/2026-08-05-db-passphrase-ssh-and-cloud-vaults.md.

## Provisioned environment (owner-managed store)

- bws CLI installed, access token configured by the user (BWS_ACCESS_TOKEN in their env).
- Project: id configured via `AIRACCOON_BITWARDEN_PROJECT_ID` / `AIRACCOON_BITWARDEN_SECRET_ID`.
- Secret: referenced by id, not name (names change).
- ALWAYS use the secret ID, not the name (IDs are stable; names change).
- The secret's VALUE is an unencrypted ed25519 SSH private key (per owner f:); the provider derives the SQLCipher raw key with the measured scheme.

## SDK vs CLI (decided: CLI)

- `Bitwarden.Secrets.Sdk` 1.0.0 (2025-01-24, the only release): net6.0, bundles the Rust native core (osx-arm64 etc., ~7 MB), SYNCHRONOUS published API (async only on unreleased main), explicit beta, custom non-OSI license. Client ctor
  measured 16–306 ms on macOS net10.
- Measured SDK behavior: token format validated client-side pre-network; format-valid fake token → BitwardenAuthException with server body (400 invalid_client); connection refused → instant throw, no timeout/retry config. State file written
  only after successful login; `stateFile=""` default is a footgun.
- Verdict: prefer the bws CLI when installed; the SDK is only for embedded/no-CLI setups.

## Bootstrap ranking for the access token (can't be in the encrypted DB)

1. macOS Keychain (pre-open readable, OS-encrypted at rest, token revocable/scoped).
2. 0600 token file (portable fallback).
3. SDK state file — REJECTED (auth output, disk secret).
4. Interactive at start — REJECTED (MCP-spawned servers have no terminal).
5. bws CLI's own token config — viable when the user manages it (the chosen path here).

## Security deltas + traps

- Remote key custody (key never rests on disk), revocation without local rekey, machine- account scoping, audit (Teams+ plans), cross-machine consistency.
- Offline: refuse to start loudly; no default cache (opt-in 0600 cache only if asked).
- ROTATION TRAP: rotating the secret in the Bitwarden web UI without `PRAGMA rekey`
  bricks the bank — config command must warn.
- Config flow pinned by owner: bws presence check first (install-guidance error), collect project id + secret id, optional `-t <token>` per-run only (never persisted), reachable- secret validation, rekey → sidecar → settings persist order,
  env-key retry leg for the unset crash window.

## Other key-source options (documented, not implemented)

- Azure Key Vault: Azure.Security.KeyVault.Secrets 4.11.0 + Azure.Identity 1.21.0 (MIT, net10); bootstrap = DefaultAzureCredential → az login state under ~/.azure.
- AWS Secrets Manager: AWSSDK.SecretsManager 4.0.100.7 (Apache-2.0, net10, same v4 SDK generation as AWSSDK.S3); bootstrap = standard AWS chain (~/.aws/credentials, SSO cache). Don't copy the static BasicAWSCredentials pattern used by the
  repo's S3 sync.
- Keychain-direct stays the general recommendation; Bitwarden is the opt-in tier the owner chose for this project.
