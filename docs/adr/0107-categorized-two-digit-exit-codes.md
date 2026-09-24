# 0107 — Categorized two-digit exit codes

Date: 2026-09-23

Status: Accepted

## Context

`ExitCode` had grown to roughly thirty single-purpose constants (1 through 27, plus 0
and 130) assigned in the order each case was discovered. Nothing about a bare number
told a caller anything: `2` could mean a locked bank, a wrong key, or a read-only
filesystem depending on which command hit it, and a script that wanted to retry a
transient failure had no way to ask "is this one of those" without a hand-maintained
table of its own. An audit of the CLI's failure surface (the catalog behind this ADR)
counted 75 distinct failure shapes the code already reaches, collapsed onto 27 numbers,
plus four cases that exit `0` today despite being failures — `encryption bitwarden` that
silently keeps the env-keyed source, and all three end states of `repair project-ids
--apply` (stuck, writers-active, and the new attention-needed case below).

**The constraint the new scheme has to fit inside.** A process exit status is one byte.
Measured on this machine, `exit(1003)` reports `$?` = `235` — `1003 mod 256` — so any
code past 255 is not a code at all, it is whatever the low 8 bits happen to be. Shells
also reserve the top of that byte: 126 means "found but not executable," 127 means "not
found," 128+*n* means "killed by signal *n*" (130 = Ctrl-C, SIGINT), so 126-255 are not
this program's numbers to hand out, `0` and `130` (`Ok.SIGC`) aside. That leaves 1-125,
and even fewer once single digits are reserved (see "Alternatives rejected"). Three
digits were never on the table.

## Decision

**Two digits, `{category}{case}`, 10-99.** The tens digit names one of nine categories;
the ones digit names a case within it, and `x0` is always that category's most general
member — the answer when nothing narrower applies. At most ten cases fit a category by
construction (`0`-`9`), which is also a design pressure: a category that wants an
eleventh case is a sign it is really two.

| Digit | Category | Concept |
|---|---|---|
| 1 | `Usage` | The argv, a value, or an interactive answer is wrong; fix the invocation. |
| 2 | `Key` | The encryption key cannot be resolved, is wrong, or its source is unusable. |
| 3 | `Bank` | The bank file itself: absent, corrupt, locked, wrong shape, mid-migration, repair not converging. |
| 4 | `Port` | Something holds the port and this run cannot bind, cycle or use it. |
| 5 | `Server` | An ai-raccoon server answered but cannot serve this invocation: another data root, no token, too old, refused. |
| 6 | `Reach` | No server could be reached, and none could be started. |
| 7 | `Model` | An embedding model or endpoint is unusable: hub, local directory, remote endpoint. |
| 8 | `Environment` | The local machine refuses: permissions, read-only root, disk space, state-directory secrets. |
| 9 | `Internal` | The product broke: a server 5xx, an unusable response, an unexpected exception, a bug guard. |

`Ok` keeps `Success = 0` and `SIGC = 130` outside the range — they are not failures, so
they do not compete for a category slot.

**Why categories, not a flat list.** A script that only ever needs to branch on "should I
retry" can test the tens digit alone: retry on `Reach` (no server could be reached — try
again, or start one), reconfigure on `Server` (a server answered, so retrying dials the
same wrong door), fix argv on `Usage`. That question was unanswerable from the old flat
numbering without a lookup table the script had to carry itself.

**No aliases.** The old `ExitCode` class is deleted, not deprecated; there is no
`ExitCode.NoBank = 22` sitting beside `ErrorCode.Bank.NoBank = 31` returning the same
value. A script pinned to an old number breaks the moment it upgrades, on purpose — a
silently-compatible alias would let "22 means no bank" survive as an accident of history
instead of the thing this ADR exists to end. This ships as **1.45.0**, a breaking change
called out as such.

Constants live as `ErrorCode.Category.Name`, e.g. `ErrorCode.Bank.NoBank = 31`,
`ErrorCode.Ok.Success = 0`. There is no flat `ErrorCode.NoBank`.

### Two categories where the boundary moved after the first pass

