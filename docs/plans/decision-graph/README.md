# Native project decision graph

Proposed design for AiRaccoon to own decision registration and graph retrieval. Runtime implementation has not started.

Read in order:

1. [Research and scope](research.md)
2. [Data model](data-model.md)
3. [Agent API and CLI](api.md)
4. [Independent design review](review.md)
5. [Implementation plan and test design](implementation.md)
6. [ADR-0142](../../adr/0142-native-project-decision-graph.md)

The implementation DAG is recorded in task-graph as `air-native-project-decision-graph-implementation`; its [exported document](implementation-plan.json) is a review snapshot. All runtime steps remain pending. The proposal includes committed project decisions, task-scoped lossless export and bounded compact previews. Workspace drafts, shared promotion and automatic cloud graph merging are deferred.
