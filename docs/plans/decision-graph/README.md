# Native project decision graph

Reviewed design for AiRaccoon to own decision registration and graph retrieval. Model, storage (schema v23), service, adapters, CLI and migration are complete in PR #923, Semantica import is deferred to #924, and the release step (2.4.0) is in progress until its CI passes.

Read in order:

1. [Research and scope](research.md)
2. [Data model](data-model.md)
3. [Agent API and CLI](api.md)
4. [Independent design review](review.md)
5. [Implementation plan and test design](implementation.md)
6. [ADR-0142](../../adr/0142-native-project-decision-graph.md)

The implementation DAG is recorded in task-graph as `air-native-project-decision-graph-implementation`; its [exported document](implementation-plan.json) is a review snapshot. The exported task-graph snapshot records model and storage complete, service in progress, and the remaining four steps pending. The proposal includes committed project decisions, task-scoped lossless export and bounded compact previews. Workspace drafts, shared promotion and automatic cloud graph merging are deferred.
