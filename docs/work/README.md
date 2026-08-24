# work/

Dated work records: plans, designs, research, reviews, incidents, backlog. Every file is
named `YYYY-MM-DD-slug.md` and is a snapshot — it records what was true when it was
written, not what is true now.

## Contents

Active records:

| File | What it is |
|---|---|
| [2026-08-25-fast-lane-flakes-diagnosis.md](2026-08-25-fast-lane-flakes-diagnosis.md) | Diagnosis (task diagnose-fast-tests): the main-run fast lane failed on two documented flake classes — the pack test's MSBuild host crash (exit 134, untriagable because build-fast lacks the crash-dump env) and a port release→rebind race (both racers got PortInUse). Lane ran 14m31s+ vs 168s on the identical PR run: crashed-pack MSBuild zombies + co-tenant load at a 15-min budget. Fixes proposed, not yet implemented. |
| [2026-08-24-vec-code-unfix-dim-plan.md](2026-08-24-vec-code-unfix-dim-plan.md) | Plan (rev 1, review round 1 folded) + implementation record: `vec_code` dimension-agnostic via the generalized `VecDimensionReconciler` (WP1-WP5, task `task/vec-code-unfix-dim`). Owner-gated 2026-08-24; implementation on the same branch. |
| [2026-08-07-skill-discovery-mcp-server.md](2026-08-07-skill-discovery-mcp-server.md) | Active research, not yet implemented |
| [2026-08-22-s6a-fixture-replacement-design.md](2026-08-22-s6a-fixture-replacement-design.md) | Design + execution record for replacing the private `jsaa-memory.db` retrieval fixture with a corpus built from this repo's own public docs (ai-raccoon#414 S6a, ADR-0090). §9 amends the plan with what execution measured differently. |

Frozen, build-embedded Gherkin bundles — referenced by
`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`'s `ReqnrollFeatureFiles`; never move,
rename, or reformat these paths:

| Bundle | What it is |
|---|---|
| [features-agent-memory/](features-agent-memory/) | `agent-memory.feature` (29 scenarios) + `spec-issue-1.md` design rationale, linked from `docs/reference/agent-memory-server.md` |
| [features-native-memory/](features-native-memory/) | `native-memory.feature` + `spec.json` native-store scope |

`archive/` holds superseded sprint records — implemented plans, research whose
conclusions now live in an ADR or reference doc, and reports whose verdicts already
shipped. Kept for provenance (some are cited directly as ADR evidence trails), not for
day-to-day reading; consult the relevant ADR or `docs/reference/`/`docs/explanation/`
doc first.
