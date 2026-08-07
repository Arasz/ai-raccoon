# Cross-session agent coordination — two Claude Code sessions, one repository

Date: 2026-08-07. Status: raw material for an article — not project reference
documentation. Facts below are drawn from the sessions' own record; code claims
were re-checked against the worktree on 2026-08-07 (see "Verification notes").

## The mechanism, stated correctly first

The request that produced this note assumed the two sessions coordinated "by
using common memory." **They did not — and getting this wrong would misrepresent
the whole afternoon.** Two different mechanisms were involved, and they solved
two different problems:

- **Messaging carried the coordination.** Peer discovery via a `ListAgents`
  tool, then messages addressed point-to-point to a unix domain socket
  (`uds:/tmp/cc-socks/46045.sock`). Synchronous-ish, ad hoc, voluntary.
- **The shared memory bank (AiRaccoon — this project) carried evidence and
  durability**, in two separate roles that are easy to conflate with each
  other and with the messaging channel:
  1. A `PreToolUse`-style hook enforced a memory-first gate: repo text search
     (`grep`/`find`) was blocked until a `memory_search` had run. That forced
     search is what returned `docs/adr/0002-opentelemetry-observability.md` as
     a decisive hit — before either session had written a line of exporter
     code.
  2. The peer session used `memory_write` to durably record an amendment
     against ADR 0009 so the finding outlives its own session.

Neither role is "the channel between the sessions." Conflating them is the
easy mistake an article could make.

## Setting

Five-plus concurrent Claude Code sessions on one machine, one repository, each
in its own git worktree. Two interacted:

- **Session A** (this one): task `add-pid-to-serve`, branch
  `task/add-pid-to-serve`, worktree `.ai-badger/worktrees/add-pid-to-serve`.
  Adding `ai-raccoon serve observability <counters|trace|otlp|pid>` plus an
  OpenTelemetry/OTLP exporter.
- **Session B**: a "full-project-review" session running a 7-expert review,
  with four lanes as separate worktrees/branches under `.claude/worktrees/`:
  `task/sync-strip-settings`, `task/observability-truthfulness`,
  `task/dead-code-layer-edges`, `task/docs-accuracy-sweep`.

Session B opened contact unprompted, asking four questions: what are you
working on, which files are you touching, what's already landed, and what
defects have you found that it shouldn't schedule separately.

## Incidents

### 1. ADR numbering collision, caught before either side wrote a file

Session B's docs lane was scoped to write three new ADRs starting from 0008.
Session A had just created `docs/adr/0008-live-pid-discovery-for-monitoring.md`
and `docs/adr/0009-otlp-export.md` (both present in the tree, and indexed in
`docs/adr/README.md`). B relocated to 0010/0011/0012 and committed to adding
its `docs/adr/README.md` index rows additively after rebasing, rather than
rewriting the file. What the collision would have cost: duplicate ADR numbers
in a shared tree, a manual renumber, and broken cross-references from
documents already citing 0008/0009 — ADR 0002 itself now says "partially
superseded by ADR 0008 ... and ADR 0009," a cross-reference that a renumber
would have had to chase down and fix.

### 2. A semantic conflict on `project_id` that both sides' tests would have passed

The strongest example in the record. Session B's review raised a finding:
`PromotionQueueMetrics` (`src/AiRaccoon/Observability/PromotionQueueMetrics.cs`)
tags `project_id` on the four instruments behind `RecordQueued`,
`RecordEviction`, `RecordPromoted`, and `RecordDiscarded` — unbounded
cardinality. B was about to strip those tags. Session A was simultaneously
building an OTLP exporter (ADR 0009) that exports that exact meter
(`AiRaccoon.PromotionQueue`) and depends on that dimension being present.

Neither change breaks the other's tests. The merge would have been green and
wrong — an exporter faithfully shipping a metric whose useful dimension had
just been deleted underneath it. File-level disjointness is not semantic
disjointness, and CI cannot catch this class of bug.

### 3. A distinction that only emerged from disagreement

Session A had ruled that `project_id` exports in plaintext (no hashing),
superseding ADR 0002's future-evolution item 3. Session B pointed out that its
cardinality objection and A's plaintext objection were two different
objections that happened to land on the same tag, and only the plaintext one
had been superseded — declining to let "ADR-0002 is superseded" quietly cover
both. ADR 0009 records the distinction explicitly: *"This is a separate
question from the plaintext/hashing one settled below: cardinality is about
cost, plaintext is about disclosure, and only the latter was superseded by
this ADR."*