`Server` and `Reach` were once going to be one "the server said no" category, and were
split because together they had sixteen cases against a ten-case cap — and because they
answer different questions. `Reach` (6) is "no server answered, and none could be
started": retry, or start one yourself. `Server` (5) is "a server answered but will not
serve this invocation": another data root, no token, too old, or (new in this ADR) a
model verb refused while a migration is open. A script retries the first and
reconfigures for the second.

That second case is why `Bank.MigrationRefused` (the case this catalog's first draft
proposed as `Bank.37`) moved out of `Bank` entirely. The server, not the bank file,
is what says no to a model verb while a `model_migration` row is open — `doctor` reading
the bank directly still reports `Bank.MigrationOpen` (36); a model verb hitting the
server's 409 for the same open row reports `Server.MigrationRefused` (59). Moving it
freed `Bank`'s case 7 for a distinction the repair loop needed and did not have: `Bank`
holds 30-39, `Server` holds 50-59, both full at ten.

### The three repair end states

`repair project-ids --apply` had three end states that all exited `0`: converged,
*stuck* (the actionable set stopped shrinking), and *writers-active* (it hit its bound
while census totals kept growing, meaning something is still writing under a folded id).
Only the first is success. The owner ruling behind this ADR gives the other two their own
codes — `Bank.RepairStuck = 37` (not retryable: rerunning without quiescing writers or
reading the server log changes nothing) and `Bank.RepairWritersActive = 38` (retryable:
the writers may stop on their own) — and adds a fourth end state neither the original
catalog nor the code named separately: **`Bank.RepairAttentionNeeded = 39`** — the run
converged everything the machine can attribute, and what is left needs a person to say
which id is correct. The owner ruled this a failure under `--apply`: the operator asked the
repair to finish, and it cannot without a person, so `--apply` exits `39`. A dry run with
the same outcome only reports, and still exits `0`. Keeping it apart from `RepairStuck`
lets a script tell "a person has to attribute ids" from "the loop stopped moving rows".

## The full code table

Retryable **yes** means rerunning unchanged can succeed (a transient condition, or a
server that may finish what it is doing); **no** means fix the config, the argv, the
environment or the product first.

### 0 — `Ok`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 0 | `Ok.Success` | The command did what was asked. | — |
| 130 | `Ok.SIGC` | Cancelled by Ctrl-C/SIGTERM before it finished; nothing changed. | yes |

### 1 — `Usage`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 10 | `Usage.InvalidValue` | A value fits its slot in the grammar but is invalid: bad enum, non-number, out of range, removed option value. | no |
| 11 | `Usage.Unparseable` | The argv does not fit the grammar: unknown or misplaced token, extra token, missing subcommand. | no |
| 12 | `Usage.MissingValue` | A required value is absent: a required argument, an empty interactive secret, or `--account` with `--cli`. | no |
| 13 | `Usage.UndialablePort` | `--port` is 0 or outside 1-65535 on a path that must dial a fixed port (proxy, settings verbs, observability). | no |
| 14 | `Usage.RemovedTransport` | `--transport` names a removed value (`stdio`, `https`). | no |
| 15 | `Usage.AliasMapInvalid` | `repair project-ids --map` names a file that is missing, unreadable or not a valid alias map. | no |
| 16 | `Usage.ConfirmationDeclined` | `model download` over the size guard was not confirmed (no `--yes`, prompt declined, or stdin at EOF); nothing was touched. | no |
| 17 | `Usage.RequestRejected` | The server rejected the request as malformed (HTTP 400) for a reason the CLI pre-flight did not catch. | no |

