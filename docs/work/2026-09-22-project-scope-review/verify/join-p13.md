# Join review — P1.3 (F70 / K1 private spawn) — independent verification

**Verdict summary:** the default-launch attack F70 measured is genuinely closed on the proxy and
plain-`serve` paths. The lane's headline claim ("private spawn by default, attach behind `--attach`")
is **true for the proxy, but not globally**: `serve --restart` and the server-routed CLI commands
(`CliSettingsBackend`) still hand the real loopback token to a squatter holding the configured port.
K1's gate as written — "a squatter on the configured port never receives the token because no attach
happens" — is **not satisfied by the delivered tree**.

**Scope:** worktree `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/w1-p13`, branch
`task/psr-w1-p13`, `5bca1900..65963199` (`41b8af97` feat, `65963199` docs/ADR-0105). Read-only: no
tracked file modified, nothing committed (`git status --short` empty after the review). Built binary
identity `1.42.5+659631991ac68aa187086d534d7dccd602d980f1` (matches HEAD). All live work under
`docs/work/2026-09-22-project-scope-review/verify/join-p13/` with scratch data roots and explicit free ports; `~/.ai-raccoon`
never read or written; no `mcp_ai-raccoon_*` tool called.

---

## 1. What actually changed at the attach / token-send decisions — **CONFIRMED**

Read from `git diff 5bca1900..HEAD` (28 files, +862/−68) and HEAD sources:

- **Default proxy path is private spawn.** `BackendSessions.AcquireBackend` branches on
  `config.Attach` (`src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:78-80`): `false` →
  `StartPrivateAsync(..., PrivateServeArguments(config), ...)`; `true` → legacy
  `AcquireAsync(config.Port, ...)`.
- **Private spawn never probes or dials the configured port.**
  `BackendLauncher.StartPrivateAsync` (`src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs:52-88`) starts
  the child and returns only the URL parsed from that child's stdout
  (`WatchUrlLineAsync`/`TryParseBackendUrl`, `:199-251`). `PrivateServeArguments` pins `--port 0`
  (`src/AiRaccoon/Hosting/Common/BackendLaunchArguments.cs:46-53`).
- **`serve` without `--attach` refuses an ai-raccoon-occupied port.** `NodeRunner.cs:84-88` and
  `:206-209` branch on `descriptor.Attaching`; `RefuseExistingServerAsync` (`:231-238`) returns
  `ExitCode.PortInUse` (3). `Attaching = options.Attach` (`:41`).
- **`--attach` is opt-in everywhere, never defaulted.** `ServerConfig.Attach` defaults to `false`
  (`src/AiRaccoon/Hosting/Common/ServerConfig.cs:23`); `RootCliOptions.Attach` false
  (`RootCliOptions.cs:28`); the only assignments come from parsed options
  (`CliArgs.cs:189`, `CliOptionsExtensions.cs:33`, `ServeCommands.cs:29`). `rg "Attach = true" src/`
  finds nothing.
- **CLI plumbing.** Root and serve `--attach` options (`CliCommandTree.cs:59-68`), serve-wins
  precedence with presence detected via `OptionResult.Implicit == false` (`ServeCommands.cs:64-78`).
- **`ServerProbe` verdict NOT narrowed globally — CONFIRMED.**
  `git diff 5bca1900..HEAD -- .../ServerProbe.cs .../ServerRestart.cs` is empty. `Answered` is still
  `body.Contains("jsonrpc")` (`src/AiRaccoon/Hosting/Common/ServerProbe.cs:69`), and the consumers are
  unchanged: `serve` pre-check (`NodeRunner.cs:71`, `:138`), `WaitForPortToFreeAsync`
  (`ServerRestart.cs:160`), restart pre-check (`ServerRestart.cs:60`), attach path
  (`BackendLauncher.cs:98,117,157`). The change sits at the token-send/attach decisions as P1.3
  required.
