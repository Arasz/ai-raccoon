# The task-graph tools

The frozen surface is 12 tools. Their names, inputs and payloads are pinned by the transport
goldens; the skill teaches these and no others. Every call takes `task_id` — `plan_id` does
not exist anywhere in the I/O.

## The 12 tools

| Tool | Reach for it when |
|---|---|
| `plan_create` | After decomposing a brief into steps with acceptance criteria, when the plan should become the graph execution lanes dispatch from. Validates the DAG and persists revision 0; identical content is idempotent (`created:false`), different content under an existing `task_id` is `already-exists`. |
| `plan_replace` | Before execution starts, when review feedback changes the step set — replaces the whole plan under an `expected_revision` check. It refuses with `plan-in-progress` once any step left pending/skipped; replacing with identical content succeeds even on a stale revision and answers `replaced:false`. |
| `plan_get` | First read after a gap or handoff: counts, ready set, blocked steps and the current revision before any mutation (`include` is `summary`, `full`, `steps` or `state`). |
| `plan_export` | At a review or release boundary, to get the canonical stored plan document, schema URL included, as an artifact. |
| `step_get` | When one step's instructions, dependencies, blockers or criteria must be seen in full — cheaper than exporting the whole plan. |
| `step_start` | Take a ready step just before dispatching a lane so `in_progress` and `started_at` are recorded; after a failure this is a retry. |
| `step_complete` | When a lane's verification passed: record the evidence and per-criterion results that close the step; `force` is the only way past failed criteria and requires a reason. |
| `step_fail` | When a lane could not deliver: record the reason and evidence so dependents show blocked instead of silently waiting. |
| `step_skip` | When a step is deliberately not done (superseded, descoped): `skipped` still satisfies readiness, unlike `failed`. |
| `ac_check` | During verification, per acceptance criterion: record passed or failed with evidence while the step is `in_progress`; completion freezes it. |
| `steps_ready` | Before each wave dispatch: the ready frontier, the waves packed around file and resource conflicts, and steps blocked by failures. |
| `progress_checklist` | For a status report or checkpoint: per-step markers, criterion tallies, next and blocked; its `format:"text"` output is the status section verbatim. |

## Input shapes

Exact argument keys. A key without `?` is required; unknown keys are refused, never dropped.
`task_id` is required by every tool.

| Tool | Arguments |
|---|---|
| `plan_create` | `task_id`, `task_description_ref`, `steps[]`, `task_context?`, `research_ref?`, `loop` (`low`\|`high`), `source_refs?` |
| `plan_replace` | `task_id`, `expected_revision`, `task_description_ref`, `steps[]`, `task_context?`, `research_ref?`, `loop`, `source_refs?` |
| `plan_get` | `task_id`, `include?` (`summary`\|`full`\|`steps`\|`state`; default `summary`) |
| `plan_export` | `task_id` |
| `step_get` | `task_id`, `step_id` |
| `step_start` | `task_id`, `step_id`, `expected_revision`, `run_id?`, `force?`, `reason?` |
| `step_complete` | `task_id`, `step_id`, `expected_revision`, `evidence?`, `criterion_results?`, `note?`, `force?`, `reason?` |
| `step_fail` | `task_id`, `step_id`, `expected_revision`, `evidence`, `reason` |
| `step_skip` | `task_id`, `step_id`, `expected_revision`, `reason`, `evidence?` |
| `ac_check` | `task_id`, `step_id`, `ac_id`, `expected_revision`, `status` (`passed`\|`failed`), `evidence?`, `note?` |
| `steps_ready` | `task_id`, `waves?` (default `true`) |
| `progress_checklist` | `task_id`, `format?` (`json`\|`text`; default `json`) |

Nested shapes:

- `steps[]` — `id`, `goal`, `instructions`, `effort` (`low`|`medium`|`high`), `level?`,
  `model?`, `persona?`, `depends_on?` (default `[]`), `acceptance_criteria?` (default `[]`),
  `files?` (default `[]`), `resources?` (default `[]`). Ids are lowercase
  (`^[a-z0-9][a-z0-9._-]*$`); runtime state (status, evidence, timestamps) is the server's,
  never the caller's.
- acceptance criterion — `id`, `statement`, `check?`.
- evidence — `kind` (`command`|`test`|`artifact`|`review`|`note`), `summary`, `ref?`,
  `recorded_at?` (defaults to server time).
- `criterion_results` — an object mapping acceptance-criterion id to `passed`|`failed`.

`expected_revision` is the revision you last read. A stale value refuses with `conflict`;
every successful mutation returns the revision the next call must pass.

## A worked example, both ways

Save the plan object below as `plan.json`; the calls run in order. The step ids are lowercase
(`s1`, `s2`); the rendered plan file labels them `S1`, `S2` by position.

```json
{
  "task_id": "aib-demo",
  "task_description_ref": "docs/work/demo.md",
  "loop": "high",
  "steps": [
    {"id": "s1", "goal": "Add the endpoint",
     "instructions": "Own src/api.py; follow the frozen schema; report the diff.",
     "effort": "medium", "files": ["src/api.py"],
     "acceptance_criteria": [{"id": "ac1", "statement": "the endpoint answers 200",
                              "check": "pytest tests/test_api.py"}]},
    {"id": "s2", "goal": "Wire the client",
     "instructions": "Own src/client.py; call the endpoint; report the diff.",
     "effort": "medium", "depends_on": ["s1"], "files": ["src/client.py"],
     "acceptance_criteria": [{"id": "ac1", "statement": "the client handles a 200",
                              "check": "pytest tests/test_client.py"}]}
  ]
}
```

