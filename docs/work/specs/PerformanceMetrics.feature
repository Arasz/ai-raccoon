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
