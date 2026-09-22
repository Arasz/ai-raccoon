# 0105 — The default launch starts its own backend; attaching is opt-in

Date: 2026-09-22 (owner ruling K1) · implementation PSR P1.3; join-review follow-up same day

Status: Accepted

## Context

F70 measured the proxy handing a pre-existing listener everything it guarded: with a
token file present on a scratch data root, a python squatter that bound the configured
port and answered `POST /mcp` with a body containing the literal `jsonrpc` received the
loopback token byte-for-byte, the full `memory_write` payload, and the forged tool result
was relayed to the agent as the backend's answer. The mechanism was two defaults in a
row. `ServerProbe` calls a port "an ai-raccoon server" when the response body contains
`jsonrpc`, nothing more (`ServerProbe.cs:69`), and `BackendLauncher.AcquireAsync`
short-circuits on that verdict without spawning anything. `BackendSessions` then reads
`<data-root>/mcp-token` and presents it on every backend request. On a shared machine the
squatter can be a different local user, because loopback ports are machine-global.

The review plan rejected the name-based fix the finding suggested. `server/discover` is
absent from the shipped ModelContextProtocol 2.2.0, reading `serverInfo.name` needs a
completed handshake (the token has already been sent by then), and the only pre-token
identity, the `/observability` name, is self-asserted and itself squat-able. The plan
offered private spawn, mutual proof, and accept-and-document; the owner ruled private
spawn (K1, 2026-09-22).

An independent join review of the first implementation (2026-09-22) then falsified the
completeness claim: two routes still handed the token to a listener that merely held the
configured port. `serve --restart` identified the listener by its self-asserted
`/observability` name, read the token file and sent the token on `POST /shutdown`
(`ServerRestart.cs`); the server-routed CLI commands acquired their backend through
`CliSettingsBackend.AcquireAsync`, the legacy attach-or-start acquire, and sent the token
to whatever answered on `--port`. Both were measured live, with a squatter holding the
port. This ADR records the follow-up that closed them.

## Decision

A launch that is about to use a data root never attaches to a pre-existing listener by
default. It starts its own backend on an ephemeral port and trusts only the URL that
child prints on its stdout pipe. `--attach` is the explicit opt-in to the shared server.

- **Proxy.** `BackendSessions` starts `ai-raccoon serve --port 0` through
  `BackendLauncher.StartPrivateAsync` unless `ServerConfig.Attach` is set. That path never
  probes or dials the configured port (`BackendSessions.cs`, `BackendLauncher.cs`). The
  acquire result's URL comes from the child's stdout alone, which is what makes the
  default attach-proof: no probe verdict can point the proxy at a foreign listener.
- **Attach.** `--attach` on the launch root (the proxy) or after the `serve` verb. Serve's
  own flag wins; the root flag is the fallback. `ResolveAttach` reads presence from
  `OptionResult.Implicit == false`, because System.CommandLine materialises an implicit
  result for an absent bool option and a null check would let serve's default shadow an
  explicit root flag.
- **Serve.** Without `--attach`, an ai-raccoon server already on the port is refused:
  exit 3 (`PortInUse`) with a line saying the port is held by an ai-raccoon server and
  naming both escapes, `--attach` and `--port 0`. A foreign listener keeps its
  existing exit 3. `serve --attach` still attaches and exits 0. `BackendSessions`'s attach
  error text ("a serve on another data root may own port N") stays on the attach path only.
- **Restart.** `serve --restart` cycling sends the listener the data root's token, so it
  is an attach-shaped trust decision and now needs the same opt-in. `IServerRestart.
  CycleAsync` takes `attaching`; once `/observability` identifies the listener and before
  the token file is read, an `!attaching` run returns `RestartOutcome.AttachRequired`
  (`ServerRestart.cs`). `NodeRunner` turns that into exit 3 with a line naming `--attach`,
  the token file it would send, and the manual escape
  (`ai-raccoon serve observability pid --port N`, then serve again). Nothing is read and
  nothing is sent without the flag. A listener that does not identify is still refused by
  its own "does not identify" line, and a free port still just binds. `--restart --attach`
  cycles exactly as before; the rewrite `dotnet tool update && serve --restart` must add
  the flag once, or stop the old server first. The alternative — a fresh mutual-proof
  endpoint — was not taken: `/observability` is self-asserted, and a challenge-response
  would add an unauthenticated cryptographic oracle and a new wire contract to preserve a
  convenience whose trust meaning `--attach` already expresses.
