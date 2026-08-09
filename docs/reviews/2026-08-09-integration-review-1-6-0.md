# Integration review — AiRaccoon 1.6.0

**Date:** 2026-08-09
**Range:** `v1.5.1..68fd411e` — 17 commits, 176 files, +6,948 / −1,273.
**Binary under test:** the release candidate packed locally as **1.5.3+68fd411e** (below 1.6.0 so the
eventual nuget publish supersedes it) and installed as the global tool, with the shared `:7721`
backend cycled onto it. `/observability` reported `1.5.3+68fd411e1031bf58cf2b0dc962bc0766d3ed09b1`,
confirming the running server rather than just the CLI.
**Method:** eight parallel review lanes over the diff (core domain, tool surface, observability,
promotion/storage, sweep/TTL/workspace, serve/token/proxy, docs, architecture), plus a live manual
test driven over raw JSON-RPC against the running backend, plus four isolated-environment test
lanes. Every lane was worktree-isolated and read-only against production code.

## Verdict

**Do not ship 1.6.0 as it stands.** The release is well built — the promotion data-loss fix works
on the wire, the span-volume fix works on the wire, the docs are the most accurate they have been,
and the token gate is correctly scoped. But it introduces an **unattended deleter of user data**
(`SweepHostedService`) that is on by default, ignores per-project access modes, deletes outside the
scope it selected from, and emits no span when it deletes. Separately, the ADR-0023 trigger that is
this release's headline fix **does not close the data-loss window it was written for**.

A third defect, found by the live test rather than the diff, is arguably the most dangerous of all
because it is not in the new code at all: **a typo in an unrelated top-level flag silently discards
`--data-root` and runs the command against the live bank, exit 0** (finding 0). It is pre-existing,
but 1.6.0 is the release that adds destructive verbs worth pointing at the wrong bank.

Two mitigating facts, both verified rather than assumed:

- **Nothing is deletable on an existing bank today.** Every sweep candidate needs
  `ttl_days IS NOT NULL`, and 1.5.1 had no MCP tool or CLI verb that could set one. On upgrade, the
  reaper is armed and finds nothing. The exposure begins the first time anyone calls
  `memory_set_ttl`.
- **The live bank came through this review at exactly 2,829 entries**, the count it started at.

That is the best possible moment to fix this: the defects are latent, not yet realised.

## Findings

Severity is about consequence, not effort. High = data loss, a security boundary, or a claim the
release makes that is false.