- **Legacy settings path deliberately left.** `CliSettingsBackend.AcquireAsync` still calls
  `launcher.AcquireAsync` (`src/AiRaccoon/Settings/CliSettingsBackend.cs:50`) regardless of
  `Attach`, reads the token (`:66`) and builds a `ServerSettingsStore` that sends it as a default
  header (`:70`; `ServerSettingsStore` constructor). ADR-0105 declares this "out of F70's blast
  radius" — see §3a for the falsification.
- **28 files changed — CONFIRMED** (`git diff --stat`).

## 2. Gates run — **CONFIRMED** (all green; filters only, no `--nologo`)

| gate | command | result |
|---|---|---|
| squatter gate + attach positive control | `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build --filter "FullyQualifiedName~BackendSessionsTokenExposureTests"` | `total: 2 failed: 0 succeeded: 2` |
| serve refusal consumer | `... --filter "FullyQualifiedName~NodeRunnerTests.BusyPortWithAiRaccoonServer_WithoutAttach"` | `total: 1 failed: 0 succeeded: 1` |
| restart attach consumer | `... --filter "FullyQualifiedName~ServeRestartTests.WithoutRestart_WithAttach"` | `total: 1 failed: 0 succeeded: 1` |
| full restart class (real-server cycle) | `... --filter "FullyQualifiedName~ServeRestartTests"` | `total: 10 failed: 0 succeeded: 10` |
| launcher private mechanics | `... --filter "FullyQualifiedName~BackendLauncherTests.StartPrivate"` | `total: 3 failed: 0 succeeded: 3` |
| full launcher class | `... --filter "FullyQualifiedName~BackendLauncherTests"` | `total: 12 failed: 0 succeeded: 12` |
| full serve consumer class | `... --filter "FullyQualifiedName~NodeRunnerTests"` | `total: 15 failed: 0 succeeded: 15` |
| CLI `--attach` parsing (7 tests) | `... --filter "FullyQualifiedName~CliArgsTests.Parse_AttachFlag\|...Parse_ServeAttach\|...Parse_RootAttach\|...Parse_NoAttach"` | `total: 7 failed: 0 succeeded: 7` |
| sessions attach/private error text | `... --filter "FullyQualifiedName~BackendSessionsTests.OpenAsync_WhenTheLauncherFindsNoUrl\|...OpenAsync_WithAttach"` | `total: 2 failed: 0 succeeded: 2` |
| settings backend unit class | `... --filter "FullyQualifiedName~CliSettingsBackendTests"` | `total: 8 failed: 0 succeeded: 8` |

The squatter gate is not vacuous today: polling during its run observed the real private child
(`AiRaccoon ... serve --port 0`, PID 51936, scratch root under the test temp dir) while the gate
stayed green. The attach positive control opens a session against a real `serve` through the token
and lists tools. Caveat on the gate's *specificity* — see Residual risk R6.

## 3. Attack results

### 3a. Can a squatter still receive the token? — **CORRECTED**

Live harness: `docs/work/2026-09-22-project-scope-review/verify/join-p13/verify.py` + `squatter.py`, minted token
`ewdlssGwueiRFQKg2lH0SGThyuS3Pr4qiU6tP4ZLTho`, explicit free ports, squatter answers `/mcp` with a
JSON-RPC body and `/observability` with a configurable name.

