# Lane report — Consumer surface, CLI + MCP (2026-08-21 delta campaign)

Lane: consumer surface · Base: `155f281e` · Read-only + live CLI probes on scratch data-roots
(real bank untouched) · 10 findings — 1 HIGH, 2 MEDIUM, 2 LOW, 5 NIT; 7 MEASURED, 3 READ.
Three briefed leads disproven (F5 endpoint, F7 missing how-to; F3's first observation was the
lane's own artifact and was self-withdrawn).

### F1 — README "What's new" is missing the 1.29.0 feature while VERSION says 1.29.0 [READ]
**Severity:** MEDIUM — `README.md:34` top entry is 1.28.0; #404's arbitrary-model feature has no What's-new entry. Confirmed drift lead.

### F2 — Settings-server auto-start fails entirely when the binary runs as `dotnet AiRaccoon.dll` / `dotnet run` [MEASURED]
**Severity:** HIGH
**Evidence:** Live probe: settings verb → `backend … did not answer within 30s (serve exit 1)`, EXIT=18, reproducible every port; manual `serve` with identical flags works. Cause: `BackendLaunchArguments.Executable()` returns `Environment.ProcessPath` (`BackendLaunchArguments.cs:14`) — the dotnet driver when unpackaged — so the child is `dotnet --data-root … serve`, which exits 1 instantly. The launcher drains the child's stderr (`BackendLauncher.cs:139-146`), so the operator never sees why. Every server-mediated CLI verb (settings/repair/model set/noise entries/watch registered/extract prune) plus stdio-proxy auto-start is dead on this invocation shape. Added in the delta (#354); no test covers `Executable()`.

### F3 — Server-side faults on the settings channel exit 15 (InvalidArgument) with raw HTTP text [MEASURED]
**Severity:** MEDIUM — a 500 prints `Response status code does not indicate success: 500` and exits 15 (`ServerSettingsStore.cs:199` → `ConfigCommands.cs:148`). Exit-code doc promises "you mistyped" vs "the bank/server is broken"; server faults land in the mistyped bucket.

### F4 — doctor's exit codes 19/20 work as documented; a missing bank exits 0 [MEASURED]
**Severity:** LOW — v99 bank → EXIT=20 with actionable text; shape mismatch → EXIT=19; healthy → 0. Residue: no-bank-at-all prints `no bank to check` and exits 0 (`DoctorCommands.cs:22-26`, deliberate but uncommented) — wrong `--data-root` reads as healthy.

### F5 — Ground-truth lead wrong: `model download` has no `--endpoint` override [MEASURED]
**Severity:** LOW — live probe: unrecognized option, EXIT=15. The endpoint is a constructor-injection test seam only. Users cannot point the downloader at a mirror/proxy.

### F6 — memory_performance live: 27 tools exposed, tool works, default payload ~4,900 zero buckets [NIT]
Reflection-derived inventory matches ground truth exactly; zero-data default response is verbose but harmless.

### F7 — How-to coverage for the download/migration flow exists; "missing how-to" lead disconfirmed [READ]
**Evidence:** `docs/how-to/configure-embedding-engines.md` Recipe 4 covers download → manifest → model set + re-embedding lifecycle with measured timings and a sequence diagram. Only README What's new lags (F1).

### F8 — `model set local` help hides the manifest requirement its own error enforces [NIT]
Help says "optional path overrides it"; actual failure on a manifest-less dir produces excellent actionable text — but you must fail once to learn the rule.

### F9 — New settings/performance subcommands follow existing render conventions; validation errors actionable [MEASURED]

### F10 — Launch-path exit-code contract verified: unknown verb=9, bad subcommand=15; server.json single-sourced from VERSION at pack time [MEASURED]

## Still open
- Whether the global-tool apphost path ever hits F2 (if not, F2 downgrades to dev-invocation-only).
- Whether any CI leg exercises the CLI against a real spawned backend (F2/F3 would surface immediately).
- The lane withdrew its own first noise-entries-500 observation after tracing it to its own db-swap during testing — recorded as a withdrawn false positive.

## Owner questions
- Should auto-start support unpackaged invocations, or fail with instructions instead of "serve exit 1"?
- Should a server-side 5xx get its own exit code instead of InvalidArgument=15?
- Should doctor distinguish "no bank" from HEALTHY?
- Is skipping What's-new in #404 a one-off, or does the release checklist need a README gate?