| # | Sev | Area | Finding |
|---|-----|------|---------|
| 0 | **High** | CLI *(live)* | **A bad value on any top-level option silently discards `--data-root` and runs against the default bank, exit 0.** `CliArgs.ReadOptions` reads every top-level option inside one `try`, so an unparseable enum/int throws from that one accessor and the `catch` resets the *entire* options record — including a `--data-root` that parsed fine. Reproduced independently against a throwaway root holding a distinctive value: `--data-root /tmp/verify-dr access default show` → `ro`; add `--transport garbage` or `--install-scope garbage` → `full`, which is the **live** bank's value. Exit 0, no warning, both options. So `ai-raccoon --data-root /tmp/test --transport htp sweep enable` arms the reaper on the real bank while appearing to configure a sandbox. |
| 1 | **High** | Promotion | The ADR-0023 trigger's `NOT EXISTS` guard is **scope-blind** while `ShareAsync` requires `scope='project'`. A queue row whose hash survives only as a `custom`- or workspace-scoped sibling is kept by the trigger, is unpromotable, and is destroyed as `stale-hash` on the next pass. This is the exact D7 loss the ADR claims is gone. Reachable with no unusual action via `memory_delete_context` and via sync tombstones. |
| 2 | **High** | Promotion | `extract prune` uses the same scope-blind predicate, so the one maintenance verb that exists cannot see or clean the orphan class the trigger newly creates. |
| 3 | **High** | Sweep | The reaper never consults per-project access mode. `memory_sweep(dryRun:false)` is `Destructive`, which the tool surface grants only in `full` mode — default is `rw`. The operation the tool refuses on a default install now runs unattended, including against projects explicitly pinned to `ro`. |
| 4 | **High** | Sweep | The sweep selects **project-scope** rows but deletes by `(hash, project_id)` with no scope predicate, so it also destroys same-hash rows in an **active workspace** and in custom/label contexts. `memory_set_ttl` has the same blindness on the write side: it stamps the TTL on every sibling. One `set_ttl` plus one tick can take out an in-flight workspace note. |
| 5 | **High** | Observability | `sweep.reaper` never calls `NoteWork()`, and the span filter defaults to suppress. **No successful sweep pass ever emits a span — including one that permanently deletes rows.** The only destructive background pass is the only one invisible in traces. |
| 6 | **High** | Observability | A pass in which *every project threw* records `result=success`. The failure-rate signal stays at 0% while nothing works. Affects extraction and sweep. |
| 7 | **High** | Core | `rating` decays only when an entry is returned by a search — it is written solely by `BumpAccessAsync`. An entry nobody searches sits at the `DEFAULT 0.5`, above the 0.3 threshold, forever. So a TTL on an unread entry **never fires**, which is precisely the population the reaper exists to collect. The tool description tells the agent decay is a matter of time; it is a matter of access. |
| 8 | **High** | Architecture | The largest behavioural change in the release — a default-on, unattended, cross-project deleter on a machine-wide shared backend — shipped with **no ADR**, while a no-behaviour-change restatement of an existing contract got ADR-0024. |
| 9 | Med-High | Sweep | `sweep.threshold` is a single **global** key, but `SetSweepThresholdAsync(projectId, …)` gates access on `projectId` and then writes it. An agent holding `full` on project A moves a knob the reaper applies to project B. `GetSweepThresholdAsync` takes a `projectId` it never uses. |
| 10 | **Med** | Observability *(live)* | Every typed refusal exports `exception.stacktrace` over OTLP, including **absolute source paths of the build machine**. Measured: 30 stack frames, 52,968 bytes — **36% of all exported trace volume** — in a 7.5-minute window. Refusals are normal control flow here (unknown hash, invalid params, out-of-scope path), so this is the steady state, and for a hosted collector those paths leave the machine. |
| 11 | **Med** | Tools *(live)* | `memory_ingest_file` and `memory_ingest_directory` still leak an **untyped** `"An error occurred invoking '<tool>'."` when the path does not exist. This is the 1.5.0 sweep's D3 class, on two more tools; ADR-0024's rollout did not reach the IO exception classes. |
| 12 | **Med** | Tools *(live)* | The `confirm=true` gate on `autoPromote` is **bypassable**: `mode=promote` performs the same cross-project sharing with no gate at all. Verified live — `mode=promote, limit=2` promoted two entries into the shared tier with no confirmation. |
| 13 | **Med** | CLI *(live)* | `ai-raccoon watch enable <project>` **disables** watching — the boolean argument is optional and defaults to false. Mirror-image: `watch disable <project> true` **enables** it, because `disable` accepts a `<true\|false>` it then ignores. |
| 14 | **Med** | Promotion | `catch (… when ex is not OperationCanceledException)` re-opens silent permanent loss: the row is already claimed when `ShareAsync` throws OCE, so on every graceful shutdown landing mid-promote the candidate is gone from the queue, never shared, **not logged and not in `Failures`**. |
| 15 | **Med** | Promotion | The ADR's flagship prove-the-check-fails test pins the wrong invariant — it inserts two `custom`-scope rows and no `project`-scope row, then asserts the candidate survives. That candidate can never be promoted. The test cited as proof of the guard is proof of finding 1. |
| 16 | **Med** | Storage | The trigger persists into a **downgraded** install where its compensating code does not. A 1.5.1 binary opening a bank a 1.6.0 binary touched inherits the trigger while its `ReplaceFileAsync` has no capture/restore, so every watched-file re-ingest silently drops candidates. |
| 17 | **Med** | Sweep | A watched-file re-ingest silently destroys `ttl_days` and `rating`. The release added capture/restore for `promotion_queue` rows across the replace but not for the entry's own TTL, so `memory_set_ttl` on a doc-derived chunk stops meaning anything the next time the file is saved. |
| 18 | **Med** | Sweep | "Interval changes apply live, no server restart needed" is false in the direction that matters: the interval is re-read *after* a tick fires, so correcting `8760` to `1` waits up to a year. Documented in the help text and the reference doc. |
| 19 | **Med** | Serve | The Bearer envelope — the entire point of `fa05a66c` — has **no wire-level test**; its only automated coverage is `DefaultHttpContext` unit tests, and the plan that authorised it named the E2E test as a deliverable. *(This review exercised it live: it works — see below.)* |
| 20 | **Med** | Serve | `McpTokenGate.GuardedPaths` is a hand-maintained list whose default is **open**. The next `MapX(...)` added is unauthenticated by default, silently, with nothing comparing the two sides. |
| 21 | **Med** | Tools | `memory_set_ttl` accepts a hash in any scope the project owns and returns `canEverExpire: true` for rows `memory_sweep` can never reach; the two tool descriptions directly contradict each other for that input. |
| 22 | **Med** | Tools | `memory_set_ttl` has **zero wire-level coverage** — added to the expected-tool-name list but never invoked in the round-trip test — and all three of its tests seed `full` access first, so deleting *both* of its `Destructive` guards leaves the suite green. |
| 23 | **Med** | Core | Mapping the **base** `ArgumentException` to `invalid-argument` launders internal BCL faults into an Information-level, caller-blaming refusal. Reachable: two identical workspace writes leave two rows with one hash, and consolidate's `ToDictionary` then throws — the workspace becomes permanently unconsolidatable while the agent is told its arguments were wrong. |
| 24 | **Med** | Scripts | `triage-coredump.py` catches `(OSError, ValueError)` but not `struct.error`, which is what a truncated dump raises — so one bad dump aborts the loop and hides every later one, contradicting the code's own comment. |
| 25 | **Med** | Concurrency | The 1 Hz watch reconcile does ~15 unpooled bank connection opens per second, permanently, on a backend ADR-0020 just made always-on and machine-wide. |
| 26 | Low | Various | 15 further Low findings across the lanes: sweep kill switch is global-only and not honoured mid-pass; `PruneOrphansAsync` reports a pre-count as "removed"; `IsDuplicate` is O(N·M) with a per-candidate allocation; consolidation drops the new workspace provenance; `sweep show` output shape changed (breaking for scripts); `serve --restart --port 0` restarts nothing and exits 0; plain `serve` attaches to an unidentified listener while `--restart` refuses one; the Hermes client whitelists `::1` which nothing ever binds; five agent-facing descriptions name parameters that do not exist on the wire (`dry_run`, `workspace_id`, `memory_list_files`); `memory_share_extract`'s new `failures` field is undocumented; a shared entry from an absolute path gets a double slash (`shared//tmp/...`); `memory_ingest_file` pointed at a directory returns success-shaped `{"indexed":0}`; `ForgettingPolicyService.SetSweepThresholdAsync` has no range check while its sibling gained one; `memory_set_ttl` is gated `Destructive` twice; `ExtractCommands`' store dependency is optional-with-a-runtime-guard. |
| 27 | Low | Docs | One verifiably-false doc claim in the whole surface: `docs/reference/agent-memory-server.md:53` omits `memory_promotion_list`'s `includeFullValue` parameter. Pre-existing, survived a 220-line rewrite of the file. |