| route | evidence | verdict |
|---|---|---|
| default proxy (`--data-root … --port <squatter>`, no flag) | squatter request count **0**; proxy exit 0; proxy stdout carries the real backend's `memory_write` result (`"stored":true`); child observed on `TCP 127.0.0.1:62612 (LISTEN)` | **closed** |
| `serve` alone on the squatter port | exit **3**; squatter saw only the tokenless probe `POST /mcp` body `"x"` (token `null`); stderr: `port 62619 is in use by an ai-raccoon server — pass --attach to use it, or --port 0 to start a private one` | **closed** |
| `serve --restart` on a self-identifying squatter (`/observability` name `ai-raccoon`) | exit **14** (`RestartTimedOut`); squatter received `GET /observability` then **`POST /shutdown` with `X-AiRaccoon-Token: ewdlss…`** — the real minted token, byte-for-byte | **NOT closed** |
| CLI settings command (`noise entries`, server-routed) | exit **15**; squatter received **`GET /noise/summary` with the same real token**; no `--attach` was passed | **NOT closed** |
| `--port <squatter>` set by the user | same as default proxy — 0 requests | **closed** |
| `--attach` defaulted anywhere | no `Attach = true` in `src/`; defaults false (`ServerConfig.cs:23`) | **closed** |
| `--attach` explicitly (control) | squatter received the token on `server/discover`, `initialize`, GETs and the full `tools/call` `memory_write` payload; forged result relayed to agent stdout | by design, matches ADR/SECURITY.md |

Sources for the two open routes: `ServerRestart.cs:70` (identify by self-asserted
`/observability` name), `:77` (read token), `:83`/`:128-135` (send it on `POST /shutdown`);
`CliSettingsBackend.cs:50` (legacy `AcquireAsync`), `:66` (read token), `:70` (send via
`ServerSettingsStore`). `CliWriteOptOuts.WritesDirectly` exempts only `encryption`, `doctor` and
`serve` (`src/AiRaccoon/Settings/CliWriteOptOuts.cs`), so every other CLI verb (`settings …`,
`model …`, `watch registered`, `extract prune`, `noise entries`, `repair`) attaches and sends the
token. K1's wording ("The CLI/proxy never attaches to a pre-existing listener for a root it is about
to use … attaches only behind an explicit flag") is contradicted on the settings path, and F70's own
G3 evidence included the `serve --restart` leg. This is the strongest finding of the join review.

### 3b. Is the private path's channel guessable/reachable? — **CONFIRMED** (residual noted)

- It is an **ephemeral TCP loopback port**, not a pipe or unix socket: live `lsof` on the private
  child showed `TCP 127.0.0.1:62612 (LISTEN)` only. The URL itself travels only over the child's
  redirected stdout pipe (`BackendLauncher.StartPrivateAsync` + `WatchUrlLineAsync`), which another
  local user cannot read.
- Another local process can **find** the listener (loopback ports are machine-global) and connect,
  but `/mcp` still requires the token from the 0600 file — a different local user cannot read it on
  POSIX. Same-user processes can read it anyway (documented threat model).
- Residual TOCTOU: `StartPrivateAsync` returns a printed URL **without checking `backend.HasExited`**
  (`BackendLauncher.cs:76-80`), and `BackendSessions.OpenAsync` then dials it. A child that prints
  and exits leaves a window in which a racer binding the freed ephemeral port receives the token.
  K1 explicitly allowed a `0600` unix socket, which would close this by construction.

### 3c. Does the change break legitimate flows? — **CONFIRMED**, with one **CORRECTED** doc point

- `serve --restart` against a real server: `ServeRestartTests` 10/10 green, including
  `AnExistingServer_IsCycled_AndTheRestartOwnsThePort`.
- Shared-server user: `--attach` works (positive control green; live case E attaches and sends the
  token; E2E suites updated to pass `--attach` where the configured port is the point).
- Auto-start/spawn: private spawn works end-to-end (live case A: real backend response, exit 0).
- `--port` semantics in CLI help: **stale**. Root `--help` says `HTTP backend port the proxy dials or
  starts (1-65535); 0 is serve-only` (`CliCommandTree.cs:30`), but on the default path the proxy
  neither dials nor starts the backend on that port. The reference docs
  (`docs/reference/agent-memory-server.md`) were updated correctly; the CLI help text was not.
  Serve's own help is accurate.

### 3d. Does ADR-0105 state the threat model honestly? — **CORRECTED**

Honest where it goes: it names the different-local-user threat, loopback ports being machine-global,
the self-asserted-name problem, and says the attach path keeps F70's exposure by design. It is
incomplete where it matters:

