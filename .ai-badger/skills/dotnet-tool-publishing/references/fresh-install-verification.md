# Fresh-install verification of a published MCP tool (the "works first try" gate)

Measured 2026-08-06 on `<pkg-id>` 1.0.6 (osx-arm64), executed as
`scripts/manual-fresh-install-test.py` (committed in the reference repo). The protocol is the post-publish complement to the pre-publish `scripts/verify-tool-package.sh` gate. Purpose: prove a clean `dotnet tool install` from nuget.org
works perfectly first try — all deps present, no missing assets, no silent repair — and to catch regressions (port binds, model packaging) that unit tests cannot see.

## Isolation (never touch the real install)

- `dotnet tool install --tool-path $TOOLPATH --version <v> <pkg-id>` — a temp tool path, NOT `-g`; the user's real `~/.dotnet/tools` shim stays untouched.
- `NUGET_PACKAGES=<fresh dir>` in the child env — forces a REAL nuget.org fetch instead of serving from `~/.nuget/packages` (the install would otherwise pass on a cached copy).
- `--data-root $DATAROOT` (fresh dir) — bypasses the tool's default `~/.<app>` so the test never reads or writes real user data.
- `unset <APP>_DB_PASSPHRASE` — an inherited secret env var silently flips the tested path into encrypted-bank mode.
- Everything under one `mktemp` base; `rm -rf` at the end.

## Integrity over presence

Presence checks false-pass on wrong/tampered assets: a sha-mismatched model fails
`BundledModel.LocateVerified` and silently triggers the runtime HuggingFace download fallback. Verify bytes:

    shasum -a 256 <store>/Models/model_qint8_arm64.onnx   # == BundledModel.ModelSha256 (4278337f…)
    shasum -a 256 <store>/Models/vocab.txt                # == BundledModel.VocabSha256 (07eced37…)

## CLI checks render to STDERR

In a stdio MCP tool stdout is reserved for the protocol, so `--version`/`--help` print to stderr by design (System.CommandLine `RenderTo(Console.Error)`). Capture `2>&1` and assert a SUBSTRING (`1.0.6`), never exact equality — the version
flag prints
`1.0.6+<commit-sha>` (InformationalVersion), not a bare `1.0.6`.

## MCP stdio round trip (the real proof)

