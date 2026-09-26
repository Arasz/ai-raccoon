# CLI reference

The complete `ai-raccoon` verb tree and exit codes, read directly from
`src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (the System.CommandLine grammar) and
`src/AiRaccoon/ErrorCode.cs` (ADR-0107's exit codes). This page is the flat list; for
the behavior behind `serve`, the proxy, attach-or-start, and what each config verb
actually changes, see [agent-memory-server.md](agent-memory-server.md#command-line-options).

To re-derive this page after the CLI grammar changes, read `CliCommandTree.cs` for the
verb tree and `ErrorCode.cs` for the exit codes: both are short, single files, and this
page has no test wired to either one, so it drifts unless someone re-reads them by hand.

## Launch (no verb)

A bare `ai-raccoon [options]` runs the proxy (or, with `--transport http`, still the
proxy; full servers come only from `serve`).

| Option | Values | Default |
|---|---|---|
| `--transport` | `proxy`, `http` | `proxy` |
| `--data-root <path>` | any path (`~` expanded); must precede the verb | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project`; must precede the verb | `user` |
| `--quiet` | flag | off |
| `--port <n>` | `1`-`65535` | `7721` |
| `--environment`, `--contentRoot`, `--applicationName` | hidden generic host flags, accepted and ignored | n/a |

## Top-level verbs

