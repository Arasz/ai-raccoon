# 0104 — Remove the stdio full-server mode

Date: 2026-09-09 (owner rulings) · implementation across P1–P6

Status: Accepted

## Context

Every pi session grew a second heavyweight ai-raccoon server (observed 3.22 GB,
since reaped). The spawner was the mem-based-rag extension's `RaccoonClient`,
which launched `ai-raccoon --transport stdio`: a complete in-process server
(bank, key, ONNX) beside the proxy-started `serve` backend the bare children
already shared. ADR-0020 had kept that flag as "the escape hatch", so two full
servers per session was the documented shape, not an accident.

The implementation inventory then showed the titled goal ("a second full server
is unlaunchable") needed one more cut than the title named: a bare
`--transport http` launch was also a complete in-process server, ungated and
reachable with no token. Removing only the stdio value would have left a second
full server one flag away. The owner ruled the full set on 2026-09-09 (7/7
APPROVE, no notes). The rulings, with the plan's shorthand:

- **D2** — bare `--transport http` is removed too: a bare launch means proxy,
  full servers come only from `serve`.
- **D1** — removal signal matrix: `stdio` dies at parse with exit 9 plus a
  targeted hint on bare launches (serve-verb paths yield 15); new exit code 26
  is out, no script branches on it. `https` is ruled in the same matrix.
- **D8** — `https` shrinks out of the surface with stdio: the accepted set is
  `{Proxy, Http}`.
- **D5** — tests migrate to HTTP-ephemeral hosts; no internal stdio host
  survives for tests.
- **D6** — hermes standardizes on the temp-port proxy recipe (temp
  `--data-root` plus a leased `--port`).
- **D9** — `scripts/` migration is in task scope (provider-setup probe,
  fresh-install script, PoC port gates, threshold-eval runner, poc-parity
  README, eval-plan-doc disposition).
- **D3** — same-data-root serves on distinct ports are live-probed in P6, else
  scoped to documented allowance without live proof.
- **D10** — a filled manual checklist in `docs/work/checklist/` is a P6 exit
  gate (required).
- **Q2** — the proxy warning on the serve path is restored (P3 micro-task, not
  this package). **Q3** — vestigial transport-plumbing parameters are accepted
  long-term as documented.

Two mechanics constraints shaped the implementation. First, deleting the enum
member goes silent in the real binary: `TryParse` returns false, `GetCliInput`
returns null, and `--transport garbage` already exits 9 with empty stderr
today, so deletion cannot carry the hint. The final shape is keep-enum plus a
raw-args pre-scan (no follow-up deletion; the members stay as parse-rejected
values and help text is hand-maintained). Second, the proxy's own stdio wire
(`ProxyRunner`'s SDK `StdioServerTransport`) is not `McpTransport.Stdio`: a
bare child still speaks MCP over stdio on its pipes. The cut is the CLI value
plus the in-process full-server host, nothing else.

## Decision

Bare `ai-raccoon` always proxies (P2 collapsed the host dispatch to the
unconditional web host for `serve`; `AppRunner` routes every bare launch to
`RunProxy`). `serve` is the sole full server. The parse contract, verified
live against the worktree build:

- bare `--transport stdio` (both `--transport stdio` and `--transport=stdio`
  spellings) exits 9 with `Hint: --transport stdio was removed; run bare
  'ai-raccoon' for the proxy or 'ai-raccoon serve' for HTTP.` on stderr;
- verb paths (`serve --transport stdio`, `settings … --transport stdio`)
  exit 15 (`InvalidArgument`), since `serve` takes no `--transport` at all;
- bare `--transport https` exits 9 with a rejection naming `proxy|http`;
- bare `--transport http` still parses and proxies like any bare run;
- `--help` lists `--transport <proxy|http>`.

Quiet mode keeps two pins and no more: the serve/HTTP host still file-sinks
every level (`HostLogging` routes `Quiet` to `quiet.log` beside the bank), and
the proxy forwards `--quiet` to the backend it starts (`BackendLaunchArguments`
passes `--data-root`, `--install-scope` and `--quiet` through). The proxy
itself stays exempt by design (stderr, `Warning` and above), so the
backend-unavailable line is always visible.

Hermes (plugin and docs) moves to the D6 recipe: a temp `--data-root` plus a
leased `--port` in `binary_args`, proven against a busy default 7721 (Evidence
below). The plugin-level `transport: stdio | http` names stay: "stdio" names
the spawned proxy child, which is what a bare spawn still is.

Scripts migrate per D9: the provider-setup probe uses the D6 recipe; the
fresh-install driver spawns one proxy child per temp bank on a leased port;
the two eval harnesses run a serve backend plus a proxy child per session, so
`[mmr-poc]` marker discipline keeps reading the process that prints the
markers (a proxy child would not carry them: the launcher captures the
backend's stderr for the failure path alone). The threshold-eval plan and
report under `docs/work/` stay untouched as history, with the new capture
composition recorded in the poc-parity README instead.

Compatibility contract, both floors:

- Forward: the first release carrying this removal (after 1.41.2) rejects the
  removed values at parse. Any launcher still passing `--transport stdio`
  gets exit 9 plus the hint on bare launches instead of a server. The
  mem-based-rag bare-proxy switch lands first in pi-badger-integration, ahead
  of the removal merge; its handoff states bare=9+hint, serve-verb=15.
- Back: a flagless bare spawn works against any server from 1.6.0 on, the
  release that made bare launches proxy. Older servers predate the proxy and
  cannot serve a flagless child. The hermes provider plugin ships in this repo
  in lockstep with the server, so both of its floors are the removal release.

Failure-surface strings belong to the P5 pass, which landed `5a813c65` on
`task/air-remove-stdio-full-server-mode-impl-p5` while this package was in flight:
`Unavailable()` now reads `ai-raccoon: {reason}; no in-process fallback exists — start
the backend first: ai-raccoon serve --port <port>` (quoted verbatim; `{reason}`
carries the failure detail), plus proxy-wording comment rewords. The reference doc
quotes that template rather than paraphrasing it; this record authors no operator
line of its own.

## Consequences

- **Positive:** one bank, one writer shape per data root: no second full
  server is launchable from the CLI anymore. The ungated direct-HTTP posture
  retires with the removal (every HTTP endpoint is a token-gated `serve`
  host), which also retires the matching SECURITY gap.
- **Positive:** the D6 recipe removes the probe's dependence on a free 7721:
  setup and hermes tests isolate by construction rather than by hoping the
  default port is free.
- **Negative:** marker discipline is serve-stderr-only now. Any future harness
  that spawns a bare proxy and reads the child's stderr for backend log lines
  sees nothing on success by design (`BackendLauncher` discards backend stdout
  and keeps only a bounded stderr tail for the failure path). The eval
  harnesses carry the heavier serve-plus-proxy composition for exactly this
  reason.
- **Negative:** proxy-started backends outlive their child (lifetime belongs
  to the idle watchdog alone). The setup probe and the fresh-install driver
  each leave short-lived backends behind on temp ports and temp banks; both
  scripts say so in their comments.
- **Neutral:** `Stdio`/`Https` enum members remain as parse-rejected values
  (the silent-deletion finding above). Help text is hand-derived from the
  surviving pair, so a future transport must update it deliberately.
- **Neutral:** the D3 same-root probe and the D10 manual checklist are P6
  work. Until P6 runs, same-data-root concurrent serves are allowed without a
  live characterization, and this removal does not claim one.

## Alternatives rejected

- **(a) Keep stdio as a hidden flag** (parseable but undocumented, or
  `[Obsolete]`). Rejected: a hidden full server keeps the two-server incident
  class alive for every caller that already passes the flag, which is the
  exact population that caused the incident. Removal must be loud at parse or
  it is not removal.
- **(b) Map `--transport stdio` to the proxy with a deprecation line.**
  Rejected: it preserves an isolation lie. The old flag meant "no ports, no
  backend process"; silently delivering a proxy (a port plus a backend that
  outlives the caller) under that name trades a loud failure for a wrong
  mental model.
- **(c) New exit code 26 for the removal.** Rejected: no script needs to
  branch removal-vs-typo (D1 ruling), and 26 costs a kept-parseable value or
  the pre-scan either way. Exit 9 plus the stdio-specific hint carries the
  same information without retiring another code.
- **(d) Keep bare-http and restate the goal as "remove the stdio full
  server".** Rejected (D2): the incident class is "a second full server", and
  bare-http was one. Keeping it would have fixed the title while leaving the
  defect.
- **(e) Hermes on `serve --port 0` plus URL discovery instead of the
  temp-port proxy recipe.** Rejected (D6): the proxy child speaks MCP over
  stdio with zero client changes, while the serve-direct shape needs an HTTP
  client the plugin would have to grow. The lease race is shared with the
  proxy's own auto-start and is documented, not new.
- **(f) Tests keep an internal stdio host seam.** Rejected (D5): a transport
  tests use that users cannot reach is dead production code with a
  maintenance cost. Tests pay the ephemeral-port price instead.
- **(g) `scripts/` as a follow-up with a tracking artifact.** Rejected (D9):
  the scripts break loudly at the cut (a removed flag is a parse failure, not
  a warning), and a loud break with no artifact in scope is how the
  fresh-install script becomes an incident.

## Evidence

Parse matrix (worktree build, `dotnet AiRaccoon.dll`, temp data roots):

- `--transport stdio` and `--transport=stdio` → exit 9, stderr carries the
  removal hint naming the proxy and `serve`; `--transport garbage` → exit 9
  with empty stderr, proving the hint fires only for the removed value.
- `serve --transport stdio` and `settings model show --transport stdio` →
  exit 15 with the unrecognized-argument text plus the same hint.
- `--transport https` → exit 9 with the `proxy|http` rejection.
- `--help` renders `--transport <proxy|http>`.
- bare `--transport http` → proxy path (exit 6 from the unpackaged muxer
  probe, no bank created), confirming D2's "still parses, still proxies".

D6 recipe proof (`/tmp/air-p4-d6-proof.py` driver, published worktree binary):
with 7721 held by a foreign backend on another data root, a bare child with a
temp `--data-root` and a leased `--port` completed initialize, a `memory_write`
plus `memory_search` hash round-trip, served from the leased port (not 7721),
and exited 0 on stdin close. The busy-port clause stands; no gap to strike.

Post-change grep: `grep -rin -- "--transport stdio" docs/ integrations/
scripts/ README.md` hits only history (`docs/adr/0020*`, `docs/plans/`,
`docs/work/`, `docs/reviews`) and this record's removal account. (`src/` keeps
the parse-rejected members and the P5-owned exit-6 line until that pass lands;
that tree is outside the grep's paths by design.)
