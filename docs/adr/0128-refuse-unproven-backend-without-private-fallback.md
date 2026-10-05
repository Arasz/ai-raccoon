# 0128 — Refuse an unproven backend without private fallback

Date: 2026-10-05

Status: Accepted

## Context

ADR-0106 made attachment depend on cryptographic proof of this data root's identity.
It also started a private backend on an ephemeral port whenever proof failed. A
running ai-raccoon whose verifier could not confirm its identity therefore caused
another backend and model to load. An uncertain probe could produce the same result.

Server presence and server identity answer different questions. Failure to prove
identity does not establish that the configured port is free.

## Decision

Retain the ECDSA nonce challenge, root and port binding, read-only verifier, and
token protections from ADR-0106. Remove automatic private fallback from proxy and
CLI settings acquisition, including its reuse and proxy-owned shutdown lifecycle.

| Probe result | Proof | Acquisition |
|---|---|---|
| `Answered` | valid | Attach without starting a backend |
| `Answered` | failed | Refuse with `Server.Unproven` (50), without starting a backend |
| `Unanswered` | valid | Attach; proof establishes identity despite the MCP probe |
| `Unanswered` | failed | Refuse with `Server.Unproven`, without starting a backend |
| `NotListening` | performed after shared acquire | Allow startup on the configured port; return its URL only after valid proof |

The launcher rechecks `ProbeAsync` before `Process.Start`. Only `NotListening`
(connection refusal) authorizes a start. A subsequent `Answered` result can return
a candidate endpoint for proof; `Unanswered` returns unavailable without a start.
Caller cancellation propagates and is checked before starting a process.

A failed proof after a shared start returns refusal. It never triggers a second
launch. Reopen runs the same configured-endpoint policy again. Proxy and CLI
callers preserve the proof reason and start no replacement backend; they send no
token or tool payload to an unproven listener.

Shared lifetime remains with the idle watchdog. Failed proof does not authorize
shutdown or killing an unrelated server. Explicit `serve --port 0` remains
available. Numeric error/log identifiers removed with private fallback stay
reserved, and executable-path rescue after a tool update remains supported.

## Scope and limits

The guarantee covers the configured loopback endpoint. It does not scan for
processes or listeners elsewhere on the machine. Two proxies can each observe a
free port before either starts and both attempt a shared launch; ordinary bind
exclusion determines the winner. This decision adds no cross-process startup lock.

A squatter can still deny service. It can no longer force an extra backend per
client. Resolving a key mismatch or conflicting port is an operator action; the
verifier never heals or replaces the trust anchor.

MCP connection replacement after successful proof remains the separate TOCTOU
limitation recorded in ADR-0106. This decision does not redesign transport binding.

## Verification

A regression starts a real ai-raccoon server, verifies it as a positive control,
then changes the disposable fixture's trust key and creates a fresh verifier.
The server keeps its cached signer and remains live while proof fails. Acquisition
must refuse with zero launcher calls. A real-process marker at the existing
executable-path seam independently observes attempted starts. Production proxy
E2E verifies refusal and survival of the original server.

Tests also cover foreign HTTP replies, inconclusive probes, the launcher's second
probe, failed proof after one shared start, cancellation, CLI acquisition and
reopen, successful attachment/start, and no-token security controls. The reviewed
[implementation plan](../plans/2026-10-05-proxy-server-identification.md) names the
acceptance gates and RED/GREEN evidence requirements.
