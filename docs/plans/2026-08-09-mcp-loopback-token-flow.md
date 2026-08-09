# Loopback token for `/mcp` — flow

Design for the shared secret that guards the always-on HTTP server introduced by
[ADR-0020](../adr/0020-always-on-http-stdio-proxy.md). Written before implementation.

## Why this exists

There is no authentication on `/mcp` today — `grep RequireAuthorization|AddAuthentication|Bearer`
over `src/` returns nothing. The only control is the loopback bind (`McpServerSetup.cs:95`), and
`SECURITY.md:50-51` names the other half of the mitigation explicitly:

> Keep the HTTP endpoint **opt-in** and loopback-only for the same reason: an unauthenticated
> `localhost` listener is reachable by any local process.

ADR-0020 removes the "opt-in" half: after it, a server is running whenever any agent has touched
memory. That leaves 22 tools — including `memory_delete`, `memory_sweep`, and the settings table
holding cloud-sync credentials — reachable by every local process on the machine: every npm
postinstall, every other agent, anything that can open a socket to 127.0.0.1.

A shared secret closes it. This is a token compared in constant time — not key derivation, not
token signing, not session protection — so it stays inside the no-hand-rolled-crypto invariant.
Generation is `RandomNumberGenerator.GetBytes`; comparison is
`CryptographicOperations.FixedTimeEquals`. Both are platform primitives.

**The zero-config property survives.** The token file is read by the proxy itself, so no MCP client
config changes and nobody types a secret anywhere.

## The flow

```mermaid
sequenceDiagram
    autonumber
    participant C as MCP client<br/>(Claude Code, Hermes)
    participant P as ai-raccoon<br/>(proxy)
    participant F as ~/.ai-raccoon/mcp-token<br/>(0600)
    participant S as ai-raccoon serve<br/>(127.0.0.1:7721)

    C->>P: spawn (stdio)
    P->>S: POST /mcp — probe, no token

    alt nothing listening
        S--xP: connection refused
        P->>S: spawn `ai-raccoon serve`
        Note over S,F: mint BEFORE bind
        S->>F: create 0600 exclusive, or read existing
        S->>S: bind 127.0.0.1:7721
        loop until answered, 30s budget
            P->>S: POST /mcp — probe
        end
    else already listening
        S-->>P: 401 + JSON-RPC error body
    end

    Note over P,F: port answers ⇒ token file exists
    P->>F: read token
    F-->>P: <secret>

    C->>P: initialize / tools/list / tools/call
    P->>S: same message + X-AiRaccoon-Token
    S->>S: FixedTimeEquals
    alt match
        S-->>P: JSON-RPC response
        P-->>C: same response, original id
    else mismatch or absent
        S-->>P: 401 + JSON-RPC error
        P-->>C: JSON-RPC error naming the token file
    end
```

## Mint

```mermaid
flowchart TD
    A[serve starts] --> B{token file exists?}
    B -- yes --> C[read it]
    B -- no --> D[create exclusive, 0600<br/>32 random bytes, base64url]
    D -- lost the create race --> C
    D -- won --> E[hold in memory]
    C --> E
    E --> F[bind 127.0.0.1:port]
    F --> G[serve requests]
```

Three properties this ordering buys, each load-bearing:

- **Mint strictly before bind.** Once the port answers, the token file is on disk. The proxy
  therefore never has to poll for the file or handle "server up, token not yet written" — it reads
  the file only after a successful probe. Getting this backwards reintroduces a startup race that
  is invisible in testing and intermittent in production.
- **Exclusive create, then fall back to read.** Two `serve` processes racing to mint converge on
  one token instead of one overwriting the other's. Note this is a second, independent race from
  the port-bind race that `ServeRunner` already handles (`ServeRunner.cs:47-50,69-81`): a process
  that loses the *bind* race exits before serving, but a process that loses the *create* race must
  still end up with the same secret.
