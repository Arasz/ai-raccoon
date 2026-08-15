# 0059. The layering guard the repo had already paid for

Date: 2026-08-15

Status: Accepted

## Context

`tests/Directory.Packages.props:18` pinned `TngTech.ArchUnitNET.xUnitV3` and **no project referenced
it**. The only mechanical layering guard in the repo was a missing `ProjectReference` on
`AiRaccoon.Core`, which catches an assembly-level leak and nothing else.

So every architecture finding of the 2026-08-14 review was invisible to CI — which is how they
accumulated at 0 warnings and a green suite.

## Decision

**Three rules, each watched fail against today's code before it was trusted.**

| Rule | Verdict when written |
|---|---|
| Core depends on no other project assembly | passed — the baseline, not a discovery |
| No type in Core references `System.Net.*` | **failed**, 4 dependencies |
| Every `[McpServerTool]` class's constructor parameters are ports | **failed**, 12 injections |

### Rule 2 was vacuous first, and passed against a violation I already knew about

Written the obvious way:

```csharp
ArchRuleDefinition.Types().That().ResideInAssembly(CoreAssembly)
    .Should().NotDependOnAny(ArchRuleDefinition.Types().That().ResideInNamespace("System.Net"))
```

**It passed.** That form matches against types *loaded into the architecture*, and only our own
three assemblies are loaded, so the target set was empty and the rule could return exactly one
answer. It is the review's own vacuous-gate failure mode, reproduced while building the gate meant
to end it — and it was caught only because the violation was known in advance and the rule was
expected to go red.

The working form reads each Core type's own dependency list, which ArchUnit records by name whether
or not the target was loaded. `Core_ReferencesNoNetworkingType_ScansANonEmptyTypeSet` is the guard on
the guard: a dependency scan over an empty type set passes for the same reason a broken one does.

## What the rules found, and the fixes

**Rule 2 — `AiRaccoon.Resilience.ResiliencePipelineFactory` lived in Core** and depended on
`HttpRequestException`, `HttpResponseMessage`, `HttpStatusCode` and `SocketException`. That is HTTP
retry policy, not domain. Its only two callers are `AiRaccoon`'s `ServerProbe` and
`AiRaccoon.Infrastructure`'s `AssetDownloader`; **nothing in Core used it**, so it moved to
`AiRaccoon.Infrastructure/Resilience/` with its namespace.

**Core drops a third-party dependency in the process.** `Polly.Core` was referenced by this one file,
so its `PackageReference` comes off `AiRaccoon.Core.csproj` — Core is back to the two the review
measured (FluentValidation and `System.Numerics.Tensors`).

**Rule 3 — 12 concrete injections across all 8 tool classes**, not the 8 the plan predicted:

```
MemoryTools.gate, PromotionTools.gate, QualityTools.gate, ShareTools.gate,
SweepTools.gate, SyncTools.gate, WatchTools.gate, WorkspaceTools.gate   -> ToolGate
ShareTools.extraction  -> SharedExtractionRunner
SweepTools.sweeper     -> SweepService
SweepTools.knobs       -> ForgettingPolicyService
SyncTools.syncFactory  -> SyncCloudStoreFactory
```

**Every one of the four extra types already had an interface** — `ISharedExtractionRunner`,
`ISweepService`, `IForgettingPolicyService`, `ISyncCloudStoreFactory`. So all 12 were bypassing a
port that existed, needing no new abstraction and no design decision: the parameter types changed and
nothing else. `AddRequiredSingleton` registers each implementation under both its own type and its
interface, which is exactly why injecting the concrete class was as easy as injecting the port and
nothing reported the difference (H19).

## Consequences

- The three architecture classes the review found by hand now fail the build instead of a reviewer.
- `AiRaccoon.Core` no longer references Polly.
- **WP14 is subsumed.** It proposed registering only via the interface and fixing the compile errors,
  gated on owner question 10. Rule 3 gets the same result from the consumer side with no registration
  change, so the question is no longer blocking; narrowing `AddRequiredSingleton` remains available as
  a separate hardening.
- `IsPort` admits interfaces, `TimeProvider`, primitives and `string`. `TimeProvider` is a BCL
  abstraction shipped as a class; the rest are values no port could stand in for. A future concrete
  injection that is genuinely correct widens this predicate **with its reason**, rather than being
  excluded by name.

## Evidence

`tests/AiRaccoon.Tests/Unit/Layering/LayeringRulesTests.cs`. Both failing rules reported their
violations by name before the fixes — the four `System.Net` dependencies and all twelve injections
are quoted above from the red run. `Speed=Fast` 2158 passed; the MCP host, harness and refusal
suites (305 passed) confirm the container still resolves every tool now that they ask for ports.
