# 0132 — The 1Password CLI is a second bank-key source beside Bitwarden

Date: 2026-10-16

Status: Accepted

## Context

The bank key can come from the environment (`AIRACCOON_DB_PASSPHRASE`) or from Bitwarden
Secrets Manager (`ai-raccoon encryption bitwarden`, ADR 0012, ADR 0038). Teams that keep their
SSH keys in 1Password have no equivalent. The 1Password CLI, `op`, reads a secret by reference
(`op read op://vault/item/field`), and an SSH Key item can render its private key in OpenSSH
format (`?ssh-format=openssh`), the same ed25519 format the Bitwarden path already parses and
derives from.

## Decision

**`1password` is a second source name, next to `bitwarden`.** It reuses the whole pipeline:
the same OpenSSH ed25519 parser, the same HKDF derivation, the same sidecar, the same
rekey-then-sidecar-then-settings persist order, the same cached resolution keyed by config
fingerprint.

**The 1Password item is an SSH Key item** holding an ed25519 key. The fetch is
`op read <reference>` with the reference stored verbatim, for example
`op://vault/item/private key?ssh-format=openssh`. Anything not starting with `op://` is refused
before a fetch.

**The reference lives in the existing `SecretId` slot**, in the sidecar and in the settings
table (`EncryptionSettingsKeys.SecretId`). `ProjectId` stays null and no settings key is added.
The resolver fingerprint already includes `SecretId`, so changing the reference refetches.

**Authentication is inherited, never passed.** `op` authenticates through
`OP_SERVICE_ACCOUNT_TOKEN` in the environment or the 1Password desktop-app integration.
`ai-raccoon encryption 1password` has **no `-t` option**: a token on ai-raccoon's argv is readable
by any same-user process through `ps` for the life of the command. `bws` keeps its `-t` for
compatibility; the new source does not inherit that mistake.

**No legacy key.** The pre-ADR-0012 derivation never existed for 1Password banks, so the provider
returns no `LegacyValue` and a 1Password bank is never eligible for the rekey migration.

**One runner for both CLIs.** `CliSecretManager(CliSecretTool tool)` replaces
`BitwardenCliSecretManager`. A tool names its executable, its token environment variable
(`BWS_ACCESS_TOKEN`, `OP_SERVICE_ACCOUNT_TOKEN`) and its install hint. The bws-named types and
error codes become tool-neutral (`CliSecretResult`, `CliSecretInvocationException`,
`CliSecretFailure`; `Key.SecretCliNotInstalled` 23, `Key.SecretCliTimedOut` 24,
`Key.SecretCliFailed` 25). The numbers do not change; they now cover either CLI. The runner also
redirects the child's stdin so it never reads the terminal.

**Timeouts:** presence check 5 s, `op read` 30 s. `op` can wait on a desktop-app approval prompt,
so it gets twice the bws bound.

**No new log events.** Events 802 and 804 carry the tool name as a parameter.

## Manual check required

Desktop-app integration needs the user's GUI session and an unlocked 1Password app. The
`ai-raccoon serve` process opens the bank at start-up through the same `op read`. Whether a
server started from a service manager or a detached shell can reach the desktop app is not
provable in tests, which use a fake `op`. A human verifies it once on a real install, and the
how-to says service-account tokens are the supported route for unattended servers.

## Consequences

- **Positive**: 1Password users get the bank-key flow Bitwarden users already have, with one
  code path to maintain.
- **Positive**: the token never appears on a command line.
- **Negative**: exit codes 23 to 25 are no longer Bitwarden-specific; scripts that matched on the
  names must follow the rename (the numbers are unchanged).
- **Negative**: a `1password` bank cannot be unlocked without `op` reachable at open time, the same
  trade-off Bitwarden banks make with `bws` (ADR 0038 caches the result per process).
