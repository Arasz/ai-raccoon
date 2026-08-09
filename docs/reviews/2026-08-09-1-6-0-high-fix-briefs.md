# 1.6.0 High-severity fix briefs

One brief per High finding from `2026-08-09-integration-review-1-6-0.md`. Each is written so an
agent that was not part of the review can execute it: what is wrong, how it was proven, what to
change, what to test, and what to assert.

**Read this first, it applies to every brief.**

- **Verify the finding before fixing it.** Each brief names how the defect was established —
  a code read, a live reproduction, or both. If a brief says "code read" and you cannot reproduce
  it, stop and report rather than changing code to satisfy a claim that may be wrong.
- **TDD is mandatory** (`.ai-badger/invariants/tdd-mandatory.md`). Write the failing test first and
  **paste its RED output** into your report. A test never seen red is not a test
  (`.ai-badger/invariants/prove-the-check-fails.md`).
- **Never touch `~/.ai-raccoon/`.** It is a live 2,829-entry bank serving every session on this
  machine. Build throwaway banks under `/tmp/<your-lane>/` and pass `--data-root` to every CLI call.
- **A live server runs on port 7721.** Do not start, restart or shut one down.
- **Do not fix findings outside your lane's file list.** Lanes are partitioned by file precisely so
  they can run at once; if the work seems to need a file you do not own, stop and report it.
- Run only the tests your change touches; CI owns the full suite
  (`.ai-badger/invariants/pipeline-runs-the-rest.md`).

Lanes are disjoint by file, so A, B and C can run concurrently. **H9 is a decision, not a patch —
do not implement it without a ruling.**

---

## Lane A — storage scope predicates (H2, H3, H4, H5)

**Files you own.** `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`,
`MemorySchema.cs`, `PromotionQueueSql.cs`, `SqliteMemoryStore.cs`,
`SqlitePromotionQueueStore.cs`, and their tests under `tests/AiRaccoon.Tests/Unit/storage/`,
`Unit/Promotion/`, `Integration/storage/`.
**Do not touch:** `SweepHostedService.cs`, `ExtractionHostedService.cs`, `BackgroundTelemetry.cs`
(lane B), `CliArgs.cs`, `BitwardenCliSecretManager.cs` (lane C).

These four defects are one root cause wearing four hats: **`hash` does not encode `scope`**
(`ContentHash.Of(path, value)`), so `(project_id, hash)` is not a unique row and every statement
that filters on that pair alone reaches rows in scopes it did not intend.

### H2 — the sweep's DELETE and `memory_set_ttl`'s UPDATE are scope-blind

**Established: live-reproduced.** Build a same-hash sibling (workspace row + project row), set a
TTL on the *project* hash, run one destructive sweep — both rows vanish and the workspace outbox
goes from 1 entry to 0, while the sweep reports deleting one candidate. Repro script:
`sibling_test3.py` in the review's scratch notes; the transcript is quoted in the review doc under
"The workspace-sibling repro".

**The two statements.**
- `MemorySql.DeleteByHashAndProject` — `DELETE FROM entries WHERE hash = @hash AND project_id = @projectId`
- `MemorySql.UpdateEntryTtl` — `UPDATE entries SET ttl_days = @ttlDays WHERE project_id = @projectId AND hash = @hash`

**How to fix.** Both need to act on the scope the caller meant. Evaluate before executing — there
are two defensible shapes and the choice is yours to argue:

1. *Add a scope predicate.* Give both statements a `scope` (and `workspace_id IS NULL`) filter and
   thread the intended scope from the caller. Narrow, but `DeleteAsync` is also the public
   `memory_delete` path, whose documented contract is "delete this entry" — check what that tool
   should do about siblings before you narrow it, and say what you decided.
2. *Make the sweep and TTL paths scope-aware without changing `DeleteAsync`'s contract*, by giving
   `SweepService` a delete that names the context it enumerated.

Prefer whichever leaves `memory_delete`'s documented behaviour intact. **Note the interaction with
H4:** if a sweep delete stops removing siblings, the ADR-0023 trigger's guard has to agree with the
new reality — do H4 in the same change, not separately.

