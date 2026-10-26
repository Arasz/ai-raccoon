# Domain model test design

Target: Core/Decisions lifecycle, selector, content and edge rules. Oracle: data-model.md lifecycle, task selection and typed-edge contracts. Pure unit tests use no clock, network, filesystem, environment, randomness or shared mutable state. Runner: `dotnet test --project tests/AiRaccoon.Tests --filter 'FullyQualifiedName~AiRaccoon.Tests.Unit.Decisions'`.

| Behavior | Failure caught | Red proof |
|---|---|---|
| Legal transition returns a new state | State remains unchanged or mutates original | Temporary TransitionTo returns this |
| All other transition pairs refuse | Self transition or terminal revival | Disable transition guard |
| Terminal states refuse amendments | History can be rewritten | Temporary EnsureCanAmend does nothing |
| Task selector defaults to last one, validates exclusive alternatives | Wrong export grouping or ambiguous selection | Temporary constructor accepts mixed/invalid selectors |
| Graph rejects self links and dependency cycles, permits contextual cycles | Invalid prerequisite graph or unnecessary rejection | Temporary edge policy skips validation |
| Content enforces bounds, valid confidence/dates and immutable collections | Bad persisted content or caller mutates stored value | Temporary validator bypasses bounds/copy |

Each temporary implementation is replaced before commit. Record actual runner results in the implementation evidence; no production coverage is claimed from this document alone.
