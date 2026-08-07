# Common File Layout

### Domain + Infrastructure + Tests

src/MyApp.Domain/ Feature/ MyAggregate.cs # sealed record with behavior MyStatus.cs # enum MyClassification.cs # value object record MyPolicy.cs # standalone policy (sealed class)
MyPolicyOptions.cs # options record for configurable thresholds MyDecision.cs # decision record + decision-type enum IMyRepository.cs # extension point interface IMyMonitor.cs # extension point interface MyStepTypes.cs # step type contracts
(if workflow)
MyInterventionSource.cs # constants for intervention sources

src/MyApp.Infrastructure/ Feature/ MyTransport.cs # transport DTO (sealed record, infra-only)
IMyTransport.cs # transport boundary interface (infra-only)
MyChannelMonitor.cs # implements IMyMonitor, uses IMyTransport MyTokenRefresher.cs # token lifecycle, raises intervention signals

tests/MyApp.Domain.Tests/ Feature/ MyAggregateTests.cs # behavior tests MyPolicyTests.cs # policy decision tests MyIntegrationTests.cs # cross-aggregate tests (e.g. intervention sources)

tests/MyApp.Infrastructure.Tests/ Feature/ FakeMyTransport.cs # test double for IMyTransport MyChannelMonitorTests.cs MyTokenRefresherTests.cs

```