### 2 — `Key`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 20 | `Key.Unresolved` | No encryption key could be resolved (the resolver threw); the most general key failure. | no |
| 21 | `Key.WrongKey` | The resolved key does not open the bank (keyed to another secret, or the env passphrase is missing); indistinguishable from corrupt today. | no |
| 22 | `Key.LegacyKeyDerivation` | The bank is still keyed under the pre-ADR-0012 derivation; `encryption migrate` fixes it. | no |
| 23 | `Key.BwsNotInstalled` | The Bitwarden CLI (`bws`) is not on `PATH` or cannot be started. | no |
| 24 | `Key.BwsTimedOut` | `bws` did not answer within its bound (5 s presence, 15 s fetch). | yes |
| 25 | `Key.BwsFailed` | `bws` ran but gave nothing usable: non-zero exit (auth, unknown secret id) or empty output. | no |
| 26 | `Key.SecretNotAKey` | The Bitwarden secret is not a parseable OpenSSH ed25519 private key. | no |
| 27 | `Key.SourceSidecarInvalid` | The `memory.db.source` sidecar is corrupt, names an unknown source, or is `bitwarden` without a secret id. | no |
| 28 | `Key.NoEnvPassphrase` | `encryption unset` cannot rekey back to env: `AIRACCOON_DB_PASSPHRASE` is not set. | no |
| 29 | `Key.BankKeyedToEnv` | `encryption bitwarden` did not switch the source: the bank still opens with the env passphrase, not the Bitwarden key. | no |

### 3 — `Bank`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 30 | `Bank.OpenFailed` | SQLite refused to open or read the bank (I/O error, cannot open, a post-open DDL/vec0 failure); the general bank failure. | no |
| 31 | `Bank.NoBank` | No bank file exists at the resolved path (wrong `--data-root`, or never served). | no |
| 32 | `Bank.Corrupted` | The file exists but is not a SQLite database (SQLITE_NOTADB); also what a wrong key looks like today. | no |
| 33 | `Bank.Busy` | The bank is locked or busy (SQLITE_BUSY/SQLITE_LOCKED) — another process holds it; retry. | yes |
| 34 | `Bank.SchemaMismatch` | The schema shape differs from this binary's DDL; `serve` repairs it on open. | no |
| 35 | `Bank.SchemaNewerThanBinary` | The bank's `user_version` is newer than this binary supports; update ai-raccoon. | no |
| 36 | `Bank.MigrationOpen` | Schema is healthy but a `model_migration` row is open; MCP tool calls are refused until the re-embed finishes. | yes |
| 37 | `Bank.RepairStuck` | `repair project-ids --apply` stopped with the same actionable set and zero rows moved, or hit its pass/time bound without census growth. | no |
| 38 | `Bank.RepairWritersActive` | `repair project-ids --apply` hit its bound while census totals grew: writers are active under folded ids. | yes |
| 39 | `Bank.RepairAttentionNeeded` | `repair project-ids --apply` converged, but ids remain that only a person can attribute; nothing more the machine can do. A dry run with the same outcome still exits `0`. | no |

### 4 — `Port`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 40 | `Port.InUse` | The bind was refused with address-in-use by a holder this run cannot cycle or identify; the general port case. | no |
| 41 | `Port.ForeignListener` | The listener on the port is not an ai-raccoon server. | no |
| 42 | `Port.LostDuringRestart` | `serve --restart`: another server took the port while this one was starting. | yes |
| 43 | `Port.HeldUnanswered` | `serve --restart`: the port gave the probe no answer, so nothing was asked to stop, and the bind proved it held. | yes |
| 44 | `Port.RestartTimedOut` | `serve --restart`: the server accepted the shutdown but still held the port at the bound. | yes |

