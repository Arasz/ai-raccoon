# 0106 — Attach-or-start again, proven by a per-root identity key

Date: 2026-09-23 (owner ruling, 2026-09-22 late: "revert the changes to the backend launch ... if
there is any squatter - we need other solution - we are not limited to MCP protocol - we can use a
proof based approach, we can generate a private key for ai-raccoon and use it to prove identity")

Status: Accepted

## Context

ADR-0105 answered F70 — a squatter holding the configured loopback port and echoing `jsonrpc` in a
`POST /mcp` body received the data root's token byte for byte, plus every tool payload — by making
the default launch never attach. A launch started its own backend on an ephemeral port and trusted
only the URL that child printed; `--attach` became the explicit opt-in for reusing a shared server.

The owner's ruling reverses the launch default while keeping F70 closed a different way. The CLI and
proxy attach to a running instance and start their own only when none is running, and the squatter
problem is solved by proof, not by refusal: ai-raccoon mints a private key only it holds, and a
listener must demonstrate possession before anything secret reaches it. The ruling puts a
purpose-built, non-MCP channel in scope, which ADR-0105's "Alternatives rejected" never had available.

Two measured defects bind this design and are fixed with it. **F49**: `McpTokenFile` put `mcp-token`
at the data root's top level while a project-scope bank lived under `<dataRoot>/.ai-raccoon`, so a
`--data-root` pointed at a repository left an unignored 0600 secret one `git add -A` from a commit,
and a backup of `.ai-raccoon/` left the credential behind. **F39**: a mistyped `--data-root` against
an empty directory minted a full bank there on the first auto-launched settings command.

## Decision

A launch that is about to use a data root probes the configured port. A **proven** ai-raccoon
listener is attached to — nothing new is spawned. Nothing listening → one is started on the
configured port. A listener that cannot prove it holds this root's identity key is untrusted: a
client-side launch (the proxy, a settings command) falls back to a private backend on an ephemeral
port, with the N1 disclosure naming the remedy (stop the listener); `serve` on an unproven port holder
refuses with exit 3 after attempting the proof. `--attach` is removed entirely — root and post-verb
spellings — and passing it is an unrecognized argument, exit 9.

Every token-bearing request proves the listener immediately before it, not once per process: the
initial acquire, `serve --restart`'s shutdown request, and the proxy's dispose-time stop of the
private backends it started. A listener that cannot prove at any of those moments is sent nothing and
reported as not stopped. A private fallback child that fails its own proof at acquire is sent nothing either;
the launch that spawned it holds its process and stops it at once, rather than leaving it to its idle
timeout. A private child that never reports its URL within the startup budget, or whose
start the caller cancels, is stopped the same way before the failure is returned.

Once a proxy has fallen back, the fallback is sticky for that proxy's lifetime. A reopen (a client
naming another protocol revision, or a lost session) proves the private child again and reuses it,
so one proxy never loads the model twice. Only a child that fails that proof sends the reopen back
through the full attach-or-start. The shared server, once it proves again, is picked up on the next
proxy launch.

### D1 — Trust anchor: a per-root ECDSA P-256 key

`identity-key` (PKCS#8 PEM, 0600) lives beside `mcp-token` in the bank state directory
(`src/AiRaccoon/Hosting/Common/IdentityKeyFile.cs`). net10.0's BCL has no Ed25519 (the reference pack
carries only the post-quantum composite `MLDsa*WithEd25519` names), so the key is ECDSA over
`ECCurve.NamedCurves.nistP256`, in the box, no new package. `keyId` =
`base64url(SHA-256(SubjectPublicKeyInfo DER))`.

- **Only `serve` mints or heals** (`IdentityKeyFile.EnsureAsync`). Verifiers call the read-only
  `IdentityKeyFile.Read`, which creates nothing: a root with no key is not provable, never a place to
  mint one. The signer is loaded once and cached.
- **Mint/heal is serialized across processes**: an in-process `SemaphoreSlim` plus an OS lock file
  (`FileShare.None`, `OwnerOnlyFile.AcquireLockAsync`). An unparseable file is deleted only once it is
  older than the heal window (`IdentityKeyFile.HealAfter`, 5 s), since younger debris may be a
  concurrent writer mid-mint.
- **Fail closed on the trust anchor**: the state directory is created 0700. An existing key or
  token file that is group/world accessible is refused rather than adopted (`EnsureFileIsPrivate`).
  An existing state directory is judged by what the extra bits allow (`OwnerOnlyFile.EnsureDirectory`,
  called by `serve`'s token and key mint):
  - **tightened** when the current user owns it and its only leak is group/world read or execute —
    the umask's 0755 every earlier binary left. `serve` sets it to 0700 and logs that once (EventId
    692), so an upgraded install and the D3 legacy-token migration start without a manual step;
  - **refused** when a group or other principal can write it (it may already hold a planted file),
    or when another user owns it (the tightening `chmod` fails). The refusal names its remedy.

  A verifier's read-only check (`IdentityKeyFile.Read`, `McpTokenFile.Read`) changes nothing: it
  still treats a shared directory as not provable until a `serve` on that root has tightened it.