**What to test, and what to assert.**
- *(red first)* A test that builds a workspace row and a project row sharing one hash, sets a TTL
  on the project hash, and asserts **the workspace row's `ttl_days` is still NULL**. This must fail
  before your change — paste that failure.
- A test that sweeps that same fixture and asserts the workspace row **still exists** afterwards
  and `memory_workspace_status` still reports its entry.
- A test that the project row *is* still deleted — the fix must not make the sweep inert.
- A custom/label-context sibling (`context` parameter on `memory_write`) gets the same two tests.
- Assert on **row identity**, not counts alone: query `(id, scope, workspace_id, hash)` so a test
  cannot pass because the wrong row survived.

### H3 — a workspace write silently lands in committed project scope

**Established: live-reproduced.** With content already committed to project scope,
`memory_write(projectId, workspaceId, content)` returns `"context": "project:<id>"` — not the
workspace the caller named — creates no workspace row, and leaves `workspace_status` at 0. The
write the agent scoped to a sandbox was committed instead, and `memory_workspace_discard` cannot
take it back.

**Where.** `SqliteMemoryStore.WriteAsync`'s dedup lookup (`MemorySql.SelectCommittedByValue`,
scoped `WHERE … workspace_id IS NULL`) runs before the workspace branch, so an existing committed
row short-circuits the workspace write and its hash/context are returned.

**How to fix.** The dedup lookup must not match a committed row when the caller named a workspace.
Evaluate: the narrow fix is to skip (or scope) that lookup when `request.WorkspaceId is not null`,
so the workspace gets its own row. Confirm the reverse ordering still behaves — writing to a
workspace first and then committing the same content already produces two rows correctly, and must
keep doing so.

**What to test, and what to assert.**
- *(red first)* Write content to project scope, then write identical content with a `workspaceId`.
  Assert the response's `context` is `workspace:<id>` and that `memory_workspace_status` reports
  **1** entry. Both assertions fail today — paste the failure.
- Assert a `workspace_id`-bearing row exists in `entries` after the second write.
- Assert `memory_workspace_discard` then removes it and the committed project row survives.
- Keep a test for the genuine dedup case: two identical *committed* writes still produce one row.

### H4 — the ADR-0023 trigger's guard is scope-blind

**Established: code read** (`MemorySchema.cs`, the `promotion_queue_entries_ad` trigger). Verify it
yourself before changing it.

The trigger keeps a `promotion_queue` row whenever *any* `entries` row survives for
`(project_id, hash)`:

```sql
AND NOT EXISTS (SELECT 1 FROM entries e
                WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash);
```

But `ShareAsync` resolves candidates with `WHERE hash = @hash AND scope = 'project' AND project_id = @projectId`
(`MemorySql.cs`). So a queue row whose hash survives only as a `custom`- or workspace-scoped
sibling is **kept, unpromotable, and destroyed as `stale-hash` on the next promote pass** — the
exact D7 data loss ADR-0023 claims to have closed.

**How to fix.** Align the guard with what `ShareAsync` can actually resolve — add
`AND e.scope = 'project'` to the trigger's `NOT EXISTS`. Apply the same alignment to
`MemorySql.RestoreQueueRowsStillBacked`, whose `EXISTS` is scope-blind while its paired
`CaptureQueueRowsForSourcePath` filters `workspace_id IS NULL` (an asymmetry that produces the same
unpromotable state through the re-ingest path).

**Migration matters here — and one common worry is already settled.** A live test proved the
trigger *does* reach existing banks: `MemorySchema.EnsureAsync` executes the whole `Ddl`
unconditionally on every read-write open, **before** the version ladder is consulted, so a bank
stamped at user_version 0, 1, 2 or 3 — even one predating the `promotion_queue` table — comes back
with the trigger present. Measured across all four stamps. So there is no "existing installs never
get the trigger" defect to fix.