- **Reuse an existing file, never rotate on start.** A server restart keeps the token, so a running
  proxy's cached token stays valid across a backend restart — which ADR-0020 relies on, because its
  reconnect path re-acquires a backend without re-reading anything.

**Rotation is deliberate and manual:** delete the file and restart `serve`. There is no automatic
expiry. An automatic one would buy nothing here — the threat is a local process reading a
world-readable socket, not a leaked long-lived credential — and would cost every proxy a
re-read-and-retry path.

## Validate

Middleware on `/mcp` only. Compare `X-AiRaccoon-Token` against the in-memory secret with
`CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes; absent, wrong length or mismatched
all take the same 401 path.

**`/observability` stays open, deliberately.** It returns a process id and OTLP on/off
(`ServerInfo.cs:4-16`) — no memory content, no settings, nothing that reads or writes the bank. It
is how `serve observability` finds a live server at all (ADR-0008), and gating it would mean the
monitoring CLI needs the data root of a server it is trying to discover. The exposure it adds over
an open port is a PID.

### The 401 must keep the probe working — this is the subtle part

`ServeRunner`'s probe identifies our server by POSTing `/mcp` with a deliberately non-JSON body and
accepting status ∈ {400, 405, 406} **and** `jsonrpc` in the body (`ServeRunner.cs:148-178`). A
naive 401 breaks that: the probe stops recognising a live ai-raccoon server, so every proxy start
tries to spawn a second one, which then loses the bind race and exits 3, and the proxy reports the
backend as unavailable while it is running perfectly.

Two rules keep it working, and both need a test:

1. **401 joins the accepted set** — the discriminator becomes status ∈ {400, 401, 405, 406} plus a
   `jsonrpc` body.
2. **The 401 body is a JSON-RPC error object**, so the "it speaks JSON-RPC" half of the
   discriminator still holds.

Rule 1 is what makes the probe work **across data roots**, which is not hypothetical: the existing
test `BusyPortWithAiRaccoonServer_Attaches_AndFirstKeepsOwnership` (`ServeRunnerTests.cs:101-133`)
deliberately probes with a *different* `--data-root`, so the probing process reads a different token
file — or none — and cannot authenticate. It must still recognise the server. The probe therefore
stays unauthenticated by design: it proves "an ai-raccoon server is here", never "I may use it".

## Failure modes

| Situation | What happens | What the operator sees |
|---|---|---|
| Token file missing when the proxy needs it | Proxy fails before forwarding | One stderr line naming the file path and `ai-raccoon --transport stdio` |
| Proxy's token does not match the server's — a `serve` started against a different data root owns the port | Every forward 401s | JSON-RPC error naming both the file and the port, so the data-root mismatch is diagnosable rather than looking like a dead server |
| Another local user's process calls `/mcp` | 401 | Nothing; that is the feature |
| File exists but is empty or unreadable, proxy side | Treated as absent — fail, never mint a second token | Same as missing |
| File exists but is empty, `serve` side | Given `HealAfter` (5 s) to fill in; still empty means its creator died, so it is deleted and re-minted through the same exclusive create | Nothing; the start that hit it is the one rescued |
| File unreadable or undeletable, `serve` side | `ExitCode.McpTokenUnavailable` before the bind | One stderr line naming the path |
| Backend restarts mid-session | Token unchanged, reconnect succeeds | Nothing |

## Corrections from implementation

Four things this document got wrong or left out. Recorded rather than quietly fixed, because the
doc was written before the code.

**1. Three `ServeRunner` tests had to change, and the doc claimed none would.** The claim was that
`BusyPortWithAiRaccoonServer_Attaches_AndFirstKeepsOwnership` passes untouched. The attach
behaviour *is* untouched — what broke is its line 124, an unauthenticated `GET /mcp` asserting
`405` as proof of ownership. Gating all of `/mcp` means that GET 401s before routing can answer
405. Same assertion in `IdleTimeoutZero_KeepsServing`, and `PortZero_PrintsBoundUrl_AndMcpClient
ReachesIt` drives a real MCP client with no token. The fix was to send the owner's token in all
three, so they still assert `405`/success and additionally exercise the authorized path — not to
relax the assertions to `401`, which would have weakened three checks to accommodate the change.

**2. The token guards `serve`, not "the HTTP server".** A bare `ai-raccoon --transport http`
launch goes through `Program.cs:33` with no token and is ungated. That is deliberate for now: the
path is manual and opt-in — the posture `SECURITY.md` already accepted before ADR-0020 — so it is
not a regression, and gating it would require the E2E `McpServerFactory` to read the token. It is
a real hole for anyone who runs that command, and it is named in `SECURITY.md` rather than left
for a reader to discover.

**3. An empty token file wedged `serve`; it now heals.** If a process dies between the exclusive
create and the write, the file exists and holds nothing, and no later start could get past it.
**Owner ruling, 2026-08-09: self-heal rather than wedge.** The file gets `HealAfter` (5 s) to fill
in; if it is still empty its creator is dead, so it is deleted and minted again through the same
exclusive create. Three things this design turns on:

- **Never write over the file.** Overwriting is worse than the wedge: one healer's token reaches
  the file while another's reaches the serving process, so every caller 401s forever against a
  server that looks healthy — a wedge that no longer announces itself. Routing the heal back
  through the exclusive create reuses the convergence the mint already has.
- **The delete is guarded by an exclusive open, not by `Read()`.** `Read()` reports a mint in
  flight as absent, so a `Read()`-guarded delete removes the token the winner is still writing.
  Caught by `TwoProcessesHealingConcurrently_ConvergeOnOneToken`: 9 distinct secrets across 16
  healers. A residual window remains between the check and the delete, unreachable without the
  cross-process lock this design refuses.
- **5 s, not the 2 minutes first proposed.** `BackendLauncher`'s acquire budget is 30 s, so a
  longer wait heals for nobody — the proxy gives up before it finishes. The window only has to
  outlast a live writer, and a live writer writes microseconds after it creates. Worst case is
  `2 × HealAfter` when the heal itself fails, still inside the budget.

**No lock file.** The exclusive create is already the cross-process primitive and it is tested; a
second one needs a TTL and an owner id to avoid wedging on the lock instead of the token — the
machinery ADR-0008 refused for the PID file.

A path that still cannot be healed — unreadable, undeletable, wrong permissions — fails like every
other `serve` failure: `ExitCode.McpTokenUnavailable` and one stderr line naming the file, not an
unhandled exception with a stack trace.

**4. EventId 640 was wrong.** It straddled `IdleWatchdog`, `ObservabilityRunner` and
`BackendLauncher`, tripping `EventIdBlocks_DoNotInterleaveBetweenOwners`. Moved to 606.

## Known gap: file permissions are POSIX-only

`0600` is enforced with `UnixFileMode` on the create. **On Windows it does not apply** — the file
inherits the data-root directory's ACL, which for a per-user profile directory is normally
owner-only, but that is the directory's property and not something this design asserts. Recorded as
a real limitation rather than papered over: on Windows the token raises the bar from "any local
process" to "any process running as this user", which is weaker than the POSIX guarantee.

Fixing it properly means setting an explicit ACL on the file, which is platform-specific code with
its own test surface. Out of scope here; worth its own decision if Windows becomes a supported
target for always-on serve.

## What this does not do

- **Not authentication of the user** — it proves the caller can read a file in the data root.
  On a single-user machine that is the intended bar.
- **Not transport security.** Traffic stays plaintext on loopback. TLS on 127.0.0.1 would protect
  against nothing that can already read the token file.
- **Not authorization.** Every holder gets the full tool surface; the `ro`/`rw`/`full` access mode
  remains the only privilege split.
- **Not protection against a process running as the same user** that chooses to read the token file.
  That is out of reach for any local shared secret and is why this is a bar-raiser, not a boundary.
