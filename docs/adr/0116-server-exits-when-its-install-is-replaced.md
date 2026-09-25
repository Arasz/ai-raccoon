# 0116 — The server exits when its own install is replaced

Date: 2026-09-25

Status: Accepted

## Context

`dotnet tool update -g ai-raccoon` deletes the outgoing version's install directory
(`~/.dotnet/tools/.store/ai-raccoon/<old-version>/...`) while the always-on `serve` backend
launched from it (ADR-0020) is still running — the exact mixed-binary scenario ADR-0022's Context
names. Until now nothing noticed on the server side: [ADR 0022](0022-authenticated-loopback-restart.md)
gave the *operator* a way to recover (`serve --restart`, an authenticated `POST /shutdown`), but the
running process itself stayed up, none the wiser that its own files were gone.

The symptom an operator actually sees: `ModelMigrationJob.HasWorkAsync` and
`CodeReindexJob.HasWorkAsync` both resolve the bundled engine to check for outstanding work, and
`BundledModel` throws `BundledModelInstallReplacedException`
(`src/AiRaccoon.Infrastructure/Embedding/BundledModelInstallReplacedException.cs`) once
`AppContext.BaseDirectory` no longer exists. `MaintenanceJobRunner.RunDueAsync` catches that as a
due-check failure and logs event 527 at Warning — every ~15 s poll, forever, until someone runs
`serve --restart` or kills the process by hand. A tool call that happens to hit the embedding path
fails too, but nothing ends the process that can no longer serve a fresh embed.

## Decision

**The `serve` backend detects that its own install directory is gone and shuts itself down
cleanly, the same way `IdleWatchdog` and `/shutdown` already do
(`IHostApplicationLifetime.StopApplication()`).** The next MCP call finds nothing listening, and the
proxy's backend acquire (`BackendSessions`, ADR-0106) starts the new install — no manual
`serve --restart` required. This is IdleWatchdog's pattern applied to a second trigger, not a new
shutdown mechanism.

- **`InstallWatchdog`** (`src/AiRaccoon/Hosting/Watchdog/InstallWatchdog.cs`) is a `BackgroundService`
  modeled directly on `IdleWatchdog`: a `PeriodicTimer` driven by the injected `TimeProvider` (tests
  advance a `FakeTimeProvider` instead of sleeping), and an `internal RunOnce()` test seam that
  returns whether the host was asked to stop.
- **The probe is injectable.** The constructor takes `Func<bool> installDirectoryExists` rather than
  calling `Directory.Exists` itself, so a test can simulate the directory disappearing without
  touching any real directory the test host is running from. Production wires
  `() => Directory.Exists(AppContext.BaseDirectory)`.
- **Registered unconditionally for the `serve` node host**, alongside `IdleWatchdog` in
  `WatchdogRegistrations.RegisterWatchdogServices` — unlike `IdleWatchdog`, it is never gated by
  `--idle-timeout`: every `serve` process is a `dotnet tool update` target regardless of how its idle
  timeout is configured. It is not registered for the proxy, for CLI verbs, or for an in-process test
  host that never calls `RegisterWatchdogServices`.
- **One clear warning, then stop.** On the tick that finds the directory gone, it logs once (event
  613) — `"ai-raccoon: this server's install at '{InstallDirectory}' was removed, most likely
  replaced by 'dotnet tool update'; shutting down so the next MCP call starts the new version"` —
  and calls `StopApplication()`. The loop returns immediately after, so a second tick can never log
  or stop a second time.
- **A poll every 20 seconds** (`InstallWatchdog.DefaultCheckInterval`), the same order of magnitude
  as the maintenance loop's own on-demand poll — noticing within one interval is fast enough that the
  527 spam this closes never gets far.
- **`MaintenanceJobRunner` no longer logs 527 for this specific cause.** A due-check failure whose
  exception is `BundledModelInstallReplacedException` now logs at Debug (event 529) instead of
  Warning (527): the install watchdog's own line already explains the cause once, loudly, and the
  process is about to exit — a Warning repeated four times a minute in the meantime is noise, not a
  new symptom.