### 5 — `Server`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 50 | `Server.Unproven` | An ai-raccoon listener holds the port but did not prove it serves this data root (another root, or no identity key). | no |
| 51 | `Server.NoToken` | This data root holds no token, so the server on the port cannot be asked anything (restart, settings, proxy session). | no |
| 52 | `Server.RestartTokenRefused` | `serve --restart`: the server refused this root's token — it serves another data root. | no |
| 53 | `Server.RequestTokenRefused` | A control-plane request got 401: the settings server refused this root's token — it serves another data root. | no |
| 54 | `Server.SessionRefused` | The proxy reached a proven backend, but it would not open an MCP session with this root's token. | no |
| 55 | `Server.TooOldToRestart` | `serve --restart`: the server on the port is too old to accept a shutdown request. | no |
| 56 | `Server.TooOldForObservability` | `serve observability`: the server answered 404 on `/observability` — too old to report its PID. | no |
| 57 | `Server.EndpointMissing` | A control-plane verb got 404 on its endpoint: the server predates the verb (e.g. `repair`, `noise`, `watch registered`). | no |
| 58 | `Server.OtlpNotEnabled` | `serve observability otlp`: the server does not export OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT` was not set when it started). | no |
| 59 | `Server.MigrationRefused` | The server's 409 refusal of a model verb while a `model_migration` row is open; nothing changed. | yes |

### 6 — `Reach`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 60 | `Reach.Unavailable` | No settings server answered at the port within the acquire budget, and none could be started there. | yes |
| 61 | `Reach.NothingListening` | `serve observability`: nothing is listening on the port. | no |
| 62 | `Reach.StoppedAnswering` | A settings server was acquired but a request failed at the transport (connection reset, timeout); the write certainly did not land. | yes |
| 63 | `Reach.BackendUnavailable` | The proxy found no backend on the port and could not start one there. | yes |
| 64 | `Reach.PrivateFallbackFailed` | The proxy's listener did not prove its identity, and the private fallback backend could not be started either. | yes |
| 65 | `Reach.StartFailed` | The backend executable could not be started as a process. | no |
| 66 | `Reach.AutoStartUnsupported` | This process cannot auto-start a backend: launched through the dotnet host, or its executable path is unknown. | no |

### 7 — `Model`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 70 | `Model.DownloadFailed` | A model download failed after the plan was accepted (a file fetch failed mid-way, a 0-byte body, or a local write failed); nothing half-installed is left. | yes |
| 71 | `Model.RepoNotFound` | Hugging Face did not resolve the repo id at the revision. | no |
| 72 | `Model.HubUnreachable` | The model hub could not be reached (DNS, connect, TLS, timeout) before any file was planned. | yes |
| 73 | `Model.ChecksumMismatch` | A downloaded file's SHA-256 does not match its LFS pin; the `.part` file is deleted. | yes |
| 74 | `Model.RuntimeRejected` | The downloaded ONNX graph failed the ONNX Runtime smoke load (corrupt, or an unsupported opset), or is not valid protobuf. | no |
| 75 | `Model.RepoUnsupported` | The repo cannot be planned: no ONNX export, unsupported model_type or tokenizer family, missing metadata, `--file` not in the tree, tree not valid JSON. | no |
| 76 | `Model.ManifestRejected` | A local model directory is unusable: no `manifest.json`, invalid manifest, a declared file missing or re-hashed, or a chunk budget too narrow for the code corpus. | no |
| 77 | `Model.EndpointUnreachable` | `model embedding set openai`: the embedding endpoint could not be reached or refused the probe (network, 401/403 bad API key, 5xx). | yes |
| 78 | `Model.BadBaseUrl` | `model embedding set openai`: `base-url` is not a usable absolute http(s) URL. Defined, not yet returned — see "Not yet distinguished." | no |
| 79 | `Model.DimensionMismatch` | `model embedding set openai`: the endpoint's output dimension contradicts `--dims`, or is not 384 and no `--dims` was given. | no |

### 8 — `Environment`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 80 | `Environment.IoFailed` | A local filesystem operation failed for a reason no narrower case names. | no |
| 81 | `Environment.PermissionDenied` | The OS denied access under `--data-root`. | no |
| 82 | `Environment.ReadOnlyDataRoot` | `--data-root` is on a read-only filesystem. | no |
| 83 | `Environment.PathTooLong` | `--data-root` is too long for this filesystem. | no |
| 84 | `Environment.DiskFull` | `model download`: the target volume has less free space than the plan needs; nothing was touched. | no |
| 85 | `Environment.TokenUnavailable` | `serve` cannot read, heal or mint the MCP loopback token in the state directory (state dir not writable). | no |
| 86 | `Environment.IdentityKeyUnavailable` | `serve` cannot read or mint the identity key in the state directory. | no |
| 87 | `Environment.SecretNotPrivate` | The state directory, or a secret file in it, is readable or writable by group/other, or owned by another user; `serve` refuses to use it. | no |
| 88 | `Environment.BindDenied` | `serve`: Kestrel's bind failed for a reason other than address-in-use (e.g. EACCES on a privileged port). | no |

### 9 — `Internal`

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 90 | `Internal.Unexpected` | The command failed for a reason no other code names; stderr carries the exception message. | no |
| 91 | `Internal.ServerError` | A control-plane request reached the server, which failed processing it (HTTP 5xx). | yes |
| 92 | `Internal.UnusableResponse` | The server answered with a status the CLI cannot use (a 4xx other than 400, 401, 404 and 409). A null or malformed body still exits `90`; see "Not yet distinguished." | no |
| 93 | `Internal.ManifestBug` | `model download` succeeded, but the manifest it generated failed validation — a product bug. | no |
| 94 | `Internal.UnhandledCommand` | A parsed command path has no dispatch arm — the tree and the dispatcher disagree; a product bug. | no |
| 95 | `Internal.Timeout` | An operation timed out inside the command, outside the paths that already name a timeout. | yes |

## Old → new mapping

One old number often reported several unrelated failures (the old scheme's own
ambiguities, several of them still open — see "Not yet distinguished"), so most rows
below split into more than one new code. The right-hand column is every new code the
audited catalog found behind that old number today.

| Old code | New code(s) it splits into |
|---|---|
| `1` | `Key.Unresolved` (20), `Key.BwsNotInstalled` (23), `Key.BwsTimedOut` (24), `Key.BwsFailed` (25), `Key.SecretNotAKey` (26), `Key.NoEnvPassphrase` (28) |
| `2` | `Key.WrongKey` (21), `Key.LegacyKeyDerivation` (22), `Bank.OpenFailed` (30), `Bank.Corrupted` (32), `Bank.Busy` (33) |
| `3` | `Port.InUse` (40), `Port.ForeignListener` (41), `Server.Unproven` (50) |
| `4` | `Server.TooOldForObservability` (56), `Reach.NothingListening` (61) |
| `5` | `Server.OtlpNotEnabled` (58) |
| `6` | `Server.SessionRefused` (54), `Reach.BackendUnavailable` (63), `Reach.PrivateFallbackFailed` (64), `Reach.StartFailed` (65), `Reach.AutoStartUnsupported` (66) |
| `7` | `Environment.TokenUnavailable` (85), `Environment.IdentityKeyUnavailable` (86), `Environment.SecretNotPrivate` (87) |
| `9` | `Usage.Unparseable` (11) |
| `10` | `Port.LostDuringRestart` (42) |
| `11` | `Server.NoToken` (51) |
| `12` | `Server.RestartTokenRefused` (52) |
| `13` | `Server.TooOldToRestart` (55) |
| `14` | `Port.RestartTimedOut` (44) |
| `15` | `Usage.InvalidValue` (10), `Usage.MissingValue` (12), `Usage.UndialablePort` (13), `Usage.RemovedTransport` (14), `Usage.AliasMapInvalid` (15), `Usage.ConfirmationDeclined` (16), `Usage.RequestRejected` (17), `Model.RepoUnsupported` (75), `Model.ManifestRejected` (76), `Model.DimensionMismatch` (79), `Environment.PermissionDenied` (81), `Environment.ReadOnlyDataRoot` (82), `Environment.PathTooLong` (83), `Environment.DiskFull` (84) |
| `16` | `Port.HeldUnanswered` (43) |
| `17` | `Server.RequestTokenRefused` (53) |
| `18` | `Reach.Unavailable` (60), `Reach.StoppedAnswering` (62) |
| `19` | `Bank.SchemaMismatch` (34) |
| `20` | `Bank.SchemaNewerThanBinary` (35) |
| `21` | `Model.DownloadFailed` (70), `Model.RepoNotFound` (71), `Model.HubUnreachable` (72), `Model.ChecksumMismatch` (73), `Model.RuntimeRejected` (74), `Internal.ManifestBug` (93) |
| `22` | `Bank.NoBank` (31) |
| `23` | `Internal.ServerError` (91) |
| `24` | `Bank.MigrationOpen` (36) |
| `25` | `Server.MigrationRefused` (59) |
| `26` | `Bank.Corrupted` (32) |
| `27` | `Key.SourceSidecarInvalid` (27), `Server.EndpointMissing` (57), `Model.EndpointUnreachable` (77), `Model.BadBaseUrl` (78), `Environment.IoFailed` (80), `Environment.BindDenied` (88), `Internal.Unexpected` (90), `Internal.UnusableResponse` (92), `Internal.UnhandledCommand` (94), `Internal.Timeout` (95) |
| `130` | `Ok.SIGC` (130) — unchanged |
| `0` (`encryption bitwarden` did not switch the source) | `Key.BankKeyedToEnv` (29) |
| `0` (`repair project-ids --apply` ended stuck) | `Bank.RepairStuck` (37) |
| `0` (`repair project-ids --apply` ended writers-active) | `Bank.RepairWritersActive` (38) |
| `0` (`repair project-ids --apply` ended attention-needed) | `Bank.RepairAttentionNeeded` (39) — a dry run with the same outcome still exits `0` |

Two things this table does not do. It does not re-derive the fan-out from first
principles — it is read off the audited catalog's own "Today" column for each new case,
which is itself a record of source-level evidence, not a rule this ADR invents. And it
does not chase every place a single old number was reached *inconsistently* by call
site — `Key.SourceSidecarInvalid`, for one, is folded into old `27` here as its general
case, although `serve` and `doctor` reported the same exception as `1` (serve silently).
Since 1.45.0 every one of those call sites classifies the exception the same way and
prints a line naming it, so `serve` no longer exits without saying why.

## Not yet distinguished

Cases the code cannot tell apart yet return the category's general code; the list is
maintained below.

- **Wrong key vs. corrupt bank — split by ADR-0111.** SQLCipher still answers the same
  SQLITE_NOTADB error for a wrong key and for a file that genuinely is not a database, but
  a bank that has opened successfully at least once now carries a key-check sidecar
  (`memory.db.keycheck`) outside the encrypted pages, kept outside the bank as this item
  originally called for. When that sidecar can settle the question, `Key.WrongKey` (21)
  and `Bank.Corrupted` (32) resolve to the case it actually is, on every call path
  including `doctor`. A bank with no sidecar yet (never reopened since upgrading) keeps
  the prior ambiguous, both-causes message and its call path's old default code.
- **A bad `base-url` is now distinguished by code**: `model embedding set openai` refuses
  a `base-url` that is not a usable absolute http(s) URL before anything is probed or
  persisted, exiting `Model.BadBaseUrl` (78). An unreachable endpoint and a rejected API
  key still share `Model.EndpointUnreachable` (77) — the Model range (70-79) has no free
  case for "rejected key" — but a rejected key (the OpenAI SDK's `ClientResultException`
  with a 401/403 status) now gets its own message naming the key, instead of the generic
  "could not be reached" text a truly unreachable endpoint gets.
- **A malformed or null server body** exits `Internal.Unexpected` (90), not
  `Internal.UnusableResponse` (92): only a non-success status is classified today.
- **A read-only `--data-root`** (82) is still recognised by the OS message text, which
  depends on platform and locale; a path too long (83) is recognised by type first. Every
  `UnauthorizedAccessException` is reported as `Environment.PermissionDenied` (81) against
  `--data-root`, even when the denied path was a `--dir` elsewhere.
- **A local write failure during a model download** exits `Model.DownloadFailed` (70),
  not an `Environment` code.
- **`Server.Unproven` (50)** still collapses every `IdentityProofFailure` (no identity
  key, a root mismatch, a bad signature, a malformed answer, a non-success status, or a
  timeout) onto one code — `Server` (50-59) is full — but the refusal now names
  which one it was on stderr, never the log line. **`Server.NoToken` (51)** no longer
  hedges "it may serve another data root": every site that returns it has already proven
  the listener's identity before the token is read, so that was never a live possibility.
  It now surfaces the token file's own refusal (a chmod-600/700 remedy) when one is set,
  and otherwise says the token is missing for this data root — never that it was deleted,
  since a just-spawned fallback `serve` may not have written it yet.
- **`serve observability`** judges by status alone: a foreign listener that answers 404
  reads as `Server.TooOldForObservability` (56), and an ai-raccoon 5xx as
  `Port.ForeignListener` (41).
- **`Bank.RepairWritersActive` (38) is best-effort**: it is told from `RepairStuck` (37)
  by census growth between passes, so a writer that deletes as fast as it writes reads
  as stuck.

## Consequences

- Every script, CI check, and manual runbook that branches on an `ai-raccoon` exit code
  by number needs updating for 1.45.0. There is no compatibility shim; see "Alternatives
  rejected" for why.
- `docs/how-to/configure-ai-raccoon-server.md`'s `doctor` and settings exit-code tables,
  and every other current doc naming an exit code, are renumbered in the same release.
  Each table row names its constant, and a test checks the constant holds the row's code.
- `ExitCode` is deleted; `ErrorCode` with nested `Ok`/`Usage`/`Key`/`Bank`/`Port`/
  `Server`/`Reach`/`Model`/`Environment`/`Internal` static classes replaces it, one
  constant per case, matching the table above exactly.
- A category is capped at ten cases by the scheme itself. `Server` and `Bank` are both
  already full (ten each); the next case in either forces either a narrower existing
  case to fold back into its `x0`, or a tenth category, which this ADR does not open.

## Alternatives rejected

- **Keep the flat list, just fill in more numbers.** Rejected: a flat number carries no
  information a script can act on without its own lookup table, which is the problem
  this ADR exists to fix, not a compatible improvement on it.
- **Alias the old constants to the new values.** Rejected: `ExitCode.NoBank` and
  `ErrorCode.Bank.NoBank` returning the same number forever would mean nobody is ever
  forced to notice the meaning moved, and "22 means no bank" would keep being true by
  accident instead of by the category scheme. A breaking change that is silently
  compatible is not a breaking change; it is a foot-gun with a delay on it.
- **Three-digit codes, for more headroom per category.** Rejected outright: an exit
  status is one byte (see "Context"), so nothing above 255 is a real exit code on any
  platform this ships on, and 126-255 already belong to shells and signals.
- **A flat two-digit range with no category structure (just 10-99 assigned in
  discovery order, as the old scheme already was in miniature).** Rejected: this is the
  status quo with two digits instead of one, and inherits the same problem — a number a
  script cannot reason about without memorizing the table.

## Related decisions

- [ADR-0060 — An unrecognised verb must not launch anything](0060-an-unrecognised-verb-must-not-launch-anything.md)
  named `FailedToParseCliArgs` (9) and `InvalidArgument` (15) as the split this ADR
  renumbers to `Usage.Unparseable` (11) and the `Usage.*`/other-category cases `15`
  used to cover; amended in place with this ADR's numbers.
- [ADR-0106 — Attach-or-start again, proven by a per-root identity key](0106-attach-or-start-with-backend-identity-proof.md)
  named exit `3` (unproven listener), `9` (removed `--attach`), and `22`
  (`ExitCode.NoBank`, the F39 guard) — its D4 decision is amended in place with this
  ADR's numbers (`Server.Unproven` = 50, `Usage.Unparseable` = 11, `Bank.NoBank` = 31).

## Evidence

Source catalog: every code above and its old-code origin was read from `src/` at
`origin/main` 4e50ce35 (VERSION 1.44.4) — CLI verb handlers, `CliFailureExitCode`,
`ExitCode.cs`, the doctor command, the encryption commands, the model download service,
and the proxy/settings backend-acquire paths. 75 distinct failure shapes were counted
behind 27 numbers, plus the four `0`-exit failures this ADR gives real codes. The
8-bit constraint was measured directly (`exit(1003)` → `$?` = `235`, this machine).
