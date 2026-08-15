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
