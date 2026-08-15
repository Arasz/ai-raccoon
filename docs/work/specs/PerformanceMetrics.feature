# DRAFT — elicitation in progress (create-task-spec). Not a contract until the decision gate is ruled.
# Stage 02 answers recorded verbatim from the owner, 2026-08-15.

@draft
Feature: Performance metrics for AiRaccoon's own development

  As a developer of AiRaccoon
  I want AiRaccoon to measure its own performance and store the measurements
  So that I can tell whether a change actually worked, and get data that points at the next improvement

  # Owner, stage 02:
  #  Q1 who      -> "use both of them as the insight into performance for us (devs). So unified
  #                  response, probably json file. Data for visualization — timeseries how
  #                  performance changed over time, or correlations with the size / amount of data /
  #                  projects etc."
  #  Q2 what     -> "I think I answered it in 1."
  #  Q3 benefit  -> "decide a) if our changes are really working as expected, b) get data to
  #                  improvements. For other users - for now? Nothing? Maybe tuning for the project?"
  #
  # Recorded consequences of those answers, to be turned into Rules in stage 03:
  #  - The audience is US, not end users. Scope boundary, owner-stated.
  #  - "Timeseries over time" makes retention a FEATURE requirement, not just hygiene.
  #  - "Correlations with size / data / projects" means each measurement must carry the context
  #    it is to be correlated against. Which dimensions is an open question.

  # ---- Stage 03, round 1. Owner-stated; wording mine, decisions theirs. ----

  Rule: A measurement series survives long enough to be a series, and the store stays bounded
    # Owner: "checkpointing hot table every 2 weeks (so we will store 4 weeks of data, checkpoint
    # first 2 - we will always have 2 weeks of history), then we will save checkpoint into
    # checkpoint table - this can be just limited to 24 entries. This will give us details in the
    # 'current' scope and long running history."
    # => hot table: rolling 4 weeks of raw measurements, so >= 2 weeks of detail is always present.
    # => checkpoint table: rolled-up summaries, capped at 24 rows (~1 year at a fortnightly cadence).
    # AMENDED, stage 04: the four weeks is a BEST-EFFORT limit, not a guarantee. Holding more than
    # four weeks is acceptable and is not a violation — owner: "we can hold more than four weeks,
    # this is best effort limit."
    # AMENDED, stage 04: the checkpoint is written FIRST and the prune runs only if it succeeded —
    # owner: "we first do a checkpoint then prune only on success." Pruning on a failed checkpoint
    # destroys the only copy of the window it was summarising.
    # AMENDED, stage 04 batch 3, owner: "I think we could align checkpoints with versions - so we
    # always start checkpoint on the version." => the cadence is NOT purely fortnightly. A new
    # version STARTS a checkpoint, so a checkpoint's window is bounded by version boundaries.
    # Consequence to carry forward: with a release-driven cadence, the 24-row cap is no longer
    # "~1 year" — that gloss assumed a fixed fortnight. How far back 24 checkpoints reach now
    # depends on release frequency, and that is a fact about the release train, not a bug.
    # ADDED, stage 05 batch 1, owner: "retention should be a background maintenance pass." The gap
    # was found by writing a step — `When the retention pass runs` named something no rule had
    # decided. So: checkpoint-and-prune is a BACKGROUND MAINTENANCE PASS, not work the flusher does
    # on its way past. This keeps it off the write path the flusher is already rate-limited on, and
    # it is a different actor from the self-metrics writer (see the self-instrumentation rule),
    # which does write directly.

    Scenario: A three-week-old bank still answers for its oldest measurement
      Given a bank whose oldest measurement is 21 days old
      When I call memory_performance for a window of 28 days
      Then the report covers that measurement
    Scenario: A bank holding more than four weeks is within contract, not over it
      Given a bank holding 40 days of measurements
      When I call memory_performance for a window of 40 days
      Then the report covers all 40 days
    Scenario: A failed checkpoint leaves the hot table unpruned
      Given a hot table holding four weeks of measurements
      And a checkpoint write that fails
      When the background maintenance pass runs
      Then a report over the oldest two weeks still returns them
    Scenario: The twenty-fourth checkpoint is written and none is discarded
      # The trigger is deliberately unnamed. A checkpoint can start because a fortnight elapsed or
      # because a version was cut, and the cap behaves identically either way — owner, stage 05:
      # "keep the checkpoint step vague". Naming one trigger would pin behaviour nobody decided.
      Given a checkpoint table holding 23 checkpoints
      When a checkpoint is written
      Then the report lists 24 checkpoints
    Scenario: The twenty-fifth checkpoint discards the oldest, not the newest
      Given a checkpoint table holding 24 checkpoints
      When a checkpoint is written
      Then the report lists 24 checkpoints
      And the checkpoint that was oldest is gone
      And the one just written is present

  Rule: Every operation is measured, not a chosen subset
    # Owner: "in - we want a complete view."
    # MOVED, stage 05 batch 2, owner: "move scenario 3 to specs." The proposed scenario 'A tool that
    # records no measurement fails the coverage test' described OUR TEST SUITE, not the product --
    # its `When` was "the coverage test runs", which nothing in AiRaccoon can trigger. It becomes a
    # named GATE in spec.json instead: a tool-call integration test asserting every tool on the
    # derived inventory produces at least one measurement, watched red by silencing one tool.
    # The rule is unchanged; only where its enforcement is recorded moved.

    Scenario: Every tool on the MCP surface produces at least one measurement when called
      Given the derived tool inventory
      When I call each tool once
      Then each one has recorded at least one measurement
    Scenario: An operation added since the last release has recorded nothing yet
      Given a tool that has never been called
      When I call memory_performance
      Then the report has no series for that tool

  Rule: The MCP tool returns the report as JSON to the calling agent
    # RESOLVED, stage 04 batch 3, owner: "2. yes." => a window with no measurements in it is an
    # EMPTY SERIES, not an error. An agent asking about a quiet bank gets a well-formed report
    # saying nothing happened, which is an answer; an error would be indistinguishable from the
    # tool being broken.

    Scenario: An agent calling memory_performance gets JSON, not prose
      When I call memory_performance
      Then the response parses as JSON
      And it carries a series for each measured operation
    Scenario: A window with no measurements is an empty series, not an error
      Given a bank with no measurements in the last hour
      When I call memory_performance for a window of one hour
      Then the report is an empty series
      And the tool does not error

  Rule: The CLI writes the report to a file and returns its path
    # Owner: "CLI? return path to saved report file - probably in the dir where CLI was accessed."
    # ADDED, stage 05 batch 2, owner: "lets fallback to tmp." Found by writing the steps: a rule that
    # promises a path back has to say what happens when the invocation directory cannot be written.
    # An unwritable directory falls back to the temp directory rather than erroring — the report is
    # the point, and its location is not worth failing the command over. The path returned is always
    # where the file actually went, never where it was meant to go.

    Scenario: The CLI prints a path and the file exists at it
      When I run ai-raccoon performance
      Then it prints a path
      And a report file exists at that path
    Scenario: The report lands in the invocation directory, not the data root
      Given I am in a directory that is not the data root
      When I run ai-raccoon performance
      Then the report file is in that directory

    Scenario: A CLI run in an unwritable directory falls back to the temp directory
      Given I am in a directory that cannot be written
      When I run ai-raccoon performance
      Then the report file is in the temp directory
      And the printed path is where the file actually went

  Rule: Correlation dimensions are sampled periodically, not carried on every measurement
    # Owner picked the periodic bank-shape sample over per-row and per-checkpoint capture, and
    # selected all four dimensions: entry count, bank size in bytes, project count, and the
    # embedded / over-window fractions.
    # Measured cost per read on 2,518 rows (live bank ~6x): bank bytes 0.006 ms, entries 0.004 ms,
    # projects 0.075 ms, embedded 1.286 ms, over-window requires the tokenizer. Read once per flush
    # rather than per measurement, which is what makes the expensive ones affordable.

    Scenario: A measurement carries no dimensions of its own
    Scenario: Bank shape is sampled once per flush, not once per measurement

  # ---- Stage 03, round 2. Owner-stated. ----

  Rule: Metrics collection is always on
    # Owner: "no, always on." Deliberately unlike noise/sweep/queryguard, which all have kill
    # switches. A series with holes in it is worse than no series.

    Scenario: No setting turns measurement off
      # RULED as proposed, stage 05 batch 3 ("all good"), with its weakness stated rather than
      # hidden: this proves ONE setting combination does not disable measurement, not that no
      # setting can. The stronger form derives the settings list and asserts none of them disables
      # it -- rejected here because that is a structural test about our code, the same species the
      # coverage check was moved to spec.json for. Behavioural and weak beats structural in a
      # .feature; if the weakness bites, the derived form belongs in spec.json beside the other gate.
      Given metrics have been recorded
      When I set every metrics setting to its most restrictive value
      Then a subsequent memory_search still records a measurement

  Rule: Recording is best effort and never fails the operation being measured
    # Owner: "recording is a best effort only."

    Scenario: A search succeeds when the metric write throws
      Given a metrics writer that throws on every write
      When I call memory_search
      Then the search returns its results
    Scenario: A failed metric write is not retried into the caller's latency
      # Internal-state assertion, chosen deliberately. The behavioural alternative -- "the search
      # takes no longer than one against a healthy writer" -- is a timing comparison, and a timing
      # assertion in CI is the kind that goes flaky and then gets muted, which is worse than an
      # honest structural one.
      Given a metrics writer that throws on every write
      When I call memory_search
      Then the failed write is not attempted again

  Rule: Measurements leave the hot path through a channel and are written by a background reader
    # Owner: "use channels, save the metric to the channel then process them in the background. We
    # need to process them so we will not lose a lot of data if the process fails but not too often
    # so the hot paths are almost not affected."
    # => two competing budgets, both owner-stated: durability on crash vs hot-path cost.

    Scenario: A search returns before its measurement is written
      Given a background reader that has not yet run
      When I call memory_search
      Then the search returns its results
      And the report does not yet include that search
    Scenario: No measurement is written on the caller's thread
      # Counted as bank writes during the call rather than as thread identity: the thread a write
      # lands on is an implementation detail, but a synchronous write is observable and is the thing
      # the rule exists to forbid.
      When I call memory_search
      Then no bank write happens during the call

  Rule: The channel is bounded and its memory cost is a stated budget
    # Owner: "we should consider memory pressure, so we should base on how metrics are collected
    # and processed."

    Scenario: A burst within capacity is flushed whole
      # The paused reader in these three is a fixture constraint doing real work, not scenery:
      # without it the arithmetic is only true if nothing drained meanwhile, and the scenarios would
      # be quietly timing-dependent.
      Given a channel holding 1000 measurements at most
      And the background reader is paused
      When 600 measurements arrive
      Then all 600 appear in the report after the next flush

  Rule: A measurement identifies its query by hash, never by text
    # Owner: "can we correlate by hash?" — yes. search_quality already stores the query text keyed
    # by correlation_id, so the metric row carries the hash AND the correlation_id: the hash groups
    # repeat queries, and the join recovers the text where the search was also quality-recorded.
    # No user content is duplicated into the metrics table.
    # REPHRASED, stage 05 batch 4, owner: "we could rephrase the query text case to - metric can
    # contain only (hash, correlationId) - this will be enforced on save."
    # => the rule is a POSITIVE ALLOWLIST, not a prohibition: a measurement's query identity is
    # EXACTLY {hash, correlation_id}, and the constraint is enforced where the row is written.
    # This is strictly better than what stage 05 batch 3 recorded. That version asserted "no metric
    # row contains query text" -- an unprovable negative that needed a table sweep, and the only
    # scenario in this file to inspect internal state. It also failed OPEN: a sweep passes until the
    # day something writes text, and then it has already been written. An allowlist enforced on save
    # fails CLOSED and needs no exception. The batch 3 exception is retired.

    Scenario: Two runs of the same query share a hash
      Given I have run the same query twice
      When I call memory_performance
      Then both measurements carry the same query hash
    Scenario: A measurement carries only the hash and the correlation id
      When I record a measurement for a query
      Then its query identity is exactly the hash and the correlation id

    Scenario: A measurement carrying query text is rejected on save
      When a measurement carrying query text is saved
      Then the save is rejected

  Rule: The report is project-scoped by default and can be asked for the whole bank
    # Owner: "lets go with project scoped, but we want to have get all data access too."
    # RESOLVED, stage 04 batch 2, owner: "8 - yes." => the whole-bank report is GATED behind the
    # elevated access mode, not merely a parameter. Project scope is the default AND the boundary:
    # crossing it is an access decision, the same shape the other cross-project surfaces already use.

    Scenario: A report defaults to the calling project
    Scenario: A whole-bank report requires the elevated access mode

  Rule: A checkpoint keeps the full statistics, not just an average, and records the version
    # Owner: "lets calculate all useful statistics, and we should probably correlate them with
    # version too - so we can pin changes to version."
    # => the year of history can answer "did p99 move in 1.15.0", not only "did the mean move".

    # The batch-1 leftover, ruled at last: "we discard the oldest, versions doesn't matter" — so the
    # proposed 'A checkpoint is discarded while it is the only record of its commit' is DROPPED, not
    # deferred. Discarding is by age alone; no commit or version protects a row from the cap.
    Scenario: A checkpoint answers for p99, not only the mean

  # ---- Stage 03, round 3. ----

  Rule: A full channel drops the incoming measurement and counts the drop
    # Owner chose DropWrite + TryWrite over DropOldest and over relying on the default.
    # Verified against the official docs rather than from memory: BoundedChannelFullMode.Wait is the
    # DEFAULT (value 0) and applies back-pressure to the writer, which rule "recording never fails
    # the operation" forbids. TryWrite returns false immediately even under Wait, so a non-blocking
    # hot path is achievable without changing FullMode — rejected because the guarantee would live
    # at the call site, where a later WriteAsync silently reintroduces back-pressure.
    # Channel.CreateBounded's itemDropped callback overload makes the drop counter a registration
    # rather than bookkeeping, which is how "drops must be visible" is satisfied.

    Scenario: A burst beyond capacity drops, and the drop count reports how many
      Given a channel holding 1000 measurements at most
      And the background reader is paused
      When 1200 measurements arrive
      Then the report counts 200 drops

  # ---- Stage 03, round 4. ----

  Rule: The channel holds at most 1000 measurements
    # Owner: "items, lets start from 1000, we will need to tune those numbers." => a setting, not a
    # constant, because the owner has said twice that these numbers are expected to move.

    Scenario: Exactly one thousand measurements arrive between two flushes
      Given a channel holding 1000 measurements at most
      And the background reader is paused
      When 1000 measurements arrive
      Then all 1000 appear in the report after the next flush
      And no drop is counted

  Rule: A checkpoint records the commit, not the release
    # Owner: "commit." => ServerInfo's +sha suffix, so a regression pins to the change that caused
    # it rather than to the release that shipped it.
    # RESOLVED, stage 04 batch 3, owner: "we want commit, version and timestamp of the commit. And
    # all entries per the window." => the checkpoint's identity is the TRIPLE (commit, version,
    # commit timestamp), and it summarises every entry in its window.
    # This dissolves the objection raised with the question: I argued "the commit" was ill-defined
    # because a fortnight spans dozens of them. Version-alignment (see the retention rule) removes
    # the ambiguity — a checkpoint starts at a version, so its commit is the one that version was
    # cut at, not an arbitrary one from the middle of the window.

    Scenario: A checkpoint records the commit, its version, and the commit timestamp
    Scenario: A new version starts a checkpoint before the fortnight is up

  Rule: The report window and bucket are the caller's choice, defaulting to the last 3 hours in 1-minute buckets
    # Owner: "let the client specify with a default for last 3h of data with 1 minute bucket."
    # => 180 points at the default, which is a plottable series rather than a raw dump.
    # RESOLVED, stage 04 batch 2, owner: "10 - we just limit bucket to the window - so we return one
    # measurement averaged over window." => a bucket wider than the window is NOT an error and NOT
    # rejected: it is CLAMPED to the window, and the report is a single point averaging it. The
    # caller always gets a series, never a validation failure, and never a partial trailing bucket
    # wider than the data it covers.

    Scenario: A caller asking for nothing gets three hours in one-minute buckets
    Scenario: A bucket wider than the window is clamped to one averaged point

  Rule: The background reader flushes on channel pressure, rate-limited to one flush per 4 seconds
    # Owner: "calculating the pressure in the channel with a time limit - we will aim at 60% of the
    # capacity flush by default, pressure will decrease aim (so if a lot of data is coming we want to
    # make more space) but we will limit flush period - to at most 4 seconds? And also every 30
    # second if aim target was not reached. I think we will adjust it."
    # AGREED: flush when the channel reaches an occupancy aim, default 60% of capacity; rising
    # pressure LOWERS that aim so a burst drains sooner; all values are settings.
    # RESOLVED, owner: "floor - i was thinking about rate limiting." => 4 seconds is the MINIMUM
    # interval between flushes, protecting the bank from write amplification under a burst. It is
    # not a deadline on a pressured batch. Consequence to carry into scenarios: between two flushes
    # the channel can still fill, so the rate limit and the drop counter interact — a burst that
    # exceeds 1000 items in 4 seconds drops, by design, and the drop count is how that is seen.
    # RESOLVED, stage 05 batch 4, owner: "pressure - arrival rate." Found by writing a step that
    # could not be written: "measurements arrive fast enough to raise the pressure" is unimplementable
    # until pressure is defined. It is the ARRIVAL RATE, not occupancy — which matters, because an
    # occupancy-based pressure would make "pressure lowers the aim" partly circular, the aim already
    # being an occupancy threshold. Arrival rate is a genuinely independent signal.

    Scenario: A second flush does not start within four seconds of the first
      Given a flush has just run
      When the channel reaches its aim one second later
      Then no flush runs until four seconds have passed since the first
    Scenario: Rising pressure lowers the aim, so a burst flushes below sixty percent
      Given an aim of sixty percent of capacity
      When measurements arrive at a rising rate
      Then the flush runs before the channel reaches sixty percent
    Scenario: An idle bank flushes after thirty seconds with the aim unreached
      Given a channel holding fewer measurements than the aim
      When thirty seconds pass with no further arrivals
      Then a flush runs

  Rule: The metrics subsystem measures itself, without going through itself
    # Owner: "also we want to save metrics for this system." => channel occupancy and pressure,
    # dropped count, flush duration and batch size, checkpoint duration, table growth.
    # RESOLVED, owner: (a) — self-metrics BYPASS the channel and are written directly by the
    # flusher, which already holds a write. Cannot recurse by construction: a subsystem measuring
    # itself through itself feeds itself, and the feedback is worst exactly when the channel is full
    # — the moment the numbers matter most and the drop counter starts lying about what filled it.

    Scenario: A flush records its own duration without enqueueing anything
      When a flush completes
      Then the report carries its duration
      And no measurement was enqueued to record it
    Scenario: Self-metrics are written even when the channel is full
      Given a channel that is full
      When a flush runs
      Then the report carries the flush duration
