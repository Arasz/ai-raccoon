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
