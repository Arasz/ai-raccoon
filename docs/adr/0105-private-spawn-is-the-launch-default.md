# 0105 — The default launch starts its own backend; attaching is opt-in

Date: 2026-09-22 (owner ruling K1) · implementation PSR P1.3

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
  existing exit 3. `serve --restart` is unchanged, and `serve --attach` still attaches and
  exits 0. `BackendSessions`'s attach error text ("a serve on another data root may own
  port N") stays on the attach path only.
- **Shared verdict untouched.** `ServerProbe` and its `Answered` rule are not narrowed.
  The verdict also serves `serve` attach and `WaitForPortToFreeAsync`'s `NotListening`
  requirement; the change belongs at the token-send and attach decisions, where it is.
- **Settings commands.** `CliSettingsBackend` keeps the legacy attach-or-start path
  (ADR-0075 §5.1). It is the CLI's own transport to a backend for one-shot settings
  commands, not the proxy, and it is out of F70's blast radius.

## Consequences

- **Positive.** Port ownership stops being a token-delivery precondition. A squatter that
  merely answers `/mcp` with a JSON-RPC body gets nothing on the default path, because no
  attach happens; the token is sent only to a child this process started and can read the
  URL back from.
- **Default change.** A bare proxy launch no longer reuses a server on `--port`; it leaves
  its own backend on an ephemeral port. Operators who relied on auto-attach to a
  pre-started server must now pass `--attach`, and on the private path `--port` no longer
  names the backend's port.
- **Default change.** A second `serve` against a busy ai-raccoon port no longer attaches
  and exits 0. It refuses with exit 3 unless `--attach` is given. The owning process is
  never touched on either path.
- **Neutral.** `--restart`'s cycle semantics, the tenant-less loopback threat model, and
  `ServerProbe`'s residual false-positive surface are all unchanged. The attach path keeps
  F70's exposure by design: asking for the shared server is asking to trust whoever holds
  the port.
- **Test cost.** E2E suites that need the backend discoverable on the configured port
  pass `--attach`; the private path is gated separately by
  `BackendSessionsTokenExposureTests` and `BackendLauncherTests.StartPrivate_*`.

## Alternatives rejected

- **Mutual proof.** The listener would have to return a value derived from the token file
  before the proxy sends the token. Rejected by the plan and ruled out by the owner as the
  second option: the only candidate proofs need either a handshake that has already
  crossed the token or an endpoint whose name is as self-asserted as `/observability`, and
  the proof would have to be invented rather than adopted.
- **Accept and document.** Writing "loopback port = trusted" into SECURITY.md alongside
  the ADR-0043 sentence the squatter falsifies. Rejected: it leaves the exposure and
  records the opposite of what the measured run showed.
- **Narrowing `ServerProbe` globally.** Rejected by the plan: `serve` attach and
  `WaitForPortToFreeAsync` depend on the current verdict, so a global narrowing would
  change restart and port-free semantics that F70 never touched.
- **A name-based identity check.** Rejected by the plan's review and inherited here:
  `/observability`'s name is self-asserted, and `serverInfo.name` rides after the token.

## Evidence

Gate watched red then green (worktree build; `dotnet test
tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`, filter on
`BackendSessionsTokenExposureTests`). Red was produced by temporarily forcing the
pre-fix acquire in `BackendSessions.AcquireBackend` (the mutation was reverted before the
green run, and no trace of it is in the committed tree): the squatter received
`X-AiRaccoon-Token: UjWUYXj4yyP2fJ4mSn9SPBJ7n4YqShUEf8ttqnW9kzI` plus the
`server/discover` and `initialize` request bodies, exactly F70's measured shape. With the
default private spawn restored, the same test passes with `squatter.Requests` empty.

Positive control: `BackendSessionsTokenExposureTests.OpenAsync_WithAttachAgainstARealServer_OpensASessionThroughTheToken`
passes: with `--attach`, the proxy reaches a real `serve` on the configured port, opens
a session through the token, and lists tools. Both tests are green together in the same
run.

Plumbing: `CliArgsTests` carries the root-flag and serve-flag spellings, the
serve-wins precedence, and the no-flag default. The first attempt failed on root
`--attach` before `serve` because `GetResult(ServeAttachOption)` returned an implicit
false result and the null-check presence test preferred it over the explicit root flag;
`Parse_RootAttachBeforeVerb_ReachesTheServeOptions` is the regression pin. E2E:
`ProxyLaunchE2ETests` and `ProxySpawnedBackendE2ETests` (with `--attach` where the
configured port is the point), `NodeRunnerTests.BusyPortWithAiRaccoonServer_WithoutAttach_ReturnsPortInUse_AndLeavesTheOwnerServing`
for the serve refusal, and `BackendLauncherTests.StartPrivate_*` for the private
mechanics. All green in the P1.3 run.

Post-change grep: `--attach` appears in the launch and serve help text and the docs that
describe them; the probe and restart code paths carry no attach default change.