- **Settings commands.** `CliSettingsBackend` acquires like the proxy: private spawn by
  default, `--attach` for the legacy attach-or-start path. The port is validated only on
  the attach path, because the private path pins `--port 0` for the child. The earlier
  claim that this path was "out of F70's blast radius" is withdrawn — the join review
  measured the token reaching a squatter through it, which is falsification, not a scope
  call. The CLI's command-level routing (which verbs go through the server at all) is
  unchanged.
- **Private channel and TOCTOU.** The private channel stays an ephemeral TCP loopback
  port. K1 permitted a `0600` unix socket; the MCP client's HTTP transport addresses a
  URL, so a unix-socket endpoint would need a custom connect path and a non-URL channel
  across the proxy, the settings backend and every test that dials the child — a transport
  redesign, recorded as the remaining hardening rather than smuggled in here.
  `StartPrivateAsync` returns a URL only if the child that printed it is still alive
  (`backend.HasExited` after the URL line is observed): a child that printed and exited
  (a crash after bind) is reported as a failure with its exit code and stderr, and its
  freed ephemeral port is never dialled with the token. The residual window — the child
  dies between that check and the caller's first request — remains, and the unix socket
  would remove it by construction.
- **Shared verdict untouched.** `ServerProbe` and its `Answered` rule are not narrowed.
  The verdict also serves `serve` attach and `WaitForPortToFreeAsync`'s `NotListening`
  requirement; the change belongs at the token-send and attach decisions, where it is.
- **Parser flag arity.** `CliArgs.ContainsVerb` no longer treats a bool root flag as
  consuming the next token. `--attach settings …` previously hid the verb behind the flag,
  so a malformed subcommand fell back to the launch-root parse and exited 9 with
  "Unrecognized command or argument 'settings'" instead of the verb's own 15 and help.

## Consequences

- **Positive.** Port ownership stops being a token-delivery precondition. A squatter that
  merely answers `/mcp` with a JSON-RPC body, or `/observability` with the ai-raccoon
  name, gets nothing on the default paths, because no attach happens; the token is sent
  only to a child this process started and can read the URL back from, or to a listener
  the operator explicitly opted to trust.
- **Default change.** A bare proxy launch no longer reuses a server on `--port`; it leaves
  its own backend on an ephemeral port. Operators who relied on auto-attach to a
  pre-started server must now pass `--attach`, and on the private path `--port` no longer
  names the backend's port.
- **Default change.** A second `serve` against a busy ai-raccoon port no longer attaches
  and exits 0. It refuses with exit 3 unless `--attach` is given. The owning process is
  never touched on either path.
- **Default change.** A settings verb (`settings …`, `model …`, `watch registered`,
  `noise entries`, `repair`, …) against a busy ai-raccoon port no longer reuses that
  server; it starts its own private backend (which the idle watchdog later stops).
  `--attach` keeps the shared-server shape; the suites whose whole point is that shape
  (`CliContractTests`, `CliBankWriteTests`) now pass it explicitly.
- **Restart UX change.** A user who ran `serve --restart` bare against a live server gets
  exit 3 and a line naming `--attach` and the manual stop, instead of a silent token
  handover. `serve --restart --attach` is the documented cycle path from here on.
- **Error text.** The two measured squatter routes now fail closed with an operator line
  naming the flag; the attach path keeps F70's exposure by design: asking for the shared
  server is asking to trust whoever holds the port.
- **Test cost.** E2E suites that need the backend discoverable on the configured port
  pass `--attach`; the private paths are gated separately by
  `BackendSessionsTokenExposureTests`, `CliSettingsTokenExposureTests` and
  `BackendLauncherTests.StartPrivate_*`.

## Alternatives rejected

