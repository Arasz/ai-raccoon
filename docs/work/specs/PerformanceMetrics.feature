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

  Rule: Every operation is measured, not a chosen subset
    # Owner: "in - we want a complete view."

  Rule: The MCP tool returns the report as JSON to the calling agent

  Rule: The CLI writes the report to a file and returns its path
    # Owner: "CLI? return path to saved report file - probably in the dir where CLI was accessed."

  Rule: Correlation dimensions are sampled periodically, not carried on every measurement
    # Owner picked the periodic bank-shape sample over per-row and per-checkpoint capture, and
    # selected all four dimensions: entry count, bank size in bytes, project count, and the
    # embedded / over-window fractions.
    # Measured cost per read on 2,518 rows (live bank ~6x): bank bytes 0.006 ms, entries 0.004 ms,
    # projects 0.075 ms, embedded 1.286 ms, over-window requires the tokenizer. Read once per flush
    # rather than per measurement, which is what makes the expensive ones affordable.

  # ---- Stage 03, round 2. Owner-stated. ----

  Rule: Metrics collection is always on
    # Owner: "no, always on." Deliberately unlike noise/sweep/queryguard, which all have kill
    # switches. A series with holes in it is worse than no series.

  Rule: Recording is best effort and never fails the operation being measured
    # Owner: "recording is a best effort only."

  Rule: Measurements leave the hot path through a channel and are written by a background reader
    # Owner: "use channels, save the metric to the channel then process them in the background. We
    # need to process them so we will not lose a lot of data if the process fails but not too often
    # so the hot paths are almost not affected."
    # => two competing budgets, both owner-stated: durability on crash vs hot-path cost.

  Rule: The channel is bounded and its memory cost is a stated budget
    # Owner: "we should consider memory pressure, so we should base on how metrics are collected
    # and processed."

  Rule: A measurement identifies its query by hash, never by text
    # Owner: "can we correlate by hash?" — yes. search_quality already stores the query text keyed
    # by correlation_id, so the metric row carries the hash AND the correlation_id: the hash groups
    # repeat queries, and the join recovers the text where the search was also quality-recorded.
    # No user content is duplicated into the metrics table.

  Rule: The report is project-scoped by default and can be asked for the whole bank
    # Owner: "lets go with project scoped, but we want to have get all data access too."
    # RESOLVED, stage 04 batch 2, owner: "8 - yes." => the whole-bank report is GATED behind the
    # elevated access mode, not merely a parameter. Project scope is the default AND the boundary:
    # crossing it is an access decision, the same shape the other cross-project surfaces already use.

  Rule: A checkpoint keeps the full statistics, not just an average, and records the version
    # Owner: "lets calculate all useful statistics, and we should probably correlate them with
    # version too - so we can pin changes to version."
    # => the year of history can answer "did p99 move in 1.15.0", not only "did the mean move".

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

  # ---- Stage 03, round 4. ----

  Rule: The channel holds at most 1000 measurements
    # Owner: "items, lets start from 1000, we will need to tune those numbers." => a setting, not a
    # constant, because the owner has said twice that these numbers are expected to move.

  Rule: A checkpoint records the commit, not the release
    # Owner: "commit." => ServerInfo's +sha suffix, so a regression pins to the change that caused
    # it rather than to the release that shipped it.

  Rule: The report window and bucket are the caller's choice, defaulting to the last 3 hours in 1-minute buckets
    # Owner: "let the client specify with a default for last 3h of data with 1 minute bucket."
    # => 180 points at the default, which is a plottable series rather than a raw dump.
    # RESOLVED, stage 04 batch 2, owner: "10 - we just limit bucket to the window - so we return one
    # measurement averaged over window." => a bucket wider than the window is NOT an error and NOT
    # rejected: it is CLAMPED to the window, and the report is a single point averaging it. The
    # caller always gets a series, never a validation failure, and never a partial trailing bucket
    # wider than the data it covers.

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

  Rule: The metrics subsystem measures itself, without going through itself
    # Owner: "also we want to save metrics for this system." => channel occupancy and pressure,
    # dropped count, flush duration and batch size, checkpoint duration, table growth.
    # RESOLVED, owner: (a) — self-metrics BYPASS the channel and are written directly by the
    # flusher, which already holds a write. Cannot recurse by construction: a subsystem measuring
    # itself through itself feeds itself, and the feedback is worst exactly when the channel is full
    # — the moment the numbers matter most and the drop counter starts lying about what filled it.

  # ---- Stage 04, batch 1. Titles proposed by me at the owner's request, then corrected by them.
  # Step-less on purpose: this is the queue stage 05 fills, and what spec_holes.py counts. ----

  Rule: Retention scenarios
    Scenario: A three-week-old bank still answers for its oldest measurement
    Scenario: A bank holding more than four weeks is within contract, not over it
    Scenario: A failed checkpoint leaves the hot table unpruned

  Rule: Checkpoint cap scenarios
    Scenario: The twenty-fourth checkpoint is written and none is discarded
    Scenario: The twenty-fifth checkpoint discards the oldest, not the newest

  Rule: Coverage scenarios
    Scenario: Every tool on the MCP surface produces at least one measurement when called
    Scenario: An operation added since the last release has recorded nothing yet
    Scenario: A tool that records no measurement fails the coverage test

  Rule: Channel scenarios
    Scenario: A burst within capacity is flushed whole
    Scenario: Exactly one thousand measurements arrive between two flushes
    Scenario: A burst beyond capacity drops, and the drop count reports how many

  # ---- Stage 04, batch 2. Same mode: titles proposed, owner shot down what was wrong.
  # Owner answered 8 (gated) and 10 (clamped); the other ten stood as proposed. ----

  Rule: Self-metrics scenarios
    Scenario: A flush records its own duration without enqueueing anything
    Scenario: Self-metrics are written even when the channel is full

  Rule: Best-effort recording scenarios
    Scenario: A search succeeds when the metric write throws
    Scenario: A failed metric write is not retried into the caller's latency

  Rule: Query identity scenarios
    Scenario: Two runs of the same query share a hash
    Scenario: No metric row contains query text

  Rule: Report scope scenarios
    Scenario: A report defaults to the calling project
    Scenario: A whole-bank report requires the elevated access mode

  Rule: Report window scenarios
    Scenario: A caller asking for nothing gets three hours in one-minute buckets
    Scenario: A bucket wider than the window is clamped to one averaged point

  Rule: Correlation dimension scenarios
    Scenario: A measurement carries no dimensions of its own
    Scenario: Bank shape is sampled once per flush, not once per measurement
