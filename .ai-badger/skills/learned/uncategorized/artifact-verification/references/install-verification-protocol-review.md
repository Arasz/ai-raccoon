# Reviewing manual install-verification protocols (fresh-install / first-try tests)

Use when reviewing (or writing) a manual test protocol that must prove "a packaged
tool installs and works first try on a fresh machine" — dotnet global tools, MCP
servers, CLIs with bundled assets. The recurring failure is a protocol that
**false-passes** the claim because it exercises only a degraded path, or fails
spuriously on an output-channel or environment quirk. Worked example: ai-raccoon
1.0.6 fresh-install protocol review (2026-08-06) — verdict APPROVE-WITH-CHANGES;
project-specific facts live in `ai-raccoon-integration`.

## 1. Silent-degradation false passes (the big one)
Fresh state often lacks the settings/rows that switch the real feature on (engine
provider, API key, feature flag). The happy-path test then runs the DEGRADED path
and still goes green — e.g. keyword-only search succeeds with no embedding model
present; a write succeeds without embedding. Fixes:
- Find what must be configured for the real path (a CLI verb? a first-run seed?)
  and make it an explicit protocol step — or file the missing default as a bug.
  Then scope the claim to what was actually tested.
- Assert side-effect counters of the real path (deferred/pending queue == 0),
  not just "call succeeded".
- Assert the degradation warning is absent from stderr.

## 2. Presence != integrity; silent download fallbacks
Tools with sha-pinned bundled assets often VERIFY sha and, on mismatch, attempt a
runtime download (bounded, "never throws", warns on stderr). A presence-only layout
check misses a wrong/tampered file, and the fallback can make the test pass WITH
network repair. Fixes: sha256-verify pinned assets in the layout step; assert the
"downloading asset" warning lines are absent; assert boot latency is far below the
fallback's time bound (a slow first call IS the fallback running).

## 3. stdout vs stderr
CLIs that reserve stdout for a protocol (stdio MCP, JSON-RPC daemons) render
`--version`/`--help`/parse-errors to STDERR by design. A script capturing only
stdout gets empty output (false FAIL) — and the reverse trap is a daemon leaking
log lines into the protocol stream (false PASS). Fixes: capture 2>&1 for CLI
probes; for protocol probes assert every stdout line parses as the protocol (JSON),
with logs confined to stderr.

## 4. Package-manager cache provenance
`dotnet tool install` (etc.) serves from the local global-packages cache — a cached
nupkg can mask what the feed actually has, and a fallback loop re-tests stale bytes.
Fixes: pin the version; isolate the cache (`NUGET_PACKAGES=<fresh dir>`) so the
fetch is real; after any republish bump the pin (package versions are immutable).

## 5. Required parameters the protocol forgot
Tool calls often require args the protocol's prose omits (mandatory ids). Read the
handler signatures — every call must carry them (e.g. `projectId`) or the test
fails spuriously, or the harness "fixes" it silently and never notices.

## 6. Stateful dedup / stale-state false passes
Content-addressed stores dedupe identical writes (return existing entry, no new
row). Use timestamp-unique test strings and a fresh data root per run, or a re-run
"passes" against leftover state.

## 7. Env inheritance
Test shells inherit the user's env. Unset vars that switch modes (passphrase vars,
provider vars) so the test exercises the true default path — and state which path
the claim covers.

## 8. Concurrency-regression classes
If the tool historically bound a fixed port, add a dual-instance step (two servers,
separate data roots, both initialize) instead of trusting the changelog.

## 9. Cross-RID gaps are a static check
Per-RID asset packing (natives, models) means one RID can be complete while others
hit the download fallback. Scoping the claim to the host RID is fine, but also
inspect the package contents (`unzip -l`) for every declared RID folder.

## 10. Shutdown hygiene
Close stdin and assert clean exit within a timeout — proves EOF handling and
prevents orphan processes before temp-dir cleanup.

## 11. Environment preconditions as step 0
List runtimes/arch up front (`dotnet --list-runtimes`, `uname -m`) so failures are
attributed to the environment, not the package.

## 12. Wire-protocol specifics (MCP stdio)
MCP stdio = newline-delimited JSON-RPC 2.0 (one JSON object per line) — NOT
Content-Length framing (that is LSP). Handshake: `initialize` (with
`protocolVersion`) first → read lines matching by id, skipping notifications →
`notifications/initialized` → `tools/call` with `{name, arguments}`; assert no
`error` member in responses; use a duplex driver (Popen + per-step timeouts), not
`printf |` pipes.
