# Independent design review

Date: 2026-10-26. Reviewer: delegated code-reviewer in isolated checkout `/tmp/air-decision-graph-review`, initial commit `9c6a176`. Author performed research and planning; only review was delegated. Review covered model, API, CLI, ADR and implementation feasibility together.

## Findings and resolution

| Priority | Finding | Resolution |
|---|---|---|
| MUST | Versions omitted change reason; edge removals omitted task/reason/actor/session | Added version change_reason and all-or-none removal provenance fields with task FK. Edge touch selects source; supersession versions target too. Added remove-in-A/B restart/export test |
| MUST | All task headers cannot fit a bounded preview | Display headers within budget; include selected/shown/omitted task counts and footer in budget. Added many-empty-tasks/Unicode test |
| MUST | Alias fold cannot reconcile independent task orders/revision namespaces | Simplified v1: refuse any occupied retired graph before repair mutates. Graph-free losers can fold without touching winner graph. Add two-graphs refusal test; graph merge deferred |
| SHOULD | Query selector default ambiguous | Query defaults all:true; export/preview default last:1 |
| SHOULD | Superseded target cannot be a boundary in the supersession task | Example uses dependency on an untouched older decision |
| SHOULD | “Lossless” omitted raw imports and referenced task metadata | Export includes relevant raw import records and referenced task stubs; explicitly limits completeness to selected native records and their provenance |
| SHOULD | Two test mutations altered verification rather than production behavior | Replace with process-local repository and access-guard bypass mutations |

The simpler-shape check retained a decision-only relational model and removed graph-history merge from v1. Full generic graph/RDF, new database, event bus, automatic summarization and task lifecycle duplication remain outside scope.

## Follow-up result

The same independent reviewer checked corrected commit `60fcc257` and reported no residual MUST findings. Model and API are ready for implementation handoff. The exported task-graph revision 1 has seven pending steps, unchecked runtime acceptance criteria, no cycles and one release/integration sink. This is design readiness only.

## Validation performed

Local document-link validation passed across all seven new Markdown documents. A simulated missing API target first made that check fail. JSON/DAG validation confirmed unique step identities, known dependencies, acyclicity, one sink and pending runtime status. `git diff --check` passed. No production code or tests changed; runtime build/test and mutation gates belong to the implementation plan. Key proposal was successfully registered in Semantica as decision `31da32fd-f81f-4ec1-ab3e-51a376c503f1`.

The planning PR remains draft for the proposed contract; runtime implementation and release are outside this planning unit.