| Verb | Purpose |
|---|---|
| `settings <family> …` | Runtime configuration, one node per subsystem; see [settings](#settings) below |
| `model embedding set local [path]` | Selects the memory bank's embedding engine: the bundled model, or an override path |
| `model embedding set openai <model-id> [base-url] [--api-key <key>] [--dims <n>]` | Selects an OpenAI-compatible embedding endpoint for memory |
| `model code set default` | Activates the bundled embedding model for the code corpus (same model memory uses); downloads nothing |
| `model code set local <dir>` | Activates a manifest directory for the code corpus |
| `model download <repo-id> [--revision <rev>] [--file <path>]... [--dir <path>] [--dry-run] [--yes]` | Downloads a Hugging Face embedding model into `<data-root>/models/<slug>` with SHA-256 pins; does not activate it |
| `watch registered [project-id]` | Lists registered watches (project, path, registered at, last change) from the watches table |
| `extract prune [--apply]` | Reports (default) or removes `promotion_queue` rows whose backing entry is gone |
| `noise entries` | Summarizes `noise_entries`, the training-data source for a future noise learner |
| `encryption bitwarden [-t <token>]` | Configures Bitwarden Secrets Manager as the bank key source; rekeys the bank |
| `encryption show` | Shows the current encryption source |
| `encryption unset` | Returns to the env-passphrase default; rekeys when `AIRACCOON_DB_PASSPHRASE` is set |
| `encryption migrate` | Rekeys a bank still on the pre-ADR-0012 key derivation |
| `repair chunk-index [--apply]` | Reports (default) or fixes chunk positions that drifted from document order |
| `repair reingest [--apply]` | Reports (default) or re-ingests files a chunker change made unreproducible |
| `repair project-ids [--apply] [--queue-only] [--diagnose] [--map <path>]` | Diagnoses (default) or folds fragment project ids into one |
| `serve [options]` | Runs the HTTP MCP endpoint in the foreground; see [serve](#serve) below |
| `doctor` | Shows bank schema/version, code engine state and embedding settings, without changing the bank |

## settings

Every family below lives under `ai-raccoon settings <family> …`, except the operations
listed above that read a non-settings table or mutate data (`watch registered`,
`extract prune`, `noise entries`, `model embedding set`, `model code set`, `encryption`,
`serve`).

| Command | Purpose |
|---|---|
| `settings access default set {ro\|rw\|full}` / `default show` | Global default access mode |
| `settings access set {project-id\|*} {ro\|rw\|full}` | Per-project access override |
| `settings access unset {project-id\|*}` | Drops a per-project override |
| `settings access list` (alias `show`) | Lists the default and every override |
| `settings model embedding reset` (aliases `unset`, `remove`) / `embedding show` (alias `list`) | Memory engine configuration reset/inspect |
| `settings model code reset` (aliases `unset`, `remove`) / `code show` (alias `list`) | Code engine configuration reset/inspect; never touches the memory engine |
| `settings model reset` (aliases `unset`, `remove`) / `model show` (alias `list`) | Same reset/show pair, reported together; `show` also includes the code rows when set |
| `settings model threads {n}` | ORT intra-op thread cap for local embedding sessions (`0` = ORT default, unset = `max(1, logicalCores/2)`); takes effect on next restart |
| `settings model device {auto\|gpu\|cpu\|mlx\|coreml\|cuda [path]}` | Where local embedding sessions run: `auto` (default) puts only the bundled model on the GPU; `mlx` is bundled-engine + osx-arm64 only (ADR-0110); `coreml` is bundled-engine + osx-arm64 only, on the Neural Engine (ADR-0118); `cuda` needs a path to a CUDA provider library and applies to every local model (ADR-0112); takes effect on next restart (ADR-0108) |
| `settings retrieval alpha set {0..1}` / `alpha show` | Dual-vector fusion alpha, default `0.5` |
| `settings retrieval fusion enable` / `disable` / `show` (alias `list`) | No-fusion-regression reorder, off by default |
| `settings retrieval rrfk set {n}` / `show` | `retrieval.rrfK`, integer ≥ 1, default `60` |
| `settings retrieval fts-weight set {n}` / `show` | `retrieval.ftsWeight`, integer ≥ 0, default `1` |
| `settings retrieval vector-weight set {n}` / `show` | `retrieval.vectorWeight`, integer ≥ 0, default `1` |
| `settings retrieval source-lambda set {0..1}` / `show` | `retrieval.sourceLambda`, default `0.1` |
| `settings retrieval consolidation set {n}` / `show` | `retrieval.consolidationThreshold`, ≥ 0, default `0.1` |
| `settings retrieval doc-formula set {max\|sum}` / `show` | `retrieval.docScoreFormula`, default `max` |
| `settings retrieval window set {max3x100\|max5x50}` / `show` | `retrieval.candidateWindow`, default `max3x100` |
| `settings retrieval show-all` (alias `list`) | Every retrieval option with its source (setting or default) |
| `settings sweep enable` / `disable` | Background reaper kill switch, armed (enabled) by default |
| `settings sweep interval-hours {1..8760}` | Reaper cadence, default `24`; applies live |
| `settings sweep threshold set {0..1}` | Sweep rating threshold, default `0.3` |
| `settings sweep show` (alias `list`) | Whole sweep policy: enabled, interval, threshold |
| `settings noise enable` / `disable` | Pre-write noise rejection kill switch, armed by default |
| `settings noise show` (alias `list`) | Whether pre-write noise rejection is enabled |
| `settings queryguard enable` / `disable` | Read-path query guard kill switch, armed by default |
| `settings queryguard shadow enable` / `disable` | Shadow mode: records verdicts without acting on them (off by default) |
| `settings queryguard structural enable` / `disable` | Structural detector, a learned annotation-only input (off by default) |
| `settings queryguard structural threshold set {0..1}` | Score the structural detector must clear to annotate |
| `settings queryguard show` (alias `list`) | Whether the guard, shadow mode and the structural detector are enabled |
| `settings sync add s3 <url> --bucket <name> [--region <name>] [--object-key <key>] [--cli]` | Configures S3-compatible cloud sync |
| `settings sync add azure <container> [--object-key <key>] [--cli --account <name>]` | Configures Azure Blob cloud sync |
| `settings sync remove` (aliases `reset`, `unset`) | Back to sync off |
| `settings sync show` (alias `list`) | Sync configuration with secrets redacted |
| `settings ingest scope add {project-id\|*} <path>` | Adds a path to the ingest scope allowlist |
| `settings ingest scope remove {project-id\|*} <path>` | Removes a path from the allowlist |
| `settings ingest scope list {project-id\|*}` | Lists a target's scope allowlist |
| `settings watch enable {project-id\|*} {true\|false}` / `disable …` | Enables/disables watching for a target (configuration only) |
| `settings watch concurrency {project-id\|*} {1..16}` | Watcher concurrency, default `4` |
| `settings watch remove {project-id\|*}` | Removes all watch config rows for a target |
| `settings watch list` (alias `show`) | Each target's watch configuration (enabled, concurrency, scope) |
| `settings extract enable {true\|false}` | Background shared-extraction kill switch, disabled by default |
| `settings extract mode {propose\|promote}` | Extraction mode, default `propose` |
| `settings extract interval {minutes}` | Extraction pass interval, default `30` |
| `settings extract capacity {n}` | Propose-tier queue capacity, default `1000` |
| `settings extract auto-promote-threshold {score\|off}` | Score-gated auto-promote threshold, default `off` |
| `settings extract exclude add/remove/list {prefix}` | `source_file` prefixes excluded from shared-extraction candidacy |
| `settings extract list` (alias `show`) | Extraction configuration: enabled, mode, interval |
| `settings maintenance interval {minutes}` | WAL checkpoint interval, default `60` |
| `settings maintenance vacuum-interval {days}` | VACUUM + ANALYZE interval, default `7` |
| `settings maintenance embed-rows-per-run {n}` | Embed drain rows per signal, both corpora, default `128` |
| `settings maintenance list` (alias `show`) | Bank maintenance configuration |
| `settings performance buffer-capacity {n}` | Measurement buffer capacity; takes effect on next restart |
| `settings performance flush-interval {seconds}` | Metrics flush interval; takes effect on next flush tick |
| `settings performance retention {days}` | Hot metrics-table retention; takes effect on next maintenance pass |
| `settings performance list` (alias `show`) | Metrics subsystem configuration |

## serve

| Option | Purpose |
|---|---|
| `--port <n>` | HTTP port to bind; `0` picks a random free port. Default `7721` |
| `--idle-timeout <span>` | Idle shutdown span (`90s`/`30m`/`4h`/`1d`); `0` disables. Default `4h` |
| `--mcp-entry` | Prints the MCP client config entry for the bound URL |
| `--format <hermes\|claude\|all>` | Entry format for `--mcp-entry`. Default `hermes` |
| `--restart` | Stops the ai-raccoon server already on the port and serves in its place |
| `serve observability {counters\|trace\|otlp\|pid} [--port <n>]` | Prints a ready-to-run monitoring command for the live server |

## Exit codes

Two digits, `{category}{case}`: the tens digit is one of nine categories, the ones digit
a case within it, and `x0` is always that category's most general case. `Ok` (`0` and
`130`) sits outside the range. **Retryable** means rerunning unchanged can succeed; a
non-retryable code needs the config, argv, environment or product fixed first.

| Digit | Category | Concept |
|---|---|---|
| 0 | `Ok` | Not a failure |
| 1 | `Usage` | The argv, a value, or an interactive answer is wrong |
| 2 | `Key` | The encryption key cannot be resolved, is wrong, or its source is unusable |
| 3 | `Bank` | The bank file itself: absent, corrupt, locked, wrong shape, mid-migration, repair not converging |
| 4 | `Port` | Something holds the port and this run cannot bind, cycle or use it |
| 5 | `Server` | An ai-raccoon server answered but cannot serve this invocation |
| 6 | `Reach` | No server could be reached, and none could be started |
| 7 | `Model` | An embedding model or endpoint is unusable |
| 8 | `Environment` | The local machine refuses: permissions, read-only root, disk space, state-directory secrets |
| 9 | `Internal` | The product broke: a server 5xx, an unusable response, an unexpected exception |

| Code | Const | Meaning | Retry |
|---|---|---|---|
| 0 | `Ok.Success` | The command did what was asked | n/a |
| 130 | `Ok.SIGC` | Cancelled by Ctrl-C/SIGTERM before it finished; nothing changed | yes |
| 10 | `Usage.InvalidValue` | A value fits its slot in the grammar but is invalid | no |
| 11 | `Usage.Unparseable` | The argv does not fit the grammar | no |
| 12 | `Usage.MissingValue` | A required value is absent | no |
| 13 | `Usage.UndialablePort` | `--port` is `0` or outside 1-65535 on a path that must dial a fixed port | no |
| 14 | `Usage.RemovedTransport` | `--transport` names a removed value (`stdio`, `https`) | no |
| 15 | `Usage.AliasMapInvalid` | `repair project-ids --map` names a file that is missing, unreadable or invalid | no |
| 16 | `Usage.ConfirmationDeclined` | `model download` over the size guard was not confirmed | no |
| 17 | `Usage.RequestRejected` | The server rejected the request as malformed (HTTP 400) | no |
| 20 | `Key.Unresolved` | No encryption key could be resolved | no |
| 21 | `Key.WrongKey` | The resolved key does not open the bank | no |
| 22 | `Key.LegacyKeyDerivation` | The bank is still keyed under the pre-ADR-0012 derivation; `encryption migrate` fixes it | no |
| 23 | `Key.BwsNotInstalled` | The Bitwarden CLI (`bws`) is not on `PATH` or cannot be started | no |
| 24 | `Key.BwsTimedOut` | `bws` did not answer within its bound | yes |
| 25 | `Key.BwsFailed` | `bws` ran but gave nothing usable | no |
| 26 | `Key.SecretNotAKey` | The Bitwarden secret is not a parseable OpenSSH ed25519 private key | no |
| 27 | `Key.SourceSidecarInvalid` | The `memory.db.source` sidecar is corrupt or names an unknown source | no |
| 28 | `Key.NoEnvPassphrase` | `encryption unset` cannot rekey back to env: no `AIRACCOON_DB_PASSPHRASE` | no |
| 29 | `Key.BankKeyedToEnv` | `encryption bitwarden` did not switch the source | no |
| 30 | `Bank.OpenFailed` | SQLite refused to open or read the bank | no |
| 31 | `Bank.NoBank` | No bank file exists at the resolved path | no |
| 32 | `Bank.Corrupted` | The file exists but does not open as a database: a corrupt bank or a wrong key, which SQLCipher cannot tell apart (the message names both remedies) | no |
| 33 | `Bank.Busy` | The bank is locked or busy | yes |
| 34 | `Bank.SchemaMismatch` | The schema shape differs from this binary's DDL; `serve` repairs it on open | no |
| 35 | `Bank.SchemaNewerThanBinary` | The bank's `user_version` is newer than this binary supports | no |
| 36 | `Bank.MigrationOpen` | A `model_migration` row is open; MCP tool calls are refused until the re-embed finishes | no |
| 37 | `Bank.RepairStuck` | `repair project-ids --apply` stopped with actionable rows still in place | no |
| 38 | `Bank.RepairWritersActive` | `repair project-ids --apply` hit its bound while writers are still active | yes |
| 39 | `Bank.RepairAttentionNeeded` | The repair converged everything it can attribute; a person must pick the rest | no |
| 40 | `Port.InUse` | The port is in use by a holder this run cannot cycle or identify | no |
| 41 | `Port.ForeignListener` | The listener on the port is not an ai-raccoon server | no |
| 42 | `Port.LostDuringRestart` | `serve --restart`: another server took the port while this one was starting | yes |
| 43 | `Port.HeldUnanswered` | `serve --restart`: the port gave the probe no answer | no |
| 44 | `Port.RestartTimedOut` | `serve --restart`: the server accepted shutdown but still held the port at the bound | no |
| 50 | `Server.Unproven` | An ai-raccoon listener holds the port but did not prove it serves this data root (the message names why the proof failed) | no |
| 51 | `Server.NoToken` | This data root holds no token, so the server on the port cannot be asked anything | no |
| 52 | `Server.RestartTokenRefused` | `serve --restart`: the server refused this root's token | no |
| 53 | `Server.RequestTokenRefused` | A control-plane request got 401 | no |
| 54 | `Server.SessionRefused` | The proxy reached a proven backend, which would not open an MCP session with this root's token | no |
| 55 | `Server.TooOldToRestart` | `serve --restart`: the server on the port is too old to accept a shutdown request | no |
| 56 | `Server.TooOldForObservability` | `serve observability`: the server answered 404 on `/observability` | no |
| 57 | `Server.EndpointMissing` | A control-plane verb got 404: the server predates the verb | no |
| 58 | `Server.OtlpNotEnabled` | `serve observability otlp`: the server does not export OTLP | no |
| 59 | `Server.MigrationRefused` | A model verb was refused (409) because a model migration is open | yes |
| 60 | `Reach.Unavailable` | No settings server answered at the port, and none could be started there | n/a |
| 61 | `Reach.NothingListening` | `serve observability`: nothing is listening on the port | n/a |
| 62 | `Reach.StoppedAnswering` | A settings server was acquired but a request failed at the transport | n/a |
| 63 | `Reach.BackendUnavailable` | The proxy found no backend on the port and could not start one there | n/a |
| 64 | `Reach.PrivateFallbackFailed` | The listener did not prove its identity and the private fallback could not start either | n/a |
| 65 | `Reach.StartFailed` | The backend executable could not be started as a process | n/a |
| 66 | `Reach.AutoStartUnsupported` | This process cannot auto-start a backend (launched through the `dotnet` host, or its path is unknown) | no |
| 70 | `Model.DownloadFailed` | A model download failed after the plan was accepted; nothing half-installed is left | no |
| 71 | `Model.RepoNotFound` | Hugging Face did not resolve the repo id at the revision | no |
| 72 | `Model.HubUnreachable` | The model hub could not be reached before any file was planned | yes |
| 73 | `Model.ChecksumMismatch` | A downloaded file's SHA-256 does not match its LFS pin | no |
| 74 | `Model.RuntimeRejected` | The downloaded ONNX graph failed the ONNX Runtime smoke load | no |
| 75 | `Model.RepoUnsupported` | The repo cannot be planned: no ONNX export, unsupported model or tokenizer | no |
| 76 | `Model.ManifestRejected` | A local model directory is unusable: no/invalid manifest, or a declared file missing or re-hashed | no |
| 77 | `Model.EndpointUnreachable` | `model embedding set openai`: the endpoint could not be reached, refused the probe, or rejected the API key (the message names the key) | no |
| 78 | `Model.BadBaseUrl` | `model embedding set openai`: `base-url` is not a usable absolute http(s) URL | no |
| 79 | `Model.DimensionMismatch` | `model embedding set openai`: the endpoint's dimension contradicts `--dims`, or is non-384 with no `--dims` | no |
| 80 | `Environment.IoFailed` | A local filesystem operation failed for a reason no narrower case names | no |
| 81 | `Environment.PermissionDenied` | The OS denied access under `--data-root` | no |
| 82 | `Environment.ReadOnlyDataRoot` | `--data-root` is on a read-only filesystem | no |
| 83 | `Environment.PathTooLong` | `--data-root` is too long for this filesystem | no |
| 84 | `Environment.DiskFull` | The target volume has less free space than the model download needs | no |
| 85 | `Environment.TokenUnavailable` | `serve` cannot read, heal or mint the MCP loopback token | no |
| 86 | `Environment.IdentityKeyUnavailable` | `serve` cannot read or mint the identity key in the state directory | no |
| 87 | `Environment.SecretNotPrivate` | The state directory or a secret file in it is not owner-only | no |
| 88 | `Environment.BindDenied` | `serve`: the bind failed for a reason other than address-in-use | no |
| 90 | `Internal.Unexpected` | The command failed for a reason no other code names; stderr carries the exception message | no |
| 91 | `Internal.ServerError` | A control-plane request reached the server, which failed processing it (HTTP 5xx) | yes |
| 92 | `Internal.UnusableResponse` | The server answered with a status the CLI cannot use | no |
| 93 | `Internal.ManifestBug` | A model download succeeded but the manifest it generated failed validation | no |
| 94 | `Internal.UnhandledCommand` | A parsed command path has no dispatch arm | no |
| 95 | `Internal.Timeout` | An operation timed out inside the command, outside paths that name their own timeout | yes |

See [ADR-0107](../adr/0107-categorized-two-digit-exit-codes.md) for the design rationale,
and `docs/how-to/configure-ai-raccoon-server.md` for the test-verified subset `doctor`
and the settings channel actually exit with.
