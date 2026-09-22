# Research record — backend launch: attach-or-start + private-key proof of identity

Date: 2026-09-22 (late) · task `air-backend-launch-identity-proof` · compiled by the orchestrator
before planning. Every finding carries its source; anything unmarked is labelled HYPOTHESIS.

## Ruling under implementation (source: owner, message bus, 2026-09-22, verbatim)

> revert the changes to the backend launch, cli (proxy) must launch instance only if no instance
> is running - if there is any squater - we need other solution - we are not limited to MCP
> protocol - we can use aproach based on proof, we can generate a private key for ai-raccoon and
> use it to prove identity (only ai-raccoon will have this key)

Reading of the clauses (each must map to a plan package):
1. **Revert** ADR-0105's K1 launch default: the CLI/proxy attaches to a running instance and
   launches one **only if none is running** (attach-or-start, the pre-F70 default).
2. **Squatter problem** is solved by a **proof of identity**, not by never attaching: ai-raccoon
   generates a **private key**; a listener must prove possession ("only ai-raccoon will have this
   key") before it is trusted with anything.
3. **Protocol freedom**: the proof is NOT constrained to the MCP surface — a custom channel is
   explicitly allowed.

## Established facts (measured / read, with sources)

- **F70 threat and mechanism** (docs/adr/0105-private-spawn-is-the-launch-default.md "Context" +
  "Evidence"; /tmp/air-review/REVIEW-ASSEMBLED.md ### F70): a squatter binding the configured
  loopback port and answering `POST /mcp` with a body containing `jsonrpc` received
  `X-AiRaccoon-Token: <minted value>` byte-for-byte plus full `memory_write` payloads. Two
  defaults composed: `ServerProbe` calls a port "an ai-raccoon server" when the response body
  contains `jsonrpc` (`ServerProbe.cs:69`), and `BackendSessions` reads `<data-root>/mcp-token`
  and presents it on every backend request. Loopback ports are machine-global, so the squatter
  can be a different local user.
- **ADR-0105 previously REJECTED mutual proof** (docs/adr/0105..., "Alternatives rejected"):
  because the candidate proofs then considered needed a handshake that had already crossed the
  token (`serverInfo.name`) or a self-asserted name (`/observability`). The owner's ruling changes
  the premise: a **cryptographic** proof over a custom channel is now in scope, which the
  rejection did not cover. The ADR must be revised (K1 → new ruling) — do not treat its
  "mutual proof rejected" as standing policy.
- **Token mechanics** (grep, this session): `McpTokenFile` (src/AiRaccoon/Hosting/Common/
  McpTokenFile.cs:13-38, file `mcp-token` in the data root, with heal-after logic); enforced by
  `McpTokenGate` middleware (src/AiRaccoon/Setup/McpServerSetup.cs:83); presented by
  `BackendSessions` (src/AiRaccoon/Hosting/Proxy/BackendSessions.cs:45) and `CliSettingsBackend`
  (src/AiRaccoon/Settings/CliSettingsBackend.cs:72).
- **Revert surface — everything #643 (squash d09d4ead) touched** (`git show d09d4ead --stat`):
  src: Hosting/Common/ServerConfig.cs, Hosting/Node/{IServerRestart,NodeRunner,RestartOutcome,
  ServerRestart}.cs, Hosting/Proxy/{BackendLauncher,BackendSessions,IBackendLauncher}.cs,
  Settings/CliSettingsBackend.cs, Setup/Cli/{CliArgs,CliCommandTree,CliOptionsExtensions,
  RootCliOptions}.cs, Setup/Cli/Commands/ServeCommands.cs;
  tests: E2E/{ProxyLaunchE2ETests,ServeRestartE2ETests}.cs, TestHelpers/{FakeRaccoon,Squatter}.cs,
  Unit/Setup/CliArgsTests.cs; docs: adr/README.md, how-to/configure-ai-raccoon-server.md,
  reference/{agent-memory-server,logging-event-ids}.md.
- **Current behavior on main (1.43.0 / d2532be7)** per ADR-0105 "Decision"/"Consequences":
  private spawn default (`--attach` opt-in); plain `serve` on a held ai-raccoon port refuses
  (exit 3) naming `--attach`/`--port 0`; `serve --restart` bare refuses (AttachRequired) because
  cycling sends the token; **settings verbs are already attach-or-start** (evening ruling K1a) and
  disclose the backend outliving the command (N1, EventId 687); the proxy stops its private
  backend on shutdown (688-689); `CliArgs` bool-flag arity fixed (separate bugfix — keep).
- **Concurrency safety of multi-process banks** (docs/adr/0105..., "Concurrent access", merged
  d2532be7; SqliteConnectionFactory.cs:295,343-347): WAL + `busy_timeout=5000`, defined
  `bank-busy` refusal, race-proof migrations. Relevant because restoring attach-or-start removes
  most dual-writer states the private-spawn default created.
- **Existing test scaffolding for the squatter threat**: `Squatter.cs` (test helper, +124 lines in
  d09d4ead), `FakeRaccoon.cs` (self-identifying fake server), `BackendSessionsTokenExposureTests`,
  `CliSettingsTokenExposureTests`, `ServeRestartTests` — the proof gates can build on all of these.
- **Historical conventions that bind the design**: secrets live in the data root (mcp-token
  precedent); refusals name their remedy (F53); exit codes are documented (ExitCode.cs: 26 corrupt
  bank, 130 interrupt, 3 PortInUse); `[LoggerMessage]` ids need registry re-measure
  (LoggerMessageEventIdTests); SqliteMemoryStore.cs is under a 1066-line ratchet.

## Known unknowns — each needs a RECOMMENDATION in the plan, not a question

1. **Key shape**: Ed25519/asymmetric keypair (owner says "private key") vs symmetric secret +
   HMAC. Trade: asymmetric lets the prover prove possession without the verifier holding the
   secret (but both live in the same data root today); symmetric is smaller surface.
2. **Key scope**: per data root (like mcp-token) vs per installation/user. Threat model is a
   different local user squatting the port; same-user processes can read any data-root secret
   regardless.
3. **Proof wire**: endpoint path + method, nonce MUST be verifier-generated (replay defense),
   response format (signature over nonce + what context binding: data root id? port? version?),
   replay window, error shapes. Custom HTTP endpoint on the backend's existing loopback listener
   is the obvious carrier (allowed by ruling clause 3).
4. **Failure mode when proof fails** (a squatter holds the port): refuse with a named remedy, or
   fall back to a private ephemeral backend (F70 machinery kept as FALLBACK, not default), or
   both (fallback + loud warning). Note the configured port is occupied in this state.
5. **What of #643's non-default changes survives**: `--attach` flag (keep/remove — attach-or-start
   makes it moot?), plain-`serve` exit-3 refusal (bind path, arguably keep), `serve --restart`
   bare (restore now that proof gates the token?), proxy-stops-private-backend (semantics change
   if spawn becomes fallback-only), F38 disclosure (N1 ruling — keep), CliArgs arity fix (keep).
6. **Does the proof gate only the token handover, or replace the bearer token?** (HYPOTHESIS worth
   evaluating: derive the channel token from the key and delete the token file — smaller secret
   count; larger blast radius. Minimal scope is: proof gates the handover, token unchanged.)

## Fold-in: p34 (handed over by the Claude primary, 2026-09-22, inside this task's fence)

Both are MEASURED findings (/tmp/air-review/REVIEW-ASSEMBLED.md ### F49, ### F39) and both bind
this task's design directly:

- **F49 — secret files live at the data-root top level while the bank lives in
  `<data-root>/.ai-raccoon/`.** `McpTokenFile.cs:41-44` puts `mcp-token` at `Path.Combine(dataRoot,
  "mcp-token")` while project scope puts bank+log under `<dataRoot>/.ai-raccoon`
  (`SqliteConnectionFactory.cs:167-176`). Measured: a `--data-root` pointed at a repository leaves
  an UNIGNORED 0600 secret in the working tree (one `git add -A` from a commit) and backups of
  `.ai-raccoon/` silently omit the credential. **The new identity key must not inherit this** —
  key and token belong in the bank state directory. This is a hard constraint on known-unknown #2
  (key scope/location), not a separate opinion.
- **F39 — a mistyped `--data-root` on any settings verb silently mints a full bank there.**
  Measured: `settings access show` against an empty dir → exit 0 and `['mcp-token', 'memory.db',
  'memory.db-shm', 'memory.db-wal']` appear at the typo'd path. `ExitCode.NoBank` exists for
  exactly this (`ExitCode.cs:64-66`) but is enforced doctor-only (`CliWriteOptOuts.cs:16` exempts
  only `encryption`); the auto-started server's first open runs `MemorySchema.EnsureAsync` and
  mints everything. This task REWORKS the attach-or-start auto-launch path — the NoBank guard
  belongs there: "launch instance only if no instance is running" must not read as "and mint a new
  bank wherever --data-root points".

So the ruling clause table covers FOUR owner clauses (revert to attach-or-start; proof of
identity via private key; protocol freedom; + the p34 pair as correctness clauses) and every
package AC must map to one of them.

## Constraints (owner standing rules — non-negotiable)

One PR per package ok / one PR per task package series; push after every commit; never push to
main; never force-push; no git stash; TDD with watched-red gates + positive controls;
`dotnet test --project tests/AiRaccoon.Tests --filter-class '*X'`, never `--nologo`; never touch
`~/.ai-raccoon` or port 7721; never edit VERSION on main; no self-spawned reviewers; squash-merge
with origin/main merged in first. Security invariant: **the token (and any key material) must
never reach a listener that has not proven identity** — the F70 gates must go green in their
proof-gated form (zero requests to a squatter, and zero secret bytes).
