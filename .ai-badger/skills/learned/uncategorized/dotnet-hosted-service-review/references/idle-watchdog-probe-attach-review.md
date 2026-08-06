# Idle-watchdog + probe-attach review — checklist and worked case

Class of feature: a serve/daemon verb that (1) arms an idle-shutdown watchdog (BackgroundService + atomic timestamp, signaled from request middleware) and (2) probes a busy port to attach to an already-running instance instead of failing. Review gate shape: verdict + findings table + per-acceptance-row honesty verdicts.

## Watchdog invariants to verify

1. **DI triple, one instance.** `AddHostedService<T>()` registers only `IHostedService→T` (TryAddEnumerable) — middleware resolving the concrete type throws on the first request. Correct shape: `AddSingleton<Concrete>()` + `AddSingleton<ISignaler>(sp => sp.GetRequiredService<Concrete>())` + `AddHostedService(sp => sp.GetRequiredService<Concrete>())`, all gated by the SAME condition that adds the middleware. Pin with a host-shape test asserting `ReferenceEquals(GetRequiredService<ISignaler>(), GetServices<IHostedService>().OfType<Concrete>().Single())` — a dropped registration or a second instance fails it.
2. **Tick derivation.** `tick = min(fixedCap, timeout/4)` (cap 60s): a fixed 60s tick delays a 2s-timeout shutdown by up to ~62s and contradicts any short-timeout acceptance. Pin with a cadence test that FAILS under the fixed cap (advance to deadline → no fire; advance one tick → fire).
3. **Baseline at construction.** Fresh host lives a full timeout with zero requests (correct — just started, not forgotten). Consequence for tests: the deadline starts BEFORE startup completes (see the testing skill's deadline-starts-at-construction trap).
4. **Activity scope.** Only the real traffic endpoint signals (path-branched middleware `Path == "/mcp"`; 404s on other paths never count). Background passes must never reset the deadline — pin with a shared-fake-clock two-service test (extraction counter > 0 AND StopCalls == 0 after the pass; shutdown still fires later) plus a grep gate: `NotifyActivity` called only from the middleware.
5. **Ownership/attach.** A second process attaching to an existing server must NOT arm its own watchdog, touch the bank, or read settings — the owner keeps the timer. Attached-mode locator output (URL/entry for a server it doesn't own) is the same staleness class as owner-printed locators (the watchdog makes any printed locator a snapshot); NOT a finding when the design specified the output — the stderr provenance line is what distinguishes attach from ownership.

## Probe-attach review — attack surface

- **False-positive surface.** A status-set + body-substring discriminator is only as good as the foreign listeners that can satisfy it. Enumerate them explicitly: which statuses are excluded (404 excluded is the big win — most foreign servers answer 404 for an unknown path), which body shapes contain the magic substring (JSON-RPC error bodies — i.e. a DIFFERENT MCP/JSON-RPC server is the realistic false positive, not a plain web server). Bound the damage (wrong URL printed, exit 0, stderr provenance line) and judge: is the acceptance sound? It is when no cheaper discriminator exists without a protocol addition (banner handshake), the false-positive set is narrow, and the failure is self-limiting + diagnosable. Record the acceptance explicitly in the report.
- **Probe spec exactness.** POST to the exact endpoint path, protocol-appropriate Accept header, non-JSON body with the JSON content type (415 only when the content type is wrong — the status set must match the endpoint's real behavior), 2 attempts × short timeout, bind-race → re-probe once → attach or distinct error exit code. Verify the probe against the REAL endpoint: the attach test should drive a real first server (not a stub) so the probe path is proven end-to-end.
- **Attached-mode side effects.** Exit 0, same stdout line as owner mode, provenance on stderr, no watchdog, no bank touch, `--idle-timeout` ignored (owner decides) — each is a checkable line.
- **Static probe HttpClient** (process-lifetime, short timeout, never disposed) is acceptable — canonical pattern, loopback targets, socket reuse desirable.
- **Defensive fallbacks** that are unreachable because a CLI validator rejects the input are fine; check the fail-safe direction (falling back to ARMED is safer than disabled).

## Worked case — AiRaccoon serve mode (2026-08-06)

Gate verdict: APPROVE-WITH-CHANGES (1 SHOULD-FIX low: real-time smoke 2s→5s; 7 NITs; 0 MUST-FIX). All 16 acceptance rows honest.

Repo-specific numbers (for AiRaccoon follow-ups):
- Probe: `POST /mcp`, `Accept: application/json, text/event-stream`, body `"x"` with `Content-Type: application/json`; recognized iff status ∈ {400, 405, 406} AND body contains `"jsonrpc"` (ordinal); 2 attempts × 1s; bind `AddressInUseException` (walk inner chain incl. `SocketError.AddressAlreadyInUse`) → re-probe once → attach or `ExitCode.PortInUse = 3`.
- Watchdog: tick = `min(60s, timeout/4)`; baseline = ctor `GetUtcNow()`; `Interlocked.Exchange` on `NotifyActivity`; `StopApplication()` → graceful → exit `Success`. EventIds: ServeRunner.Log 601/602/603/605 (no 604), IdleWatchdog.Log 610/611/612 (600-series was free).
- R3 default gating by construction: `ServerConfig.IdleTimeout` defaults `Zero`; `ToServerConfig` never sets it (pinned by a test); serve applies the 4h default only when the option is absent. Verify "untouched" claims by diffing the file (empty diff = by-construction proof).
- Host seam for deterministic wiring tests: optional `TimeProvider?` param on the host builder, `AddSingleton(timeProvider ?? TimeProvider.System)` registered AFTER `Dependencies`' registration — later descriptor wins in Microsoft DI.
- Real-time smoke findings: 2s timeout vs pre-URL phase (temp-bank create + SHA-256 of 23MB bundled model + Kestrel bind ≈ 0.2–0.3s, ~10× margin); recommended 5s/15s. Suite flake triage: real-host tests in the default parallel Unit lane (no `[Collection]`) starve 5s-bounded BDD `StepUntilAsync` polls → "plausible load contributor, not cause".
- Doc-drift NITs to watch: plan table rows that contradict §3.5/appendix (e.g. "ToServerConfig sets 4h", "601–604", "Program.cs zeroes IdleTimeout") — implementation followed the more authoritative lines.