- **Rotation** is manual: stop the server, replace `identity-key`, start again. "Delete the key, then
  `serve --restart`" was rejected — the restart cannot prove once the key is gone. A rotate verb is
  future work.

### D2 — Wire protocol v1

```
POST /identity/prove            Content-Type: application/json
request   { "v":1, "nonce":"<32-byte CSPRNG, base64url>", "rootFp":"<base64url sha256>",
            "keyId":"<base64url sha256(SPKI)>" }
response  200 { "v":1, "keyId":"…", "signature":"<base64url IEEE-P1363 fixed 64-byte r||s>" }
          400 { "error":"malformed" | "root-mismatch" | "no-key" }        any other status = NotProven
```

- The listener resolves its own state directory and its own bound port. If its `rootFp` differs from
  the challenge's, it answers 400 `root-mismatch`, signs nothing and echoes nothing. `rootFp` is never
  returned on any path.
- The signed transcript is
  `"ai-raccoon/identity/v1\n" + nonce + "\n" + keyId + "\n" + rootFp + "\n" + port`, SHA-256,
  `DSASignatureFormat.IeeeP1363FixedFieldConcatenation` pinned at both sign and verify
  (`src/AiRaccoon/Hosting/Common/IdentityProof.cs`).
- The verifier rebuilds the transcript with **the port it actually dialled**, so a signature relayed
  from a same-root backend on another port fails. The nonce is fresh per attempt, retries included, so
  a captured response cannot be replayed.
- A nonce-only transcript was rejected: it proves possession *by proxy* — a squatter could relay the
  challenge to any live same-root backend (the victim's `serve`, or a fallback child discoverable via
  `/observability`) and hand back its signature. Binding port and `rootFp` closes that relay.
- The endpoint is mapped on the existing loopback listener and allowlisted in `McpTokenGate.OpenPaths`
  (`src/AiRaccoon/Hosting/Node/McpTokenGate.cs`); the gate stays default-closed, so a forgotten entry
  costs a 401, never a bypass. `/mcp`, `/settings` and `/shutdown` stay token-gated.
- Bounded: challenge and response bodies are capped at 8 KB and read incrementally; at most 4 requests
  in flight, a burst waits 1 s then gets 503 `{"error":"busy"}`
  (`src/AiRaccoon/Setup/Identity/IdentityProofEndpoint.cs`). The client
  (`src/AiRaccoon/Hosting/Proxy/IdentityProver.cs`) budgets 2 s per attempt and treats anything short
  of a verified 200 — transport failure, timeout, malformed or oversized body — as not proven.

### D3 — Token: kept, relocated, migrated

The proof authenticates the server to the client; the token still authenticates the client to the
server. Deriving one from the other would collapse the two directions into one credential, so the
token stays. It moves from the data-root top level into the bank state directory
(`src/AiRaccoon/Hosting/Common/McpTokenFile.cs`, scope-aware constructor). A legacy top-level token is
migrated once: read only when owned by the current user and not group/world readable or writable;
written into the state directory through the same exclusive-create path a mint uses; the legacy file
deleted only after that write succeeds. The state-directory file wins when both exist; the legacy path
is never minted into; a planted legacy file fails closed. User-scope paths are unchanged.

### D4 — F39 no-mint guard

The guard covers the client auto-launch paths only — the proxy's backend acquire
(`BackendSessions.AcquireBackend`) and the settings verbs' acquire (`CliSettingsBackend.AcquireAsync`).
When no bank file exists at the resolved path, the acquire refuses before starting anything and exits
`ExitCode.NoBank` (22), the code `doctor` already uses for a missing bank. The exemption is by **path
identity**, not by which flag was typed: a resolved data root equal to the default root keeps today's
bootstrap. `serve`, `serve --restart`, `encryption` and `doctor` are exempt unconditionally.
Unparseable arguments (including a stray `--attach`) exit 9 (`FailedToParseCliArgs`);
`InvalidArgument` (15) stays reserved for semantic validation. The split is by parse-error kind:
an error on a command (unrecognized token, missing subcommand) is 9; an error on an option or
argument value, or a value this CLI rejects after parsing, is 15 — on every path (ADR-0060 amendment).

### D7 — Secret layout (F49)

