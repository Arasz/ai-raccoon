# kind-contract

Applies when two components deploy independently and a fake or a spec stands between them — a
consumer-driven contract, a bi-directional spec comparison, or a route/DTO derivation check. The
runner is whatever executes the contract against real provider code (never a mock of the
provider); where a fake stands in for the real backend elsewhere in the suite, that fake is the
same finding as `T1-DBL-01` and is pointed at rather than restated.

**`T2-CTR-01` — Two components that deploy independently need a contract test; two that always ship together do not.**
- *design:* before adding a contract test, name what could change on one side without the other redeploying.
- *review:* contract tests answer what integration tests cannot answer cheaply — will a provider change break a consumer before either deploys?
- *check:* do the two components deploy on independent release cadences (separate CI pipelines/deploy triggers)? Evidence: the deploy config — two components shipping from one atomic pipeline don't need this; two that deploy separately with no contract test between them is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-CST-02`
- *cites:* Vocke *Practical Test Pyramid* (CDC section).
- *meta:* pass=7 order=29

**`T2-CTR-02` — Consumer-driven by default; bi-directional spec comparison only when the provider is third-party or will not run verification.**
- *design:* reach for CDC first; fall back to spec comparison only when the provider genuinely cannot run your verification.
- *review:* CDC runs real provider code and catches behavioural issues a static spec comparison cannot.
- *check:* is a bi-directional spec-comparison test used against an in-house provider that could instead run CDC verification? Evidence: who owns the provider — spec comparison used by default against an in-house provider, with no attempt at CDC, is the violation.
- **severity:** minor · **evidence:** strong · **flag:** argued · **parent:** `T1-DBL-03`
- *cites:* PactFlow CDC-vs-BDCT comparison.
- *meta:* pass=5 order=21

**`T2-CTR-03` — The contract is verified against real provider code, never against a document or a mock of the provider.**
- *design:* wire the contract test to invoke the provider's real handler, not a description of it.
- *review:* a contract "verified" against a mock of the provider verifies the mock's author's beliefs, not the provider.
- *check:* `grep -rn 'Mock<IProvider>\|Substitute.For<IProvider>' <contract test dir>` — a contract test constructing a mock of the provider interface, rather than invoking the provider's real handler, is the violation.
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-DBL-01`
- *meta:* pass=1 order=10

**`T2-CTR-04` — Any in-process double standing in for a remote provider is bound by the same contract suite as the real client.**
- *design:* write the contract suite once, run it against both the double and the real client.
- *review:* this is where a fake's divergence hurts most, because nothing else is watching the process boundary.
- *check:* auto — for each remote-provider double, does the contract suite run against it too?
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-DBL-01`
- *cites:* `evidence.md` (fake-contradicts-production-backend).
- *meta:* pass=1 order=10

**`T2-CTR-05` — Where no contract test exists, a derivation check is the minimum: the client's route/DTO set is generated from, or diffed against, the server's.**
- *design:* wire a `--check` derivation script into the gate before assuming e2e or manual review will catch backend drift.
- *review:* a frontend e2e suite with every API call stubbed never touches the real API — it cannot be the line that holds against contract drift.
- *check:* auto — does a derivation script exist and run in CI?
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-CST-06`
- *cites:* `evidence.md` (test-suite-shape).
- *meta:* pass=6 order=27

**`T2-CTR-06` — A schema/contract test proves the contract rejects the wrong shape, not only that it accepts the right one.**
- *design:* for every discriminated union or constrained enum in the contract, assert at least one rejection case.
- *review:* a schema that accepts everything passes every acceptance-only test — the rejection case is the one that would have caught the drift.
- *check:* auto — a parity/schema test file with zero rejection assertions is a finding.
- *rationale:* `evidence.md` (schema-accepts-everything-no-rejection-test) — a parity suite pinning one field's accepted values with no rejection test shipped a selector the backend could not register.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-DBL-01`
- *meta:* pass=1 order=10