## Consequences

- **Positive.** The gap ADR-0022 named and left manual is now closed automatically: `dotnet tool
  update -g ai-raccoon` alone is a complete recovery, with no `serve --restart` step. ADR-0022's
  `--restart` stays as the operator-triggered path (e.g. to force a same-version restart) and the
  cross-root/token semantics it defined are unchanged.
- **Positive.** `ModelMigrationJob`/`CodeReindexJob`'s 527 spam is bounded to at most one watchdog
  interval instead of running forever.
- **Neutral — a second background poll.** Like `IdleWatchdog`, it costs one more timer tick every 20
  seconds; the check itself is a single `Directory.Exists` call.
- **Addressed by a follow-up (2026-09-25).** The gap this ADR left open — whether a proxy that
  resolved `Environment.ProcessPath` before the update can still respawn afterward — was real:
  measured on macOS (osx-arm64, .NET 10), `Environment.ProcessPath` and `Process.MainModule.FileName`
  both report the **symlink-resolved** target path for a process launched through the
  `~/.dotnet/tools/ai-raccoon` symlink, never the symlink path itself, so a proxy started before the
  update tries to spawn a deleted `.store/<old-version>/...` binary and fails. `BackendSessions.AcquireBackend`
  now falls back, in order, to the dotnet global-tool shim
  (`Path.Combine(<user profile>, ".dotnet", "tools", "ai-raccoon")`, `.exe` on Windows) and then to
  `ai-raccoon` resolved on `PATH`; neither found keeps the pre-existing refusal.
  `BackendLaunchArguments.ResolveExecutable` does the check and the fallback (file-existence,
  home-directory and `PATH` lookups are injected, never touching the real machine in a test), and
  logs once, naming the missing path and the fallback used.

## Non-Goals

- **Changing `BackendSessions` or the proxy's spawn logic.** The fix is entirely on the backend's own
  shutdown; how a proxy re-acquires afterward is unchanged (ADR-0106).
- **A grace period or draining beyond what `StopApplication()` already gives.** The host's existing
  `ShutdownEndpoint.DrainWindow` (10 s, ADR-0022) applies the same way it does to every other
  shutdown trigger.
- **Detecting a *partial* install (some files missing, directory still present).** Only the
  directory's existence is checked; a corrupted-but-present install is unchanged behavior.

## Related decisions

- [ADR 0022 — `serve --restart` over an authenticated loopback shutdown](0022-authenticated-loopback-restart.md):
  named this gap and gave the operator a manual recovery over the same `StopApplication()` path this
  ADR now reaches automatically.
- [ADR 0020 — Always-on HTTP + stdio proxy](0020-always-on-http-stdio-proxy.md): the mixed-binary
  lockout this closes another leg of.
- [ADR 0106 — Attach-or-start again, proven by a per-root identity key](0106-attach-or-start-with-backend-identity-proof.md):
  `BackendSessions`' acquire is what starts the new install on the next forward.

**Evidence:** `src/AiRaccoon/Hosting/Watchdog/InstallWatchdog.cs`;
`tests/AiRaccoon.Tests/Unit/Setup/Serve/InstallWatchdogTests.cs`;
`src/AiRaccoon/Hosting/Watchdog/WatchdogRegistrations.cs`;
`tests/AiRaccoon.Tests/Integration/Setup/McpServerSetupHostTests.cs`
(`HttpHost_AlwaysRegistersTheInstallWatchdog`); `src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs`;
`src/AiRaccoon.Infrastructure/Embedding/BundledModelInstallReplacedException.cs`. The 2026-09-25
executable-fallback follow-up: `src/AiRaccoon/Hosting/Common/BackendLaunchArguments.cs`
(`ResolveExecutable`, `GlobalToolShimPath`, `PathExecutable`); `src/AiRaccoon/Hosting/Proxy/BackendSessions.cs`
(`AcquireBackend`); `tests/AiRaccoon.Tests/Unit/Hosting/BackendLaunchArgumentsTests.cs`;
`tests/AiRaccoon.Tests/Unit/Hosting/BackendSessionsTests.cs`.