- It does not disclose that **`serve --restart` still hands the token to a self-identifying
  listener** — it only says "`--restart`'s cycle semantics … unchanged". A reader cannot infer the
  token handover from that.
- It classifies `CliSettingsBackend` as "out of F70's blast radius" while that path hands the real
  token over; that is a scope call the owner's K1 ruling does not make.
- It is silent on the ephemeral-port TOCTOU and does not record that the chosen channel is TCP
  rather than the `0600` unix socket K1 offered.
- Minor: the new ADR file has no trailing newline.

## 4. Is the F70 attack from the G3 report genuinely closed? — **CORRECTED**

The G3 report's primary measured attack (proxy default attach → token + `memory_write` payload +
forged result) is genuinely closed and independently reproduced: with a token file pre-existing and a
squatter on the configured port, the default proxy sent **zero** requests to the squatter and talked
to its own `serve --port 0` child instead. The plain-`serve` attach leg is closed too (exit 3, no
token). But the G3 measurement explicitly included the `serve --restart` handover, and that leg —
plus the settings-command route — still transmits the real token. **F70 cannot be marked fully
closed** on the delivered tree; it is closed for the default launch, open on two explicit-action
routes.

---

## Residual risk

- **R1 (HIGH residual) — `serve --restart` token handover.** A squatter that answers
  `/observability` with name `ai-raccoon` receives the real token on `POST /shutdown`
  (`ServerRestart.cs:70,77,83,134`; measured). Failure scenario: attacker binds the default 7721,
  self-identifies, waits for a user's `serve --restart`; the captured token then authorises
  `/mcp` and `/shutdown` for the whole bank on that data root.
- **R2 (HIGH residual) — CLI settings-command token handover.** Every server-routed CLI verb except
  `encryption`/`doctor`/`serve` attaches via `CliSettingsBackend` with no `--attach` and sends the
  token (`CliSettingsBackend.cs:50,66,70`; measured with `noise entries`). This is a direct
  contradiction of K1's gate wording; the ADR's "out of blast radius" label is not a ruling.
- **R3 (MEDIUM) — ephemeral-port TOCTOU.** `StartPrivateAsync` returns a printed URL without a
  liveness check (`BackendLauncher.cs:76-80`); a child that dies between printing and the proxy's
  first request leaves the port free for a racer to bind and receive the token. Narrow window, but
  the `0600` unix socket K1 permitted removes it.
- **R4 (documented, unchanged) — loopback reachability and same-user trust.** The private backend is
  an ephemeral TCP loopback listener; any local process can find it, and same-user processes can read
  the token file. This matches SECURITY.md's stated model, but SECURITY.md's table still implies the
  token reaches only processes that read the file, which R1/R2 falsify.
- **R5 (LOW) — stale CLI help.** Root `--port` description (`CliCommandTree.cs:30`) still describes
  the pre-ADR-0105 dial/start semantics.
- **R6 (LOW, test quality) — gate specificity.** `BackendSessionsTokenExposureTests` swallows
  `BackendUnavailableException` and never asserts `sessions.Url` is non-empty, so a future regression
  that makes the private spawn fail would leave the gate green for the wrong reason. The positive
  control does not cover the private-spawn path (it attaches to an already-running server). Today the
  spawn was observed running, so the current green is genuine; the assertion should be added.

## Still open

- **Owner ruling needed on R1/R2:** do `serve --restart` and the server-routed CLI commands move to
  private spawn / mutual proof / `--attach`-only, or are their token handovers explicitly accepted
  and recorded in SECURITY.md and the ADR? Until then K1's gate text is not true as delivered.
- **R3:** whether to switch the private channel to the `0600` unix socket K1 offered (closes the
  race) or accept the ephemeral TCP TOCTOU.
- **Register:** F70 should be recorded as "default-launch attack closed; `serve --restart` and
  settings-command token handovers remain" rather than fully closed.
- **Pre-existing, untouched:** the `serve --data-root /Users/arasz/.ai-raccoon --port 7721` server
  (PID 52020) still runs and was never contacted by this review.