## What the live test proved works

This half matters as much as the findings — several of these are claims the release makes that
nobody had checked on the wire.

- **The D7 promotion fix holds live.** `memory_share_extract(mode=promote, limit=2)` returned
  `promotedHashes: 2, skippedDuplicates: 0, failures: []` with conservation on the bank. The
  claim-then-share loss this release exists to fix did not occur.
- **The span-volume fix works, measured.** Over a 448-second capture window — ~447 passes of the
  1 Hz `watch.reconcile` — exactly **2** `watch.reconcile` spans were exported. The suppression is
  real and the counter still moves on the suppressed path.
- **OTLP export is genuine.** 67 POSTs captured (`27` traces, `40` metrics), all
  `application/x-protobuf`, 568 B–46 KB. Span names conform to the MCP semantic convention
  (`tools/call memory_search`, …) and `mcp.method.name`, `gen_ai.tool.name`, `service.name`,
  `service.version` are all present on the wire.
- **The `fa05a66c` Bearer path works** — this review drove the entire sweep over
  `Authorization: Bearer <loopback token>` on the direct-HTTP route, which finding 19 notes has no
  automated wire coverage. It authenticates correctly.
- **ADR-0024's contract holds on the wire.** Removal verbs are idempotent and report a count
  (`memory_delete`, `memory_promotion_discard`, `memory_delete_context` → `{"deleted":0}`);
  transitions refuse typed (`memory_share`, `memory_set_ttl` → `unknown-hash`). All four workspace
  tools agree on `unknown-workspace` — 1.3.1's D2 stays fixed.
- **The 1.5.0 sweep's argument-binding leak is fixed.** `memory_write` with an undeclared parameter
  now returns a typed `invalid-argument` instead of the untyped exception it leaked at 1.5.0.
  (Finding 11 is the *IO* exception class, which the fix did not reach.)
- **`memory_set_ttl` validates its range correctly** — `0`, `-5` and `99999` all refuse with
  `invalid-params: ttlDays must be between 1 and 36500, or null to never expire`.
- **Watch reconciles all three file events** within 8 seconds: an appended file, a newly created
  file, and a deleted file all reached the index correctly, and `watch_status` moved
  `Scanning → Healthy` with a `lastSync`.
- **Path containment holds.** `/tmp/…/../../etc/hosts` normalises to `/etc/hosts` and is refused;
  an unscoped project refuses every ingest.
- **`memory_sync` refuses cleanly** when unconfigured, naming the command that would configure it.
- **The tool inventory is 23 on the wire**, matching every documented count.

## Every prior defect, re-tested against the candidate

