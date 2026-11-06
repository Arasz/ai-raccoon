# Decision persistence test design

Oracle: data-model.md relational constraints and transaction/retention contracts. Real encrypted-bank connection factory, a unique temporary directory per test instance, fixed supplied instants and explicit IDs. No network, process environment changes or shared database names. Focused runner: `dotnet test --project tests/AiRaccoon.Tests --filter 'FullyQualifiedName~AiRaccoon.Tests.Integration.Decisions'`.

| Behavior | Failure caught | Mutation |
|---|---|---|
| Fresh and v22 banks gain tables and keep existing content | Missing migration or destructive schema change | Remove decision DDL from fresh/migration path |
| Reopen retains versions, edges and receipts | Session-local graph | Skip version/receipt insert |
| Project predicates and composite FKs isolate records/links | Cross-project leakage | Remove project predicate/endpoint FK |
| Write callback failure rolls back earlier writes | Partial supersession | Commit each write outside callback transaction |
| Read callback sees a consistent snapshot with a concurrent writer | Mixed export versions | Remove BEGIN read transaction |
| Graph tables absent from cloud snapshots on every push path | Local graph uploads | Remove decision-table strip entries |
| Edge removal retains task/reason/session | Task export misses link removal | Omit removal metadata update |

Schema-red evidence: opening a fresh bank before decision DDL produced 1 failing test because decision_projects was absent. Test cleanup uses TestData.DeleteTempRoot; cancellation comes from TestContext.Current.CancellationToken.
