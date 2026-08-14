# 0043 — A probe with no answer is not an empty port

Date: 2026-08-14

Status: Accepted. Corrects the outcome model of [ADR 0022](0022-authenticated-loopback-restart.md)
(`serve --restart`), whose exit-code split assumed the pre-check always knows which case it is in.

## Context

`IServerProbe.RespondsAsync` returned a `bool`. Every way of not getting an answer collapsed into
`false`: a refused connection, a request that timed out, a connection accepted and dropped, a reply
that was not JSON-RPC. `ServerRestart.CycleAsync` and `NodeRunner.RestartServer` both read that
`false` as one fact — `RestartOutcome.Nothing`, *"nothing was listening — a restart is a plain
start"* — so `RestartRefusal` returned null and `serve` went on to bind.

The bind then threw `AddressInUse`, because something *was* listening. The catch probed again, got
`false` again, and printed the weakest of the lines it had:

```
ai-raccoon: port 49222 is in use — pass --port 0 for a random port, or free the port
```

So the code concluded "nothing is listening" and, one line later, failed to bind because something
is. Both statements came out of the same run, and nothing in the model could hold them apart.

Two acceptance gates measured this on a loaded machine (both pass in isolation):

- `AListenerThatWillNotIdentify_ReportsPortInUse_WithoutAskingItToStop` (23s) expected the foreign
  line and got the in-use line;
- `AServerThatRefusesOurToken_ExitsRestartTokenRefused_AndNeverAttaches` (17s) expected exit 12 and
  got exit **10** — `RestartLostThePort`, *"another server took the port while this one was
  starting"*. That is the worse of the two failures: the run never restarted anything and never
  held the port, so nothing was taken from it. The line is not merely uninformative, it is false.

A longer probe timeout narrows the window and leaves the logic exactly as wrong, so it is not the
fix here.

## Decision

**Three changes, all in the same shape: a fact the code does not have is never substituted with a
fact it would like.**

**1. The probe reports what it learned.** `ProbeVerdict` replaces the boolean at the decision
points (`RespondsAsync` stays, defined as `ProbeAsync(…) is Answered`, for the proxy's wait loops
that only ever wanted "can I talk to it yet"):

| Verdict | Evidence | Means |
|---|---|---|
| `Answered` | a JSON-RPC reply on `/mcp` | an ai-raccoon server holds the port |
| `NotListening` | `SocketError.ConnectionRefused` | **proof** nothing holds the port |
| `Unanswered` | timeout, reset, hang-up, or a non-JSON-RPC reply | no proof either way |

Only a refusal proves an empty port. A reply that is not ours proves a *listener*, so it is
`Unanswered` rather than `NotListening` — the old code read it as an empty port too.

**2. `RestartOutcome.Unknown` carries the absence.** `Unknown` still lets `serve` bind: refusing on
it would turn every slow probe into a failed start, and the bind is the cheapest thing that can
settle the question. What it does not do is claim the port was free.

**3. A refused bind refutes the pre-check.** The `AddressInUse` catch has proof the port is held.
`RestartTransition.AfterBindRefused` is the declared table, and the belief it is given decides the
report:

| Restarting | Pre-check believed | Port says now | Report |
|---|---|---|---|
| no | anything | `Answered` | attach (exit 0) |
| no | anything | otherwise | `PortInUse` (3) |
| **yes** | **`Unknown`** | **anything** | **`RestartProbeUnanswered` (16)** |
| yes | `Nothing` / `Stopped` | `Answered` | `RestartLostThePort` (10) |
| yes | `Nothing` / `Stopped` | otherwise | `PortInUse` (3) |

`RestartLostThePort` now requires a pre-check that actually established the port was free
(`Nothing`) or freed it (`Stopped`). Exit 16 says the honest thing instead:

```
ai-raccoon: cannot restart the server on port 49222: it is in use but gave the probe no answer,
so nothing was asked to stop — try again, stop the listener yourself, or serve on another port
```

It is a distinct code rather than a reuse of `PortInUse` because the remedies differ: 3 means a
holder that will not go away by itself, 16 is retryable in the sense 10 is — the probe may answer
on the next run. Both moves are logged with the verdict that triggered them (`604`/`609` in
`NodeRunner`, `656` in `ServerRestart`).

The transitions live in `RestartTransition` — `FromProbe`, `MayBind`, `AfterBindRefused` — so the
outcome is not assigned in one place and re-derived in three, and `RestartRefusal` now throws for
any outcome `MayBind` accepts rather than falling through to the timed-out line.

## Consequences

- `serve --restart` on a port held by anything that does not answer the probe exits **16**, not 3.
  `AForeignListener_StillReportsPortInUse` asserted 3 for a raw TCP listener that accepts and hangs
  up; it is renamed and re-pointed, because that listener answers no probe and `serve` cannot
  honestly call it foreign — only unidentified. The line keeps the words "in use".
- Plain `serve` (no `--restart`) is unchanged in code and message: `PortInUse` (3), or attach.
- `ServerProbe` takes an injectable request timeout, which is how the timeout case is reproduced in
  a unit test in 80ms. The production default is unchanged at 1s.
- The end-to-end gate needs no timing luck: a `LoopbackPort.Reserve()` that never accepts holds the
  port and answers nothing, so the probe can only time out, on any machine.

## Considered and rejected

- **Raising the probe timeout.** Makes the window smaller and the logic no better. If the 1s bound
  is itself wrong, that is a separate change with its own justification.
- **Re-running the cycle when the post-bind probe answers.** When the pre-check timed out and the
  port answers after the failed bind, `serve` could identify and cycle it then. It is a retry loop
  around a stop-the-world operation; the operator line says "try again" instead.
- **Splitting `Unanswered` into "reset" and "timed out".** A reset does prove a listener where a
  timeout does not, but nothing downstream would act differently on the two, and it doubles the
  table for a distinction no operator line uses.

## Known gap

`ServerRestart.WaitForPortToFreeAsync` still treats "the probe stopped answering" as "the port
freed", which is the same conflation inside the restart's own wait loop: a probe that times out
while the old server drains would be read as a freed port. It is left alone here because changing
it without a gate that watches it fail would be exactly the habit this ADR exists to correct; a
closed listening socket refuses on loopback, so the loop is right today for the reason it should be
asserting.