Identity key, token, bank and log live under one directory — the bank state directory resolved by
`src/AiRaccoon.Infrastructure/Sqlite/BankPaths.cs`: the data root for a user-scope install,
`<dataRoot>/.ai-raccoon` for a project-scope one. A state-directory backup carries the credential with
the bank by design, and nothing 0600 sits at a project's data-root top level.

## The invariant, stated honestly

To any listener that has not proven identity: **zero secret bytes; requests are limited to the
existing `POST /mcp` probe and the bounded nonce challenge.** "Zero requests" was never true — the
probe (`ServerProbe.cs`) already POSTs a bare `"x"` body, without a token, to whatever holds the port.
The proof **shrinks** the acquire-time TOCTOU window between "the listener proved" and "the token was
sent"; it does not close it, because the token rides later connections. Channel binding or a
unix-socket transport would close it and stay future work.

## Answering ADR-0105's "unauthenticated cryptographic oracle" objection

ADR-0105 rejected a challenge-response proof because it "would add an unauthenticated cryptographic
oracle and a new wire contract". That was right for the candidates it had: a handshake that had
already crossed the token, or a signature over `/observability`'s self-asserted name. This design
answers the objection instead of dropping it:

- **Bounded transcript** — the listener signs one fixed shape; of its five fields only the nonce comes
  from the caller, as opaque CSPRNG bytes. `keyId`, `rootFp` and port are the listener's own, and a
  `rootFp` mismatch is refused before any signing.
- **Fresh nonce** — chosen by the verifier per attempt, so no response is reusable.
- **Domain separation** — the `ai-raccoon/identity/v1` label keeps a signature meaningless to any
  other protocol sharing the key.
- **Rate bound** — 4 in flight, 503 beyond; it bounds harvesting speed but does not prevent it
  (residual 3).

## Upgrade note: mixed-version operation is not supported

A state directory an earlier binary created is 0755 (the umask). The first current `serve` on that
root tightens it to 0700 and logs the change once (D1); it does not refuse it. A directory others
can write, or one another user owns, is still refused with the remedy.

The first current binary to open a root migrates a legacy top-level token (D3). An older binary still
expects the top-level path and re-mints a token there on its next run, leaving two live tokens. Stop
the old server manually before running the new binary against the same root.

## Residuals

1. **Same-uid attackers and root are out of scope** — they read the key file directly.
2. **Post-proof TOCTOU** — the proof binds one connection; the token rides later ones. Channel binding
   / unix sockets remain future work.
3. **DoS** — squatting the port forces every client onto the fallback; a listener that accepts and
   never answers costs the same; fallback sprawl costs a model load each; the endpoint is rate-bounded
   but unauthenticated by construction.
4. **The bearer token, once handed over, is still full access** — environment propagation, `/proc`,
   crash dumps, transcripts.
5. **Copied or restored state directory** — cross-root confusion is refused via `rootFp`; restoring to
   the original root is supported by design.
6. **Legacy-token migration window** — the planted-file check defends it; mixed-version operation is
   not supported (see the upgrade note).
7. **Permissions are POSIX 0600/0700 only** — a Windows install inherits the data root's ACL; the key
   is plaintext even with an encrypted bank; state-dir backups carry the trust anchor.
8. **The F39 default-root carve-out is a deliberate product decision** (path identity).
9. **Metadata** — `/observability` still reveals pid and version; `rootFp` is never echoed.
10. **A missing or corrupt key file means "cannot attach"**, only "spawn private" — never "mint here".
11. **The invariant's precise form** is the one above — not "zero requests".
12. **The overruled precedent** — ADR-0105's oracle objection is answered above, not dropped.
13. **The dispose-time prove-then-stop needs a client that closes stdin.** The MCP SDK's stdio client
    (`StdioClientSessionTransport`, SDK 2.2.0) waits out its `ShutdownTimeout` for the proxy to exit
    before it closes stdin, then kills the process tree. Under that client the proxy's dispose path
    never runs: its private children die in the tree kill instead of being proven and stopped over
    `/shutdown`. No secret is sent to anyone on that path; it only means the proven stop is skipped.

## Alternatives rejected

- **Deriving the token from the identity key** — collapses the two trust directions (D3).
- **Signing only the nonce** — possession by proxy (D2).
- **A dedicated rotate verb** — deferred; manual stop/replace/start covers rotation today.

## Evidence

P1 (state directory, token relocation, identity key) and P2 (proof endpoint, client verifier) are
gated red-then-green; `docs/work/implementation/p2-report.md` records the proof-channel shapes and the
mutation probes (P1363↔DER, nonce reuse, port dropped from the transcript, `rootFp` check removed —
each turned its gate red). The F39 guard, the attach-or-start revert and the end-to-end threat matrix
carry their own gates in the same change (`docs/work/plans/PLAN-FINAL.md` §2 P3–P5).