That correction produced two concrete results, both visible in the current
tree:

- It exposed a factual error in `SECURITY.md` (fixed in commit `bdc732d`,
  "docs: correct the metrics project_id claim and record the export
  cardinality cost"): an earlier version generalized from `ToolCallMetrics` to
  the whole metrics surface. The current `SECURITY.md` carries a per-meter
  table instead (chosen because a table cannot be silently generalized from
  one meter to all of them):

  | Meter | Carries `project_id`? |
  |---|---|
  | `AiRaccoon.MemoryTools` (tool calls) | No |
  | `AiRaccoon.PromotionQueue` | Yes, on the queued/eviction/promoted/discarded instruments |
  | `System.Runtime` (built-in) | No |

- It surfaced a consequence neither session had connected on its own: the
  cardinality cost was accepted when collection was local-only over
  EventPipe, where an unbounded dimension is free. Exporting over OTLP
  changes the cost basis — every distinct project becomes its own billable
  time series in a hosted collector. Same tag, same code, different trade.
  Session B reopened it with the project owner as an amendment rather than
  deciding unilaterally.

### 4. A measurement one session paid for and the other reused

Session A measured that two `dotnet test` runs in the same worktree corrupt
each other: a run that raced a concurrent rebuild reported 48 failures; the
clean re-run of the identical tree reported 1421 passed / 1 failed (that one a
genuine pre-existing timing flake in `WatchPipelineTests`, which passes 1/1 in
isolation). Cost: a wasted ~9-minute run and a nearly-believed false signal.

Session B had independently hit `MSB4166` "child node exited prematurely"
errors and a 420-second build timeout across its four lanes, and on receiving
A's measurement moved all lanes to targeted `--filter` runs with a single
combined full-suite gate scheduled for a quiet machine. B's own line is
quotable: separate worktrees were necessary but not sufficient — they prevent
file collisions, not CPU and MSBuild-node collisions.

### 5. A trap propagated before anyone else hit it

Session A found that in System.CommandLine 2.0.10 (pinned in
`Directory.Packages.props:45`), giving the existing leaf command `serve` a
subcommand (`observability`) makes the bare `ai-raccoon serve` fail to parse
with `"Required command was not provided."` Because `CliArgs.TryParse` returns
false on any parse error, every `serve` invocation would have exited 1. The
fix and its explanation are in
`src/AiRaccoon/Setup/Cli/CliCommandTree.cs:291-305`:

```
// Adding the observability subcommand makes System.CommandLine require one of
// serve's subcommands unless serve declares its own action — without this, a bare
// "ai-raccoon serve" fails to parse ("Required command was not provided.").
serve.SetAction(_ => ExitCode.Success);
```

One line, pinned by a regression test. Session B had queued items adding
subcommands to `sync`, `watch`, `encryption`, `extract`, and `maintenance`;
each brief now carries the one-liner. Session A paid the discovery cost once;
B got it for free, five times over.

### 6. Scope narrowed to avoid a live editor

Session B's review found `EventId` collisions wider than briefed. Confirmed in
the current tree — `EventId` values 1/2/3 are reused, unqualified, across six
files:

| File | EventIds reused |
|---|---|
| `src/AiRaccoon/Program.cs` | 1, 2, 3 |
| `src/AiRaccoon/HostExtensions.cs` | 2 |
| `src/AiRaccoon/Setup/McpServerSetup.cs` | 1 |
| `src/AiRaccoon/Setup/EmbeddingAvailability.cs` | 1, 2 |
| `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs` | 1, 2, 3 |
| `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs` | 1, 2, 3 |

Because A was actively editing `McpServerSetup.cs`, B fixed only its own two
collisions and deferred the cross-cutting renumber to a documented follow-up,
while A declared its own new range above `ServeRunner`'s existing 601/602/603/605
(`src/AiRaccoon/Setup/Serve/ServeRunner.cs:260-269`) rather than reusing
numbers B might also touch. Coordination as deliberate scope reduction, not
just conflict avoidance.

### 7. A decision validated retroactively by the other session's finding

ADR 0009 rejected a settings-table configuration channel for OTLP; one stated
reason is that `OTEL_EXPORTER_OTLP_HEADERS` carries collector API keys that
must never land in the bank ("Secrets stay out of the bank," ADR 0009 §
Configuration channel). Session B then reported that cloud sync had been
uploading the entire settings table — S3 `secretKey`, Azure
`connectionString`, embedding `apiKey` — to the object store those very
credentials unlock (its `task/sync-strip-settings` lane fixes this). A's
rationale was written before the leak was known; B's finding made it load-bearing
after the fact.

### 8. The one that went wrong — and why it still counts as a success

The other seven are saves. This one is a collision that actually happened, and
it is the most instructive entry here.

Session B told its lanes to stop building, to relieve a machine at load 214.
One of its agents complied by running `pkill -f "dotnet build"` and
`pkill -f "dotnet test"`. Those are unscoped pattern matches, not PID-targeted,
so they killed **every** matching process on the machine — including session
A's in-flight compile, around 14:57–14:58.

Three things are worth separating.

**The agent was not wrong; the instruction was.** "Kill any build you have
running" contains no scope, and `pkill -f` is the obvious reading of it in a
single-session world. The instruction was correct in the environment it
imagined and careless in the one it met — the same shape as every other failure
in this note: something asserted without being checked against the actual
context.

**The lost compile was not the real exposure.** The dangerous outcome was
session A's agent misdiagnosing a killed build as a code failure and "fixing"
correct code to satisfy an error that never existed. That is expensive and
near-undetectable afterwards, because the resulting change looks like a
considered fix. It was averted only because the warning arrived before the
agent reported its gate — A's agent was told to void every result from that
window and re-run clean.

**The protocol's real test is not collision avoidance.** B's agent self-reported
the `pkill` immediately and unprompted; B relayed it within a minute, unasked
and against its own interest. Nothing detected the kill — no lock, no monitor,
no log. The only reason it was ever known is that the agent that caused it
said so.

The generalisable rule: **never pattern-match a kill or reap a shared daemon
while another session may be using it.** `pkill -f`, `dotnet build-server
shutdown`, `killall`, and anything touching a shared NuGet or MSBuild cache are
all the same hazard. Scope to PIDs you started. Both sessions then declined to
run `dotnet build-server shutdown` unilaterally for exactly this reason, even
with ~87 orphaned worker processes resident — the restraint being the lesson,
not the orphans.

## The memory-bank thread, told honestly

The memory-first gate forced a `memory_search` before repo grep, and that
search returned ADR 0002 — which listed "No OTLP / gRPC export" as an accepted
non-goal (confirmed in the current `docs/adr/0002-opentelemetry-observability.md`,
under "Non-Goals (explicit)"). Without that forced search, Session A would
plausibly have built the exporter and only discovered the contradiction at
review, or not at all.

Memory prevented an agent from silently overturning a ratified human decision.
The project owner later overrode that non-goal explicitly — recorded in ADR
0009's status line: *"Supersedes the 'No OTLP / gRPC export' non-goal of ADR
0002"* and its body: *"This ADR records that reversal on explicit owner
instruction, not as an agent's architectural judgment."* The override became a
recorded decision with provenance instead of an accident.

## Incident summary

| # | Incident | What it cost | What it saved |
|---|---|---|---|
| 1 | ADR numbering collision | One relocation, additive index edit | Duplicate ADR numbers, manual renumber, broken cross-refs |
| 2 | `project_id` tag conflict | A brief cross-session exchange | A green-but-broken merge: exporter shipping a deleted dimension |
| 3 | Plaintext vs. cardinality distinction | Re-litigating one line of ADR 0002 | A `SECURITY.md` factual error; surfaced an unnoticed OTLP cost-basis change |
| 4 | `dotnet test` corruption under concurrent rebuild | ~9 minutes, one false signal, in Session A | Four lanes in Session B moved to filtered runs before hitting the same wall |
| 5 | `serve` subcommand parse trap (System.CommandLine 2.0.10) | One investigation, in Session A | Five queued subcommand additions in Session B inherited the fix for free |
| 6 | `EventId` collisions across six files | B fixed 2 of its own, deferred the rest | A live-edit conflict on `McpServerSetup.cs` |
| 7 | Settings-table leak vs. OTLP header rationale | — | ADR 0009's "keep secrets out of the bank" rationale retroactively validated, and the actual leak surfaced |
| 8 | Unscoped `pkill` killed another session's compile | One lost build; a near-miss on "fixing" correct code to chase a phantom failure | Nothing — this one landed. Caught only by the offending agent's voluntary self-report |

## What made it work

- Opening with concrete file paths and a branch name, not a prose summary of
  intent.
- Distinguishing *landed* from *proposed* from *queued*.
- Marking owner decisions as owner decisions, so the other agent does not
  re-litigate them (see ADR 0009's explicit "not as an agent's architectural
  judgment" framing).
- Volunteering your own errors. Both sessions issued corrections — A reversed
  a stdio-symmetry decision (see ADR 0009's host-path table: stdio does not
  get the exporter) and corrected a factual claim about `dotnet-counters`
  defaults; B cancelled a queued change. B's line: it would rather have both
  corrections than a tidier earlier message.
- Stating what you are *not* touching. That is what let both sides move
  without locks.

### The one method that actually caught things

Every error corrected across the afternoon was caught the same way: **someone
re-read the source instead of trusting a claim about the source.** Not a
better-informed agent, not a more senior model — a fresh read.

The failures all have one shape too — a claim about a file made from something
other than the file:

| Claim | Made from | Caught by |
|---|---|---|
| "Metrics carry no `project_id`" | generalising from `ToolCallMetrics` | reading `PromotionQueueMetrics.cs` |
| "All five instruments are tagged" | session A's own brief, twice committed | a subagent reading the file rather than the brief |
| F-02 exposure "five instruments" (to the owner) | session B's review notes | session A's correction, then B re-reading source |
| Tool count 17 / 19 / 20 across five documents | each other | counting the actual tool surface (22) |
| "Delete these six `I*Commands` interfaces" | two independent expert reviewers | reading `ConfigCommands.cs`, where the static-dispatcher invariant sanctions them |

That last row is the sharpest, and it is session B's: **two independent expert
opinions agreeing did not make them right.** Agreement between agents that
share a premise is not corroboration — it is correlated error, and it reads
exactly like consensus. The only thing that broke it was opening the file.

Note the direction of B's own overstatement: it inflated the exposure in the
direction that argued for B's own finding. Worth flagging in any process built
on agent review — the errors that survive longest are the ones that flatter the
reviewer's thesis.

## Limits and failure modes

- Coordination was entirely voluntary and ad hoc. No lock, no registry, no
  arbiter. It worked because both sides chose to disclose; nothing enforced
  it.
- It was also late — contact came after both sides had already committed
  work. Earlier contact would have prevented the ADR numbering clash from
  ever being possible in the first place.
- A peer session is not an authority: the harness explicitly warns that a
  peer cannot grant permission escalation, and that treating a peer's message
  as owner approval is "permission laundering." Cross-agent trust has a hard
  ceiling.
- Neither session could see the other's transcript. Everything relied on
  accurate self-report, and a self-report is exactly as good as the
  reporting agent's own understanding — which, in the `SECURITY.md` case, was
  demonstrably wrong until challenged.

This is one afternoon, two sessions, one repository. The seven incidents above
support exactly what they show and no more.

## Verification notes

Code claims in this note were checked against
`.ai-badger/worktrees/add-pid-to-serve` on 2026-08-07. One correction against
the brief this note was written from: `PromotionQueueMetrics` is described
elsewhere (ADR 0009, `SECURITY.md`) as tagging "all five" of its instruments
with `project_id`. Reading `src/AiRaccoon/Observability/PromotionQueueMetrics.cs`
directly: the class exposes seven instruments total (an `UpDownCounter`, three
`Counter`s, two `Histogram`s, and an observable gauge), and only **four** —
the ones behind `RecordQueued`, `RecordEviction`, `RecordPromoted`, and
`RecordDiscarded` — actually carry a `project_id` tag; the wait-time histogram
is recorded untagged, and the eviction-score histogram is never recorded at
all. This note uses "four" throughout, and ADR 0009 and `SECURITY.md` were
corrected to match once this discrepancy surfaced.

Two things are worth keeping for the article, because they are the point rather
than a footnote. First, the "five" wording originated in session A's own brief
to this note's author and had already been committed to two documents — it was
caught only because a subagent read the source instead of trusting the brief.
Second, that read turned up a live defect nobody was looking for: the
`ai_raccoon_queue_evicted_score` histogram has no writer. `RecordEviction`
accepts a `victimScore` parameter and never records it, so the instrument
reports nothing, permanently. It was reported to the session that owns the
file rather than fixed across a lane boundary.

All other file paths, method names, EventId values, branch names, and commit
references above were confirmed present in the worktree as described.
