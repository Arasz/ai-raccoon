---
name: ai-raccoon-deployment-ops
description: "Use when operating ai-raccoon: rollouts, serve, watchdogs."
---

# ai-raccoon deployment ops

Operating facts and rollout mechanics for the installed `ai-raccoon` global tool and its
HTTP serve on this machine. All facts measured 2026-08-07 unless noted.

## Serve lifecycle (measured)

- `ai-raccoon serve` binds `127.0.0.1:7721`, MCP endpoint at `/mcp`. Background it with a
  log: `ai-raccoon serve --idle-timeout 0 > ~/.ai-raccoon/serve.log 2>&1 &` (detach with
  `setsid`/`start_new_session` so it survives the parent).
- **The idle watchdog default is 4 HOURS, not off.** `ServeRunner` resolves
  `DefaultOptions.IdleTimeout` (= 4 h) when no flag is given; `ServerConfig`'s record
  default of Zero only matters for the in-code config path. A serve started without
  `--idle-timeout` silently shuts itself down after 4 h without MCP traffic — an
  unexplained "the promotion host died overnight" is this. **`--idle-timeout 0` = permanent.**
- Only ONE process may own 7721; a second `serve` attaches to the existing one and exits 0.
- Background hosted services (extraction loop, bank maintenance, idle watchdog) run in the
  HTTP serve process. STDIO processes (per-session bridges) are session-bound: they get the
  maintenance service's startup/shutdown boundary checkpoints but no timers. The WAL bloat
  failure mode (431 MB of 100% checkpointable garbage, measured) happens when several
  stdio bridges linger and nothing truncates — fixed by the 1.1.1 maintenance service.
- CLI config verbs live under `settings`: `ai-raccoon settings extract enable true`, `ai-raccoon
  settings maintenance list`. **There is NO `config` verb** — `ai-raccoon config extract ...` fails
  with "Unrecognized command or argument 'config'" (hit 2026-08-07 in a rollout script). Pre-1.21.0
  builds took these root-level (`ai-raccoon extract enable true`); WP6 of the bank-open-cost plan
  moved them under `settings` with no compatibility alias.

## Version-rollout watchdog pattern

The scripts `~/.hermes/scripts/ai-raccoon-rollout-<ver>.py` (one per target version, cron
every 30 min) encode a reusable shape:

1. **Version gate**: `ai-raccoon --version` (parse `N.N.N` — output is `1.1.0+<sha>`
   SemVer). Below target → `dotnet tool update --all --no-http-cache -g` → re-check →
   still below → log + SILENT (watchdog discipline: no output in steady state).
2. **First run at target** (marker `~/.ai-raccoon/rollout-<ver>-done.marker` absent):
   kill whatever holds 7721 surgically (`lsof -ti :7721` → SIGTERM — never
   `pkill -f "ai-raccoon serve"`, that also kills scratch servers on other ports), start
   the new serve with `--idle-timeout 0`, assert auto-promotion settings ONCE
   (`ai-raccoon settings extract enable true` + `settings extract mode promote` — marker keeps later manual
   changes un-fought), then VERIFY the release's new features (see below), write the
   marker, print a summary.
3. **Steady state**: marker present → only ensure the serve is up (self-heal). Silent.
4. While waiting below target, keep the promotion host alive: if 7721 is free, start the
   CURRENT-version serve (no marker, no settings writes).

Feature verification for a release (make it part of the rollout, don't trust the version
string): the new CLI family answers (`ai-raccoon settings maintenance list` → exit 0), the
service's startup checkpoint logs in serve.log ("Bank WAL checkpoint complete", EventId
510) within ~60 s, and the WAL file is small after truncation.

## Cron-scheduler quirks (Hermes cronjob tool)

- `schedule: "every 30m"` can still record `"repeat": "once"` — and the job may NEVER fire
  (next_run_at in the past, last_run_at null). Verify with `cronjob list`; the script is
  the source of truth — run it manually to force a rollout.
- CLI sessions have no live-delivery channel: cron output is saved locally (viewable via
  `cronjob list`), never delivered. Diagnostics go to `~/.ai-raccoon/rollout.log` +
  `serve.log` regardless.

## Local QA deploy protocol (measured 2026-08-13, tar2)

For "deploy local version + full manual test" rounds:

1. **Bump +0.0.1 locally, uncommitted** (csproj PackageVersion/InformationalVersion/AssemblyVersion +
   src/AiRaccoon/.mcp/server.json root+package). The two VersionContractTests go RED in the
   worktree — expected, never commit the bump.
2. `dotnet build src/AiRaccoon -c Release -p:DOTNET_ENV=local` (packs into the repo's
   .nupkg-local feed). **`dotnet tool update -g ai-raccoon --version <same>` is a NO-OP when
   that version is already installed** — a rebuilt nupkg with new bits is NOT pulled. To
   redeploy same-version bits: `dotnet tool uninstall -g ai-raccoon` then `dotnet tool
   install -g ai-raccoon --version <v> --add-source <repo>/.nupkg-local`.
3. **Backup the live bank BEFORE testing:** `sqlite3 ~/.ai-raccoon/memory.db ".backup
   '<backup path>'"` (online-consistent even with writers) + integrity_check.
4. Manual matrix: every MCP tool (mcporter with `--header X-AiRaccoon-Token=<mcp-token>`,
   dedicated `manualtest-<id>` project, zero-residue verified at the end), every settings
   verb changed then restored, serve options on scratch ports. QA-doc every defect
   (docs/work/YYYY-MM-DD-<id>-qa.md); TDD fixes with a witnessed RED. An owner f: about a
   live regression means fix + redeploy immediately, not just document.
5. Killing/restarting the 7721 serve: a Hermes stdio bridge's BackendLauncher may spawn a
   fresh backend on 7721 between your kill and your start — a `serve --restart` can then lose
   the port race (exit 8) to a bridge-spawned `--quiet serve`; that's by design.

## Release-PR mechanics (this repo)

Version bump = TDD `VersionContractTests` RED (ExpectedVersion → new) then GREEN:
`src/AiRaccoon/AiRaccoon.csproj` (PackageVersion/InformationalVersion/AssemblyVersion),
`src/AiRaccoon/.mcp/server.json` (root + package version), and the
`AI_RACCOON_VERSION` pin in `scripts/manual-fresh-install-test.py`. Ship as a tiny PR from
current main. The owner merges fast and may REBASE+merge the feature branch remotely —
always `git fetch` first and compare `origin/<branch>` vs local HEAD before pushing
follow-ups; a merged PR orphans pushed commits (a version bump pushed after the merge
landed as an orphan — recovered via cherry-pick onto a fresh branch from origin/main).