**But that same mechanism is the trap for *this* change.** The DDL uses
`CREATE TRIGGER IF NOT EXISTS`, so **an edited trigger body will not replace the one already on
disk.** You must `DROP TRIGGER IF EXISTS promotion_queue_entries_ad` before recreating it, or every
existing bank keeps the broken guard forever while fresh banks get the fix — the worst possible
split. This is the single most important line in this brief. PR #246's own migration comment makes
the same distinction ("unlike `promotion_queue_entries_ad` above, which reaches every bank via
`IF NOT EXISTS`"), so the mechanism is established, not speculative.

> **Blocked on PR #246.** That PR is open and touches `MemorySchema.cs`, `PromotionQueueSql.cs`,
> `SqlitePromotionQueueStore.cs` and `PromotionQueueService.cs` — every file H4 and H5 need. It adds
> a `scorer_version` column and bumps `CurrentVersion` 3 → 4 with a `MigrateToV4Async` hard step. It
> does **not** touch the `NOT EXISTS` guard or the orphan predicates, so H4 and H5 remain open.
> **Do not start H4/H5 until #246 merges**, then rebase onto it — the version ladder it introduces is
> also the natural place to hang the `DROP TRIGGER` step.

**What to test, and what to assert.**
- *(red first)* Insert an `entries` row in `project` scope and a `custom`-scope sibling with the
  same hash, plus a matching `promotion_queue` row. Delete the project row. Assert the queue row is
  **gone**. Today it survives — paste that failure.
- **Prove the check can fail:** drop the trigger, repeat, assert the queue row survives. Paste both.
- A migration test: create a bank, `DROP TRIGGER promotion_queue_entries_ad`, close, reopen with the
  current code, and assert `sqlite_master` contains the trigger **with the new body** (assert on the
  SQL text containing `scope`, not merely on the name). Without this the fix ships only to fresh
  banks.
- Assert the existing behaviour still holds: a queue row whose project-scope entry is alive survives
  an unrelated delete.

### H5 — `extract prune` cannot see the orphans the trigger creates

**Established: code read** (`PromotionQueueSql.cs`, the orphan-count and delete statements).

Same predicate, same fix: the orphan definition must be "no `project`-scope entry backs this hash",
matching `ShareAsync`. Otherwise the one maintenance verb that exists is blind to the one orphan
class this release adds.

**What to test, and what to assert.**
- *(red first)* Build the H4 fixture (queue row backed only by a `custom`-scope sibling) and assert
  `extract prune` **reports it** as an orphan. It reports 0 today.
- Assert `--apply` removes it and a second run reports 0 (idempotence).
- Assert a queue row backed by a live `project`-scope entry is **not** reported — the prune must not
  become over-eager.
- While here: `SqlitePromotionQueueStore.PruneOrphansAsync` counts and deletes in two unsynchronised
  statements and reports the **pre-count** as "removed". Wrap them in one transaction and report the
  affected-row count from the DELETE. Assert the reported number equals the number actually removed.

---

## Lane B — the reaper's consent and its telemetry (H6, H7, H8)

**Files you own.** `src/AiRaccoon.Infrastructure/Degradation/SweepHostedService.cs`,
`src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs`,
`src/AiRaccoon/Observability/BackgroundTelemetry.cs`, `src/AiRaccoon/Access/ForgettingPolicyService.cs`,
and their tests under `tests/AiRaccoon.Tests/Unit/sweep/`, `Unit/Extraction/`, `Unit/Observability/`.
**Do not touch:** anything in `Sqlite/` (lane A) or `Setup/Cli/` (lane C).

### H6 — the reaper deletes from every project regardless of access mode

**Established: code read + live.** `memory_sweep(dryRun:false)` is `Destructive`, which
`AccessModePolicy` grants only in `full` mode; the default is `rw`. Live, the MCP call on an `ro`
project is refused with `access-denied: memory_sweep requires mode full (current ro)`. The reaper
calls `SweepService` with no gate at all, so the same project is reaped by the timer. The in-code
justification (`"a timer has no caller"`) is written about the *threshold read* and then silently
covers the deletes.

**This one needs a decision recorded, not just code.** Two defensible answers:

1. **Honour the mode.** Skip a project whose resolved access mode is not `full`. Consistent with
   the rest of the system, and makes the per-project mode a real opt-out.
2. **Exempt the reaper deliberately** — argue that retention is an operator policy, not an agent
   permission — and *document the exemption* next to the kill switch in
   `docs/reference/agent-memory-server.md`.

Pick one, implement it, and write the reasoning into the reaper ADR (see below). Silence is the one
unacceptable outcome. Related and worth fixing with it: `sweep.threshold` is a single **global** key,
yet `SetSweepThresholdAsync(projectId, …)` gates on `projectId` before writing it and
`GetSweepThresholdAsync` takes a `projectId` it never uses — either make the key per-project with a
global fallback, or drop the misleading parameter.

**What to test, and what to assert.**
- *(red first)* A bank with two projects, one `full` and one `ro`, both holding a sweepable entry.
  Run a reaper pass. Assert the `ro` project's entry survives (option 1) — this fails today.
- Assert the `full` project's entry is still deleted, so the reaper is not made inert.
- Assert the pass does not throw when a project's mode cannot be resolved.
- If you take option 2, the test instead asserts the documented behaviour *and* the doc line exists.

### H7 — no successful sweep pass ever emits a span

**Established: code read, corroborated live.** `BackgroundTelemetry`'s span filter defaults to
suppress and emits only when `NoteWork()` is called. `SweepHostedService` never calls it — verified
by searching `NoteWork` across `src`, which returns Watch, Extraction, BankMaintenance and
IdleWatchdog, but not Sweep. So the only destructive background job is the only one invisible in
traces, including on passes that delete rows. Live OTLP capture over 7.5 minutes recorded
`watch.reconcile` and `bank.maintenance` spans and no `sweep.reaper` span.

**How to fix.** Call `pass.NoteWork()` when the pass actually deleted something, and tag the span
with what it deleted. `RunPassAsync` does not currently take the telemetry scope — thread it in.
Do **not** span every pass unconditionally: the span-volume fix (`9ac3d543`) is deliberate and
correct, and a 24-hour reaper that no-ops should stay quiet. The rule is "span the passes worth
reading", and a pass that deleted user data is always worth reading.

**What to test, and what to assert.**
- *(red first)* A pass that deletes at least one entry emits exactly one `sweep.reaper` span
  carrying a `deleted` count > 0. Fails today — paste it.
- A pass that deletes nothing emits **no** span but still records the counter and duration. This is
  the negative case that proves you did not simply span everything.
- **Then strengthen the gate that missed this.**
  `tests/.../Observability/BackgroundInstrumentationCoverageTests` asserts each hosted service takes
  an `IOperationTelemetry` *constructor parameter* — a check that stayed green through this exact
  defect. Change it to assert an **emission** per service, then prove it can fail by removing your
  `NoteWork()` call and watching it go red.

### H8 — a pass where every project threw reports `result=success`

**Established: code read.** Per-project failures are caught inside the loop and logged, then control
reaches `pass.Succeeded()` unconditionally. The `ai_raccoon.background.passes{result}` failure rate
stays at 0% while nothing works. Affects `SweepHostedService` and `ExtractionHostedService`.

**How to fix.** Count per-project failures in the pass and let the outcome reflect them. Evaluate
the shape: a `failures` tag plus a distinct `result` value is less disruptive than failing the whole
pass, and keeps the "one bad project must not stop the others" property that the per-project catch
exists to provide. Do not remove that catch.

**What to test, and what to assert.**
- *(red first)* A pass over three projects where all three throw. Assert the recorded `result` is
  **not** `success`. Fails today.
- A pass where one of three throws: assert the other two were still processed **and** the failure is
  visible in the span/metric.
- A clean pass still records `success` with no failure tag.

### Also in this lane: the reaper ADR

The largest behavioural change in the release — a default-on, unattended, cross-project deleter on a
machine-wide shared backend — shipped with no ADR, while a no-behaviour-change contract restatement
got ADR-0024. Write `docs/adr/00NN-the-sweep-reaper.md` covering: on-by-default vs off-by-default;
why the kill switch is global-only; the scope of `sweep.threshold`; your H6 ruling; and the
reaper ↔ promoter interaction (a reaper delete fires the ADR-0023 trigger and drops queue rows; if
`PromoteAsync` already claimed one, the candidate is lost and logged only at Debug). Add the row to
`docs/adr/README.md`.

Two facts to record in it, both verified during the review and both load-bearing for the decision:
- **On an existing bank the reaper is armed but inert.** Every candidate needs a non-NULL
  `ttl_days`, and 1.5.1 had no MCP tool or CLI verb that could set one. Measured: a bank with
  entries aged 3,650 days and ratings 37 orders of magnitude below the threshold swept **nothing**.
- **`ParseEnabled` is fail-open** — `!string.Equals(value, "false")`, so `'0'`, `'no'` and any
  garbage read as *enabled*. For a destructive default-on job, consider whether that is the
  direction you want and say so either way.

---

## Lane C — CLI option isolation and the Bitwarden token (H1, H10)

**Files you own.** `src/AiRaccoon/Setup/Cli/CliArgs.cs`,
`src/AiRaccoon.Infrastructure/Encryption/BitwardenCliSecretManager.cs`, and their tests under
`tests/AiRaccoon.Tests/Unit/Setup/`, `Unit/Encryption/`.
**Do not touch:** `Sqlite/` (lane A) or the hosted services (lane B).

### H1 — a bad value on any top-level option silently discards `--data-root`

**Established: live-reproduced twice, independently.** A throwaway bank was set to access mode `ro`;
the live bank's is `full`.

```
$ ai-raccoon --data-root /tmp/verify-dr access default show
ro
$ ai-raccoon --data-root /tmp/verify-dr --install-scope garbage access default show
full            <- the LIVE bank's value.  exit 0, no warning
$ ai-raccoon --data-root /tmp/verify-dr --transport garbage access default show
full            <- same
```

**Why.** `CliArgs.ReadOptions` reads every top-level option inside **one** `try`. An unparseable
enum/int throws from that one accessor and the surrounding `catch` resets the **entire**
`CliOptions` record to defaults — including a `--data-root` that parsed perfectly. `TryParse`'s
success gate only inspects `parseResult.Errors`, which stays empty because System.CommandLine does
not eagerly validate enum coercion, so the command proceeds against `~/.ai-raccoon`.

**Severity is about what it can point at.** `ai-raccoon --data-root /tmp/test --transport htp sweep enable`
arms the reaper on the real bank while appearing to configure a sandbox.

**How to fix.** Parse each top-level option independently so a bad value falls back to *that
option's* default and cannot discard its siblings. Better still, evaluate whether an unparseable
top-level option should refuse the whole invocation with a non-zero exit — that is the behaviour a
misplaced `--data-root` already gets (`Unrecognized command or argument '--data-root'`, exit 1), so
refusing is the consistent choice and silence is the anomaly. Argue whichever you pick.

**What to test, and what to assert.**
- *(red first)* Assert that parsing `["--data-root", "/tmp/x", "--install-scope", "garbage", "access", "default", "show"]`
  either yields `DataRoot == "/tmp/x"` or fails the parse — and specifically that it does **not**
  yield the default data root with a success result. Fails today.
- The same for `--transport garbage` and `--port garbage`. This project has a documented
  System.CommandLine gotcha: **test real argv through `CliArgs.TryParse`, not just the handler**.
- Assert a *valid* combination is unaffected, and that a bad value on its own (no `--data-root`)
  behaves as it does today or refuses — state which and why.
- Assert the exit code. A refusal that exits 0 is itself a finding.

### H10 — the Bitwarden access token is passed in the child process's argv

**Established: live-proven** with a slowed stand-in `bws`, captured from `ps aux`:

```
/bin/sh .../fakebin/bws secret get 11111111-…-111111111111 -t FAKE-PS-VISIBLE-TOKEN-XYZ123
```

`BitwardenCliSecretManager` does `ArgumentList.Add("-t"); ArgumentList.Add(token)`, so the token is
readable by any same-user process for the life of the call.

**How to fix.** Pass it the way the default path already does — as an environment variable on the
child `ProcessStartInfo` (`BWS_ACCESS_TOKEN`), never as an argv element. The mechanism already
exists in the same class; the `-t` path just does not use it.

**What to test, and what to assert.**
- *(red first)* A test that inspects the constructed `ProcessStartInfo` and asserts **no element of
  `ArgumentList` equals the token** and that `Environment["BWS_ACCESS_TOKEN"]` does. Fails today.
- Assert the token does not appear in any log message or exception text on the failure paths.
- Keep the existing behaviour tests green: a per-run token still authenticates, and a missing token
  still refuses cleanly without writing a sidecar.
- `no-hardcoded-secrets` and `no-hand-rolled-crypto` both apply; do not change the HKDF derivation.

---

## H9 — rating decays only on access

> **RULED, 2026-08-09.** The owner's ruling: the reaper exists to prune memories that are *not
> used*, and memories should **decay** rather than disappear after a fixed time — a real decay
> algorithm belongs in the product. That turns H9 from a bug fix into a design change, specified in
> **`2026-08-09-h9-decay-design-brief.md`** — read that, not the paragraphs below, which are kept
> only as the record of what was found.
>
> Headline from the design brief: `RatingPolicy.Rating(...)` has exactly **one** call site (the
> search-hit bump), the rating is read in only two places, and it has **no influence on search
> ranking at all**. A decay formula exists; a decay system does not. Measured on the live bank,
> the change lands with **zero immediate deletions** (0 rows carry a TTL, max idle 3.9 days, and
> the threshold crosses at ~22 idle days), so now is the safest moment to make it — but it must be
> sequenced after H2 and H6, and after in-flight lane A1, which owns two of the files.

`rating` is written only by `BumpAccessAsync`, which runs when an entry is returned by a search. An
entry nobody searches keeps the column default `0.5`, above the `0.3` threshold, forever — so
`ShouldDegrade`'s `rating < threshold` is never true and **a TTL on an unread entry never fires**.
That is precisely the population a retention reaper exists to collect, so the feature is inert for
its main use case. Measured: entries aged 3,650 days with never-accessed ratings survived a
destructive pass.

Why it must not be fixed first: making rating decay with time would **increase the reaper's blast
radius** immediately, and the reaper currently deletes without per-project consent (H6) and outside
the scope it selected from (H2). **Fix H2 and H6 first; then decide H9.**

The options, for whoever rules on it: leave decay access-driven and correct the `memory_set_ttl`
description (which currently tells agents decay is a matter of time); or make rating a function of
age as well as access, and re-derive the default threshold against a real bank before shipping it.
A related honesty problem rides along: `DegradationPolicy.CanEverExpire` claims "ever" but computes
"right now", and that field goes on the wire to agents.

---

## Suggested order

Lanes A, B and C are file-disjoint and can run at once. Within the whole set:

1. **H1** (lane C) — smallest fix, widest blast radius, and it makes every sandbox-based test on
   this machine trustworthy again.
2. **H2 + H4 + H5** (lane A) — one root cause; do them as one change so the trigger guard and the
   delete agree. **Remember the `DROP TRIGGER` migration line.**
3. **H3** (lane A) — workspace isolation.
4. **H6** (lane B) — the reaper's consent, plus the ADR.
5. **H7 + H8** (lane B) — telemetry honesty, and strengthen the coverage gate that missed H7.
6. **H10** (lane C) — secret hygiene.
7. **H9** — only after a ruling.

Re-run the gate on the **merged** result, not on each lane's own tree: each lane measured a
different worktree, and these changes interact (a scope-aware delete changes what the reaper does,
which changes what the telemetry tests observe).

Finally: **the reaper is currently disarmed on the live bank** (`ai-raccoon sweep disable`), done as
a precaution before this review cycled the backend onto the candidate. Re-arm with
`ai-raccoon sweep enable` once H2 and H6 have landed and been verified.
