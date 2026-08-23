# Stack — Cosmos DB

No researched ruleset yet — see `governance.md` for the bar a rule must clear
before more are added here. Every L1 (`T1-*`) and L2 (`T2-*`) rule already
applies unchanged. One seed below is proven locally; severity is capped at
`minor` for every rule in a stub file regardless of the defect's real cost,
because it has not yet been through the full evidence/parent review a
researched stack file gets.

See `cosmos.instructions.md` for the project's Cosmos conventions (partition
key from the write/query shape, single writer, ETag concurrency) — cited here
rather than restated as a test rule.

#### `T3-COSMOS-01` — The partition-key argument and a `WHERE c.userId`-shaped tenancy predicate are proven only against the real store, never a fake or an in-memory repository.
- *design:* name, in the test, the lane that proves the query text and partition-key argument against the real Cosmos emulator or account.
- *review:* this is `N-DATA-04`'s stack form — a port-contract test against a fake is an echo; a wrong partition key or predicate passes every fake test.
- *check:* grep the query method across unit fakes and integration suites — if only the fake references it, the query is untested.
- **severity:** minor (see note above on the stub severity cap) · **evidence:** strong · **flag:** auto
- *parent:* `T2-INTG-03` · *cites:* `evidence.md` (test-suite-shape); `cosmos.instructions.md`

## How to contribute a rule

Read `governance.md` §"Adding a rule" before adding anything: a stack rule needs a `parent:`
L1/L2 id, a real proven failure, and a falsifying `check:`.
