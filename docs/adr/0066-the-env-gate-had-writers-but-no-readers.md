# 0066. The env gate had writers but no readers

Date: 2026-08-15

Status: Accepted

## Context

WP19's flake — five observations, one red build on an unrelated PR, and two disconfirmed hypotheses
(`LoopbackPort` contention, then CPU contention). ADR-0062 fixed a genuine but *different* defect in
`IdleWatchdogTests` and recorded `ToolRefusalsTests` as still open with no known cause.

ADR-0061's diagnostic then named it on the next CI failure:

```
should start with "access-denied:" but was "unexpected-error: SqliteException"

  [Error] WatchHostedService:            SQLite Error 26: 'file is not a database'
  [Error] BankMaintenanceHostedService:  SQLite Error 26: 'file is not a database'
  [Error] ToolRefusals: memory_write     SQLite Error 26: 'file is not a database'
```

**SQLite error 26 is what you get opening a plain database with a key.** Three services in one server
hit it at once, because they share a bank.

`TestData.EnvVarGate` already exists, and its own comment names the hazard: *"Serializes tests that
mutate the process-global `AIRACCOON_DB_PASSPHRASE`"*. But it is a `SemaphoreSlim` taken only by the
classes that **write** the variable. It serialises writers against each other and does nothing for a
class that merely **reads** the environment — which is what any test that opens a bank through the
real host does, because the DI graph registers `EnvEncryptionKeyProvider`.

**The gate had writers and no readers.**

## Reproduced on demand

Not by looping the test. Setting the variable ambiently and running the class:

```
AIRACCOON_DB_PASSPHRASE=someambientpassphrase dotnet test --filter ToolRefusalsTests
→ Failed: 1, Passed: 33
   [Error] Bank maintenance run failed
```

The same Error record CI produced, from the same cause, on command. That is the causal link the
package required before any fix — *"a flake fixed without a reproduction is a flake that moved"*.

## Decision

**`ToolRefusalsTests` takes `EnvVarGate` as a reader**, for the lifetime of each test, via
`IAsyncLifetime`. It cannot now run while a writer holds the variable.

Scope is deliberately one class. `ServeRestartTests` and `BackendLauncherTests` already take the gate
— they are writers too — so the exposed reader in the observed failures was this one.
`WatchEventSourceTests` failed in the same CI run but with a filesystem-timing symptom and **no**
error 26, so it is left alone rather than swept in on a guess.

## What this is not

**It is containment, not the cure.** The cure is that tests should not mutate process-global state at
all: a seam over the environment read in `EnvEncryptionKeyProvider` would let the writers inject a
value instead of setting one, and no reader would need a gate. That is a production change with its
own design, and naming it here is worth more than half-doing it — 18 test classes currently reach for
`EnvScope`, and every one of them is a writer that a future bank-opening test will have to know about.

The honest statement of the residual: **any new test that opens a bank through the real host, and
does not take this gate, reintroduces the defect.**

**Amended same day — the residual is closed.** `EnvGateReaderRuleTests` scans every test source for
`CreateServerHost` and requires the file to take the gate, with a companion assertion that the scanned
set is non-empty. Watched red: it named `ProxyLaunchE2ETests` and `ToolTelemetryCoverageTests`, the two
`Speed=Slow` classes the first sweep had missed. Eight classes became readers.

**Measured, because serialising tests is a real cost and the number decides it:** `Speed=Fast` ran
145.7s and 137.2s before, 141.5s and 145.0s after — **+1.8s on ~141s, 1.3%**, against a run-to-run
spread of 8.5s in the baseline pair alone. The cost is inside the noise.

## Consequences

- `ToolRefusalsTests` becomes `IAsyncLifetime`; its temp-root cleanup moves into `DisposeAsync`.
- It now serialises against every `EnvScope` user. `Speed=Fast` is 2m17s against 2m11s before — the
  cost is inside the noise of the runs either side of it.
- ADR-0062's "not fixed" section is superseded for `ToolRefusalsTests`. The `LoopbackPort` hypothesis
  the package started from stays disconfirmed: no port was ever involved.

## Evidence

The reproduction above, and `Speed=Fast` 2165 passed with the gate applied.

**On proving the fix.** A fix for a rare flake cannot be proven by the suite going green — it was
usually green. What is proven is the *mechanism*: an ambient passphrase makes this class fail, on
command, with the error CI reported, and the class can no longer run while any writer holds the
variable. That is the strongest available claim, and it is deliberately not stated as "the flake is
gone".
