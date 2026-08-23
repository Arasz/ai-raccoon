# Stack — Azure Functions

No researched ruleset yet — see `governance.md` for the bar a rule must clear
before more are added here. Every L1 (`T1-*`) and L2 (`T2-*`) rule already
applies unchanged. One seed below is proven locally; severity is capped at
`minor` for every rule in a stub file regardless of the defect's real cost,
because it has not yet been through the full evidence/parent review a
researched stack file gets — the underlying risk (a silently disabled auth
binding) is not minor, the *proof bar cleared so far* is.

See `azure-functions.instructions.md` for the project's Durable Functions
conventions (deterministic orchestrators, bounded retries, managed identity) —
cited here rather than restated as a test rule.

#### `T3-FUNC-01` — A `%Key%` app-setting binding expression resolves from host config only, never the worker's `appsettings.json`; an unresolved binding is silently disabled.
- *design:* verify the binding's key exists in the **host's** configuration surface (`local.settings.json` / app settings), not the worker process's own config.
- *review:* a test asserting the binding fires proves nothing if the binding itself never resolved — check for silent disablement, not just absence of error.
- *check:* trigger the function through the real host config path at least once; a worker-config-only test cannot catch this.
- **severity:** minor (see note above on the stub severity cap) · **evidence:** strong · **flag:** argued
- *parent:* `T1-CST-05` · *cites:* `evidence.md` (functions-binding-expression-config)

## How to contribute a rule

Read `governance.md` §"Adding a rule" before adding anything: a stack rule needs a `parent:`
L1/L2 id, a real proven failure, and a falsifying `check:`.