- Framing: newline-delimited JSON-RPC 2.0 — one JSON object per line, NO Content-Length headers (that's LSP — the classic trap).
- Drive with a Python `subprocess.Popen` duplex (stdin/stdout/stderr pipes), per-step timeouts. Never `communicate(input=…)` — closing stdin makes a stdio server exit on EOF before it replies (also in dotnet-mcp-server's probe pitfalls).
- `initialize` FIRST with a `protocolVersion` (e.g. `2025-06-18`); accept the server's negotiated version rather than asserting one.
- Send `notifications/initialized` before any `tools/call`.
- Read stdout line-by-line, match responses BY ID (skip interleaved notifications); every line must parse as JSON (catches log leakage corrupting the protocol).
- Assert the initialize round-trip completes < 10 s — a missing model costs up to the full 30 s download bound, so latency is a silent-repair tripwire.

## THE FALSE-PASS TRAP: config-gated engines degrade silently

The single most important lesson. `embedding.provider` is NEVER auto-seeded on a fresh bank (only `model set local` / `model set openai` write it). With provider empty:
`memory_write` skips embedding entirely (`EmbedIfConfiguredAsync` early-returns) and
`memory_search` runs FTS5-only — an exact-keyword query still returns the entry. A completely model-less install therefore PASSES a naive "search works" check.

This empty-provider state is deliberate and user-surfaced (`model reset` prints "embedding engine reset to default: no engine (FTS5-only search)"; `model show` prints "provider:
(none — FTS5-only search)"). So the test must not try to change the product; it must test honestly:

1. Run the documented setup verb as part of the happy path: `<tool> --data-root
   $DATAROOT model set local` (bundled engine, no download needed).
2. Assert the engine actually ran: `memory_stats` must show `entries >= 1` AND
   `pending == 0`. `pending > 0` means writes were deferred, never embedded — the
   "no missing models" claim is void.
3. Assert stderr contains NO "Downloading bundled model asset", NO "Bundled embedding model unavailable", NO "Failed to download bundled model asset" — no silent repair happened at runtime.
4. Keep a zero-config probe (fresh data root, NO setup verb) as an informational step:
   it documents the degraded default (entries=1, pending=1, FTS5 hit) instead of pretending it is the model path.

## Result shapes come from source, not guesses

The MCP SDK wraps tool results: `result.content[0].text` holds a JSON STRING of the tool's record — unwrap before asserting fields. And `serverInfo` reports the ASSEMBLY identity, not the `.mcp/server.json` marketing identity:

- `serverInfo.name` = assembly name (`<AssemblyName>`), `serverInfo.version` = AssemblyVersion numeric-only (`1.0.6.0`) — the csproj documents this split ("MCP serverInfo.version reads AssemblyVersion (numeric-only)"). Assert version as a
  PREFIX, name as-is.
- Result record shapes (verify in source before writing assertions):
  `WriteResult(Hash, Path, Context, CreatedAt)` — no content field;
  `StatsResult(Entries, Pending, Contexts)` — JSON keys `entries`/`pending`, NOT
  `entryCount`/`pendingCount`;
  `SearchResultList(Results)` of `MemorySearchResult(Hash, Seq, Ranking, Path, Snippet)` — assert search hit by HASH identity with the write result, not by content text (the snippet is truncated).

In this session 7 of 8 first-run "failures" were driver assertions against guessed shapes — zero product bugs. Read the records, then write the assertions.

## Regression checks

- **Dual-instance:** launch a second server on a second fresh data root and initialize both concurrently — directly regression-tests port-bind bugs (the 127.0.0.1:5000 collision class fixed in 1.0.6 by the plain-app-host-for-stdio
  refactor).
- **Graceful shutdown:** close stdin, assert clean exit 0 within a timeout — proves EOF handling and prevents orphans before cleanup.

## Version pinning

NuGet versions are immutable, so the pin must be bumped after every republish. Expose it as an env override (`<APP>_VERSION`, default = current) so the gate re-runs against the next release without editing the script. Optionally also verify
the model/vocab sha pins against source constants rather than hardcoding, so a model swap fails loudly.

## Driver hygiene (from the 2026-08-06 review round on the committed script)

- **Assertions must be able to fail.** A passphrase check written as
  `os.environ.pop(...)` followed by "not in os.environ" is tautological — the pop already removed it. Capture the popped value (`had = pop(...)`) and assert on that.
- **Re-assert stderr AFTER the process join, not only on a sleep.** A `sleep(1)` + drain check while the server is alive is a race, however unlikely. Close stdin → wait (exit 0)
  → join the stderr drain thread → assert no-download/no-unavailable lines on the FINAL stderr. The early check stays as a fast signal; the joined one is the proof.
- **sha pins are version-coupled and live in THREE files** (the gate script,
  `verify-tool-package.sh`, `download-embedding-model.sh` — plus `BundledModel.cs`
  constants). After a model swap, re-derive the hashes from verify-tool-package.sh and sync all of them; a forgotten sync fails loudly (mismatch = FAIL), never silently.
- **Register temp-dir cleanup via `atexit`** so an unhandled exception (timeout, missing dotnet) still removes the base dir instead of leaking it.

## Step order (worked)

0. env preconditions: `dotnet --list-runtimes` (NETCore.App + AspNetCore.App 10.0.x),
   `uname -m` = arm64, `NUGET_PACKAGES` isolated, passphrase unset
1. fresh install from nuget.org (exit 0; ~2-3 s when cached deps resolve)
2. layout + sha256 integrity (store root via `os.walk` for `.store/…/tools/net10.0/<rid>/`)
3. `--version` (2>&1, substring)
4. `--help` (2>&1, verb tree: access/model/retrieval/sync/watch/encryption + --data-root)
5. `model set local` (setup verb, confirmation on stdout)
   6-9. MCP: initialize (< 10 s) → notifications/initialized → memory_write (unique timestamped string, projectId required or `invalid-params`) → memory_search (hash identity) → memory_stats (entries >= 1, pending == 0)
10. stderr assertions (no download/unavailable/failed lines)
11. dual-instance concurrent initialize
12. close stdin → exit 0 (both instances)
13. zero-config probe (informational: FTS5-only, pending=1)
14. cleanup

Exit 0 = all green on first install attempt, zero manual repair.