CLI:

```bash
CLI='uv run --script .ai-badger/skills/task-decomposition/scripts/task_graph_cli.py'
$CLI plan_create --json "$(cat plan.json)"
$CLI steps_ready --json '{"task_id": "aib-demo"}'
$CLI step_start --json '{"task_id": "aib-demo", "step_id": "s1", "expected_revision": 0}'
$CLI ac_check --json '{"task_id": "aib-demo", "step_id": "s1", "ac_id": "ac1", "expected_revision": 1, "status": "passed", "evidence": [{"kind": "test", "summary": "pytest tests/test_api.py", "ref": "8 passed"}]}'
$CLI step_complete --json '{"task_id": "aib-demo", "step_id": "s1", "expected_revision": 2}'
$CLI progress_checklist --json '{"task_id": "aib-demo", "format": "text"}'
```

MCP (pi) — the same six calls as the model makes them: the declared tool name plus the JSON
arguments the CLI passes to `--json` (the first call's arguments are the plan object above):

```text
mcp__task-graph__plan_create        <the plan object above>
mcp__task-graph__steps_ready        {"task_id": "aib-demo"}
mcp__task-graph__step_start         {"task_id": "aib-demo", "step_id": "s1", "expected_revision": 0}
mcp__task-graph__ac_check           {"task_id": "aib-demo", "step_id": "s1", "ac_id": "ac1", "expected_revision": 1, "status": "passed", "evidence": [{"kind": "test", "summary": "pytest tests/test_api.py", "ref": "8 passed"}]}
mcp__task-graph__step_complete      {"task_id": "aib-demo", "step_id": "s1", "expected_revision": 2}
mcp__task-graph__progress_checklist {"task_id": "aib-demo", "format": "text"}
```

Those names reach the model only when the `task-graph` entry in `.pi/mcp.json` carries
`"exposure": "direct"` — under the `codemode` default they stay undeclared, and codemode
scripts and `tool_search` reach undeclared tools.

`plan_create` answers `created:true` at revision 0; `step_start` returns 1, `ac_check` 2,
`step_complete` 3, and the checklist text reads:

```text
# aib-demo — 1/2 steps complete
- [x] S1 Add the endpoint (1/1 criteria)
- [ ] S2 Wire the client (0/1 criteria)
```

## The CLI twin

With no MCP host, the CLI takes the same 12 names and the same argument objects:

```bash
uv run --script .ai-badger/skills/task-decomposition/scripts/task_graph_cli.py <tool-name> --json <args>
```

`<tool-name>` is one of the frozen twelve; `<args>` is the JSON **object** the tool's input
model accepts. Exit 0 prints the payload, exit 1 prints the closed error envelope, exit 2 is a
usage error (unknown verb, unparseable `--json`, non-object `--json`). Payloads are the
server's by construction — the CLI loads the same handlers.

### The opt-in Jev advisory

With `AI_BADGER_JEV=1` (plus `AI_BADGER_JEV_TIER=1` and/or `AI_BADGER_JEV_WAVES=1`), the
advisory reads a plan document and prints its proposals:

```bash
uv run --script .ai-badger/skills/task-decomposition/scripts/jev_choice.py plan.json --both --json
```

It never blocks the plan: with the flags off it prints `{"status": "off"}` and touches no
network, and a failed call leaves the deterministic result standing. OpenRouter's endpoint
also needs `AI_BADGER_ALLOW_THIRD_PARTY=1` and no `dataPolicy` lock; a loopback
`AI_BADGER_JEV_ENDPOINT` does not (see `decomposition-method.md`). Tier proposals are
upgrades only; wave hints only add serialization. `references/decomposition-method.md`
explains how to fold them into the plan.

## Error codes (closed)

Only these codes are emitted; anything else is a bug, not a shape to handle:

`invalid-arguments`, `not-found`, `already-exists`, `conflict`, `invalid-transition`,
`dependencies-incomplete`, `criteria-unmet`, `plan-in-progress`, `schema-version-unsupported`,
`prerequisite-missing`, `config-error`, `store-error`.

The ones a planner meets first: `already-exists` (different content under a live `task_id` —
revise with `plan_replace`), `conflict` (stale `expected_revision`), `plan-in-progress`
(replace attempted after a step left pending/skipped), `dependencies-incomplete` (completion
before a dependency), `criteria-unmet` (completion with unchecked criteria — `force` is the
orchestrator-only way past it).

## Degraded path

No server listed and the CLI does not run means the graph is off. Write the plan file by hand
to `.ai-badger/task-tracking/plans/<YYYY-MM-DD>-<taskId>.md` in the frozen shape: one
`**S<N> …**` heading per step, one `- [ ]` per acceptance criterion, and the generated-banner
note `hand-written — graph off`. Say the graph is off, and never hand-write `tracking.db`.
Manual checkboxes do not enforce failed/forced/blocked or the join rule — those are graph-only
semantics; without the graph, checkbox state is the only status. Never `plan_create` over an
in-flight task that has no plan row: that is the legacy plan-file path, read and maintained by
hand.