- **Mutual proof.** The listener would have to return a value derived from the token file
  before the proxy sends the token. Rejected by the plan and ruled out by the owner as the
  second option: the only candidate proofs need either a handshake that has already
  crossed the token or an endpoint whose name is as self-asserted as `/observability`, and
  the proof would have to be invented rather than adopted. Re-proposed for `--restart` in
  the join review; declined there for the same reason, plus the new unauthenticated
  challenge endpoint it would add.
- **Accept and document.** Writing "loopback port = trusted" into SECURITY.md alongside
  the ADR-0043 sentence the squatter falsifies. Rejected: it leaves the exposure and
  records the opposite of what the measured run showed.
- **Narrowing `ServerProbe` globally.** Rejected by the plan: `serve` attach and
  `WaitForPortToFreeAsync` depend on the current verdict, so a global narrowing would
  change restart and port-free semantics that F70 never touched.
- **A name-based identity check.** Rejected by the plan's review and inherited here:
  `/observability`'s name is self-asserted, and `serverInfo.name` rides after the token.
- **Leaving the settings path as the legacy acquire.** Rejected after the join review
  measured the token arriving at a squatter: the proxy's rule is the launch rule, and the
  CLI's own transport is a launch.

## Evidence

The first pass's gate was watched red then green under `BackendSessionsTokenExposureTests`
(worktree build; `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`, filter on the
class). Red was produced by temporarily forcing the pre-fix acquire in
`BackendSessions.AcquireBackend` (the mutation was reverted; no trace in the tree): the
squatter received `X-AiRaccoon-Token: UjWUYXj4yyP2fJ4mSn9SPBJ7n4YqShUEf8ttqnW9kzI` plus
the `server/discover` and `initialize` request bodies, F70's measured shape. With the
default private spawn restored, the same test passes with `squatter.Requests` empty, and
now also asserts `sessions.Url` is non-empty so a failed spawn cannot pass the gate for
the wrong reason.

Join-review gates, each watched red then green on the same tree:

- `CliSettingsTokenExposureTests.AcquireAsync_WithoutAttach_WithASquatterHoldingTheConfiguredPort_DoesNotSendItTheToken`
  — red: the squatter received `POST /mcp` plus
  `GET /settings?key=sweep.threshold` carrying `X-AiRaccoon-Token: <minted value>`;
  green: zero requests, `PrivateUrl` on an ephemeral port. The positive control,
  `AcquireAsync_WithAttachAgainstARealServer_ReadsThroughTheToken`, writes and reads back
  a setting through the shared server's token.
- `ServeRestartTests.RestartWithoutAttach_AgainstAnAiRaccoonListener_RefusesAndSendsNoToken`
  — red: the self-identifying `FakeRaccoon` received `POST /shutdown` with the token and
  the run exited 14 after waiting for a stop that never came; green: zero shutdown
  requests, zero token headers, exit 3, stderr names `--attach` and the manual stop.
  `ServeRestartTests` stays 11/11, including the real-server cycle now invoked as
  `--restart --attach` and the foreign-listener refusal that still identifies first.
- `BackendLauncherTests.StartPrivate_WhenTheChildPrintsAUrlThenExits_DoesNotReturnTheUrl`
  — red: the URL printed by a child that had already exited came back as `result.Url`;
  green: `result.Url` is null and the child's exit code 7 is reported.
- `CliSettingsBackendTests` — red: the private path reported "cannot dial --port 0" and
  the failure text named the port; green: 10/10 with `PrivateCalls`/`AcquireCalls`
  distinguishing the two paths.
- `CliArgsTests.Parse_RootAttachBeforeABrokenVerb_KeepsTheVerbPath` — red: command path
  empty and "Unrecognized command or argument 'settings'" after `--attach`;
  green: `["settings", "sweep"]` with the verb's own `'bogus'` error.

Consumers re-run green on the touched surface: `CliArgsTests` 99/99, `CliContractTests`
3/3, `CliBankWriteTests.ReadCommand_CommitsNothingToTheBank` + `AWriteCommand_IsSeen`
31/31, plus `NodeRunnerTests`, `BackendLauncherTests`, `BackendSessionsTests`,
`QuietLoggingTests`, `AppRunnerSettingsRoutingTests` and the attach-parsing tests (77/77).

Post-change grep: `--attach` appears in the launch and serve help text and the docs that
describe them; the probe and restart code paths carry no attach default change.