The 1.3.1 and 1.5.0 sweeps (`docs/reviews/2026-08-08-manual-tool-sweep-1-3-1.md`,
`docs/reviews/2026-08-09-manual-tool-sweep-1-5-0.md`) left seven numbered defects. All seven were
re-tested directly against the running candidate, not inferred from the diff.

| # | Defect | Status on 1.6.0 | Evidence |
|---|--------|-----------------|----------|
| D1 | `memory_workspace_consolidate` schema/description mismatch | **Fixed** | `keep: ["all"]` → `{"promoted":1,"discarded":0}` |
| D2 | Workspace family — four answers for an unknown workspace | **Fixed** | `status`, `discard`, `consolidate` and `write` all return `unknown-workspace: Workspace '…' does not exist for project '…'` |
| D3 | Unknown-hash asymmetry (`memory_delete` vs `memory_share`) | **Ruled, not a defect** | ADR-0024 makes the split intentional: removals report a count, transitions refuse typed. Both behave as the ADR says. |
| D4 | `memory_stats` leaking every project's contexts | **Fixed** | A fresh probe project reports `"contexts":["shared"]` only |
| D5 | A one-entry shared tier capturing every `scope=all` search | **Fixed** | Three off-topic queries against the real 2,829-entry bank returned 5 results each, **0** from the shared tier |
| D6 | `consolidate` reporting promoted entries as also discarded | **Fixed** | `{"promoted":1,"discarded":0}` |
| D7 | `memory_share_extract(mode=promote)` losing candidates | **Fixed mechanically** | `promotedHashes: 2, skippedDuplicates: 0, failures: []`, conservation held. But findings 1/2/14 above are *new* loss paths in the same code, so D7's class is narrowed rather than closed. |
| — | 1.5.0 addendum: `memory_write` leaking an untyped exception on an undeclared parameter | **Fixed** | now `invalid-argument: The arguments dictionary is missing a value for the required parameter 'content'` |
| — | Same class, IO exceptions | **Still open** | finding 11 — `memory_ingest_file` / `memory_ingest_directory` on a missing path |

## Release decision

Three of the High findings (1, 2, 15) are one change: align the trigger guard, the prune predicate
and `RestoreQueueRowsStillBacked` with what `ShareAsync` can actually resolve, and fix the test that
pins the wrong state. Two more (3, 4) are one change: give the sweep a scope-aware delete and decide
explicitly whether the reaper honours access modes. Finding 5 is one line plus a test that can fail.

The cheapest safe path to a shippable 1.6.0, in order:

0. **Finding 0** — parse each top-level option independently so one bad value cannot discard a good
   `--data-root`. This is the smallest fix on the list and the one with the widest blast radius: it
   is the only defect here that can point a *destructive* command at the wrong bank, and it makes
   every sandbox-based test on this machine — including three lanes of this review — conditionally
   untrustworthy.
1. **Findings 4 and 21** — scope predicates on `DeleteByHashAndProject` and `UpdateEntryTtl`. These
   are the only findings that lose data a user never asked to expire.
2. **Findings 1, 2, 15** — the scope-blind trigger guard and its mistaken test.
3. **Finding 3** — honour per-project access mode in the reaper, *or* document the exemption next to
   the kill switch. Either is defensible; silence is not.
4. **Findings 5, 6** — `NoteWork()` in the sweep pass, and stop reporting an all-failed pass as
   success. Then strengthen `BackgroundInstrumentationCoverageTests`, which asserts a constructor
   parameter rather than an emission and so stayed green through finding 5.
5. **Finding 8** — write the reaper ADR while the reasoning is fresh.
6. **Findings 10, 11, 12, 13** — the live-test defects; each is small and each is user-visible.

Finding 7 (rating decays only on access) is the one that deserves a decision rather than a patch:
it makes the whole TTL feature inert for the population it targets, and fixing it *increases* the
reaper's blast radius, so it should not be fixed before findings 3 and 4 are.

## Method notes

- The reaper was **disarmed on the live bank** (`ai-raccoon sweep disable`) before cycling the
  backend onto the candidate, because `sweep show` reported `enabled: True` from defaults with no
  configuration ever set. Re-arm with `ai-raccoon sweep enable` once findings 3 and 4 land.
- All mutating live calls ran against a throwaway project (`manual-160-check`). Its entries, its
  three shared-tier promotions, its watch registration and its ingest scope were all removed
  afterwards; the shared tier is back to its single pre-existing row and the `ai-raccoon` bank is
  back to 2,829 entries.
- Promotion *scoring and ranking quality* was deliberately out of scope for every lane — a parallel
  task owns it. Promotion *correctness* was in scope and is where findings 1, 2, 14 and 15 come from.
- Restarting the shared `:7721` backend affects every concurrent session on this machine;
  ADR-0020's probe-and-respawn absorbed it with no session needing recovery.
